# Verbs on pull requests and the validation flow: open-pr, update-pr, pr-checks, wait-for-checks, validation-verdict.
# The merge verb is in Verbs.Merge.ps1.

# The item's pull request of a Copilot owner task (docs/design/copilot-cloud-agent.md, "One task, resumed"): always the open pull request of the
# owner task's current branch (copilot_task.branch). A recorded pull request that was closed or superseded (a migration, a takeover) is replaced
# by it in the state (pr, copilot_task.pr, awaiting_checks). Every owner pull request targets the default branch: one stacked on another branch is
# retargeted once that base has no open pull request (merged or closed); while the base's pull request is still open, the stack is consolidated
# onto the owner's pull request when its branch holds the base's commits (the commit-ancestry guard of #192), the base's pull request is closed as
# superseded. Returns $null when the item has no owner branch with an open pull request; else @{ pr; changed; retargeted; superseded; refused }.
# An owner pull request still a draft is marked ready for review (GraphQL): GitHub merges no draft and QC is not offered for one (#483, #488:
# a PASS parked pass-unmerged on a successor's draft). Returns the pull request as it is afterwards.
function Set-PrReady($pr) {
    if (-not $pr -or -not $pr.draft) { return $pr }
    try { [void](Invoke-GhGraphQL 'mutation($id:ID!){ markPullRequestReadyForReview(input:{pullRequestId:$id}){ pullRequest{ number } } }' @{ id = "$($pr.node_id)" }) } catch { return $pr }
    return (Get-Pr ([int]$pr.number))
}

function Invoke-Demote([int]$n, $info, $st, [string]$Reason, [string]$Cause) {
    $s = $st.state; $c = Get-Cfg
    $d = if (Test-Has $s 'demotions') { [int]$s.demotions } else { 0 }
    $s.demotions = $d + 1
    $owned = Test-OwnedClaim $s
    $s.Remove('verdict'); $s.Remove('bot')
    if (-not $owned -or $s.demotions -gt [int]$c.timing.maxDemotions) { $s.Remove('claim'); Remove-ClaimRef $n }
    if ($s.demotions -gt [int]$c.timing.maxDemotions) {
        Set-Park $s $(if ($Cause -eq 'verdict') { 'verdict-failed' } else { 'demotion-limit' }) ($c.timing.parkHours * 60)
        Save-State $n $st
        Add-Comment $n "failed $($s.demotions) time(s), over the demotion limit of $($c.timing.maxDemotions); parked for human review: $Reason"
        return @{ outcome = 'parked'; reason = $s.park.reason }
    }
    Move-Card $info $c.columns.development
    if (-not (Test-Has $s 'pr')) { $s.pr = @{} }
    $s.pr.rejection = $Reason
    if ($owned) { Move-OwnerTo $n $s $c.columns.development } else { $s.handoff = @{ column = $c.columns.development; at = ConvertTo-Iso (Get-UtcNow) } }
    Save-State $n $st
    Add-Comment $n "$(if ($Cause -eq 'verdict') {'validation FAILED'} else {'merge failed'}): $Reason - demoting to **Development** (round-trip $($s.demotions) of $($c.timing.maxDemotions))."
    return @{ outcome = 'demoted'; reason = $Reason; round_trip = $s.demotions }
}

