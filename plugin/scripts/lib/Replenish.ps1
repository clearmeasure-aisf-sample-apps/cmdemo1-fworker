# Backlog replenishment (keeper.replenish): when an item the gateway owns reaches Done (delivered to production), the sweep creates ONE new,
# small, docs-only work item so the board keeps moving. Off unless keeper.replenish.enabled (tdd only; the source leaves it off).
#   keeper.replenish = { enabled, maxPerDay (items created in 24 h), targetOpen (open factory items of this environment, never exceeded),
#                        holdWhenWaiting (backpressure: nothing is created while this many real items or more wait for a session; 0 = off),
#                        onlyWhenIdle (nothing is created unless nothing waits and a session could take a new item) }
# Both read the gateway's last plan (sync-board input replenish_waiting, delivery-speed.md 6); with either set and no plan counted yet nothing is created.
# One new item per finished item: the finished item's state records `replenished` (the new number), so a sweep never repeats it.

$script:ReplenishTopics = @(
    @{ title = 'Docs: work order lifecycle guide'; file = 'docs/domain/work-order-lifecycle.md'; about = 'the lifecycle of a work order from Draft through Assigned and InProgress to Complete, including shelving back to Assigned and cancellation' }
    @{ title = 'Docs: work order statuses reference'; file = 'docs/domain/work-order-statuses.md'; about = 'each WorkOrderStatus (Draft, Assigned, InProgress, Complete, Cancelled), what it means and which transitions leave it' }
    @{ title = 'Docs: waiting on vendor substate explained'; file = 'docs/domain/waiting-on-vendor.md'; about = 'the WaitingOnVendor substate of InProgress and why waiting and resuming do not change the persisted status' }
    @{ title = 'Docs: work order priorities'; file = 'docs/domain/work-order-priorities.md'; about = 'the Low, Medium (default) and High priorities and how they are stored and used when searching' }
    @{ title = 'Docs: employees and roles guide'; file = 'docs/domain/employees-and-roles.md'; about = 'the Employee and Role model and what CanCreateWorkOrder and CanFulfillWorkOrder permit' }
    @{ title = 'Docs: buildings and rooms on a work order'; file = 'docs/domain/buildings-and-rooms.md'; about = 'how Building (optional) and RoomNumber locate a work order and how the building room summary groups them' }
    @{ title = 'Docs: work order search guide'; file = 'docs/domain/work-order-search.md'; about = 'the work order search filters, sort order, paging and the table and board views kept in the URL' }
    @{ title = 'Docs: work order numbers'; file = 'docs/domain/work-order-numbers.md'; about = 'the unique work order Number, where it is shown and how a work order is looked up by it' }
    @{ title = 'Docs: assigning and reassigning a work order'; file = 'docs/domain/assignment.md'; about = 'who may be assigned a work order, how CanReassign decides, and the AssignedDate' }
    @{ title = 'Docs: completing and cancelling a work order'; file = 'docs/domain/completion-and-cancellation.md'; about = 'how a work order reaches the terminal states Complete and Cancelled and what CompletedDate records' }
    @{ title = 'Docs: due dates and overdue work orders'; file = 'docs/domain/due-dates-and-overdue.md'; about = 'how DueDate drives the overdue filter and the overdue counts on the search summary cards' }
    @{ title = 'Docs: dashboard recent activity'; file = 'docs/domain/dashboard-recent-activity.md'; about = 'how the dashboard picks the last work orders by their latest created, assigned or completed date' }
    @{ title = 'Docs: API tools endpoints overview'; file = 'docs/api/tools-endpoints.md'; about = 'the anonymous /api/tools endpoints (hash, base64, guid-generator, random, slugify, timestamp-converter), their methods and the versioned prefix' }
    @{ title = 'Docs: feature flags endpoint'; file = 'docs/api/feature-flags.md'; about = 'the read-only /api/features/flags endpoint, its response shape and the flags catalog' }
    @{ title = 'Docs: health and diagnostics endpoints'; file = 'docs/api/health-endpoints.md'; about = 'the /_healthcheck, /_build and /_telemetry endpoints: what each answers and who may call it' }
    @{ title = 'Docs: work order status counts endpoint'; file = 'docs/api/status-counts.md'; about = 'the /api/work-orders/status-counts endpoint and the query behind it' }
    @{ title = 'Docs: CQRS request flow walkthrough'; file = 'docs/conventions/cqrs-request-flow.md'; about = 'how a request flows from the Blazor UI through the API controller, IBus, MediatR and a handler to the database' }
    @{ title = 'Docs: onion architecture dependency rules'; file = 'docs/conventions/onion-architecture-rules.md'; about = 'which project may reference which: Core, DataAccess, the UI layer and Database' }
    @{ title = 'Docs: testing conventions cheat sheet'; file = 'docs/conventions/testing-cheat-sheet.md'; about = 'the NUnit and Shouldly conventions: Stub prefix, test naming and the AAA layout without section comments' }
    @{ title = 'Docs: database migration how-to'; file = 'docs/conventions/database-migrations.md'; about = 'how to add a numbered DbUp script under src/Database/scripts/Update, with tabs for indentation' }
    @{ title = 'Docs: code style conventions'; file = 'docs/conventions/code-style.md'; about = 'naming, file-scoped namespaces, null! for injected fields and the other code style rules of the repository' }
    @{ title = 'Docs: branch naming and commit flow'; file = 'docs/conventions/branch-naming.md'; about = 'the {username}/{branch-description} format and when to run the quality gates' }
    @{ title = 'Docs: running the application locally'; file = 'docs/conventions/running-locally.md'; about = 'how to build, run the Server project, reach the health check and run the unit and integration tests' }
    @{ title = 'Docs: local telemetry with the Aspire dashboard'; file = 'docs/conventions/local-telemetry.md'; about = 'how the AppHost sets the OTLP endpoint locally and where telemetry goes in Azure' }
)
$script:ReplenishTerms = @('Work order', 'Draft', 'Assigned', 'InProgress', 'Complete', 'Cancelled', 'WaitingOnVendor', 'Creator', 'Assignee', 'Priority', 'Due date', 'Overdue', 'Building', 'Room number', 'Employee', 'Role', 'Query', 'Command', 'Handler', 'IBus', 'MediatR', 'DataContext', 'Smart enum', 'Onion architecture', 'DbUp', 'Lamar', 'Blazor', 'Playwright', 'bUnit', 'Shouldly', 'Feature flag', 'Health check', 'Release candidate', 'Technical debt')

function Get-ReplenishCfg {
    $c = Get-Cfg
    $r = if ((Test-Has $c 'keeper') -and (Test-Has $c.keeper 'replenish')) { $c.keeper.replenish } else { @{} }
    $e = "$env:FACTORY_ENVIRONMENT".Trim().ToLowerInvariant(); if (-not $e) { $e = 'tdd' }
    $inEnv = -not (Test-Has $r 'environments') -or (@($r.environments | ForEach-Object { "$_".ToLowerInvariant() }) -contains $e)
    $num = { param($k, $d, $min, $max) if ((Test-Has $r $k) -and "$($r.$k)" -match '^\d{1,6}$') { [Math]::Min($max, [Math]::Max($min, [int]"$($r.$k)")) } else { $d } }
    return @{
        enabled = ((Test-Has $r 'enabled') -and $r.enabled -eq $true -and $inEnv)
        maxPerDay = (& $num 'maxPerDay' 30 0 500)
        targetOpen = (& $num 'targetOpen' 10 0 200)
        holdWhenWaiting = (& $num 'holdWhenWaiting' 0 0 1000)
        onlyWhenIdle = ((Test-Has $r 'onlyWhenIdle') -and $r.onlyWhenIdle -eq $true)
    }
}

# The repository's factory issues, open and closed, newest first (up to 5 pages of 100): their titles (uniqueness) and the replenished ones of the day.
function Get-ReplenishIssues {
    $c = Get-Cfg; $out = @()
    for ($page = 1; $page -le 5; $page++) {
        $rows = @(Invoke-Gh -Path "$(Get-RepoPath)/issues?state=all&labels=$([uri]::EscapeDataString("$($c.label)"))&per_page=100&page=$page")
        foreach ($i in $rows) { if ($i -and -not $i.PSObject.Properties['pull_request']) { $out += $i } }
        if ($rows.Count -lt 100) { break }
    }
    return $out
}

# The next item: the first catalog topic whose title is unused, then a glossary entry, then a numbered note.
function Get-ReplenishTopic($Used) {
    $isFree = { param($t) -not $Used.Contains("$t".ToLowerInvariant()) }
    foreach ($t in $script:ReplenishTopics) { if (& $isFree $t.title) { return @{ title = $t.title; about = $t.about; file = $t.file } } }
    foreach ($term in $script:ReplenishTerms) {
        $t = "Docs: glossary entry for $term"
        if (& $isFree $t) { $slug = ($term.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-'); return @{ title = $t; about = "the term '$term' as it is used in this application, in two or three sentences with a pointer to the code or page where it appears"; file = "docs/glossary/$slug.md" } }
    }
    for ($k = 1; $k -lt 100000; $k++) { $t = "Docs: note $k"; if (& $isFree $t) { return @{ title = $t; about = 'one short, accurate fact about how this application works, taken from the existing code or documentation'; file = "docs/notes/note-$k.md" } } }
}

# Called by the full sweep: $List the board's cards (this environment's), $Last the last column. Returns the counts for /status, or $null when off.
# $Waiting: the items of the gateway's last plan that no session has accepted ($null: no plan counted yet); $FreeWorkers: sessions that could take a new item now.
function Invoke-Replenish($List, [string]$Last, [string]$Column, $Waiting = $null, [int]$FreeWorkers = 0) {
    $rc = Get-ReplenishCfg
    if (-not $rc.enabled) { return $null }
    $c = Get-Cfg; $envLabel = Get-EnvLabel
    $open = @($List | Where-Object { $_.open -and (Test-NameIn $c.label $_.labels) }).Count
    $res = @{ enabled = $true; maxPerDay = $rc.maxPerDay; targetOpen = $rc.targetOpen; open = $open; createdToday = 0; created = @(); skipped = ''; waiting = $Waiting; holdWhenWaiting = $rc.holdWhenWaiting; onlyWhenIdle = $rc.onlyWhenIdle }
    # backpressure: generated work only when the plant is short of it (unchanged when neither setting is on)
    if ($rc.holdWhenWaiting -gt 0 -or $rc.onlyWhenIdle) {
        if ($null -eq $Waiting) { $res.skipped = 'no-plan'; return $res }
        if ($rc.holdWhenWaiting -gt 0 -and [int]$Waiting -ge $rc.holdWhenWaiting) { $res.skipped = 'waiting'; return $res }
        if ($rc.onlyWhenIdle -and ([int]$Waiting -gt 0 -or $FreeWorkers -le 0)) { $res.skipped = 'not-idle'; return $res }
    }
    $done = @($List | Where-Object { "$($_.column)" -ieq $Last -and (Test-NameIn $c.label $_.labels) } | Sort-Object { [int]$_.number } -Descending)
    $issues = $null; $today = 0; $used = [System.Collections.Generic.HashSet[string]]::new()
    $cutoff = (Get-UtcNow).AddHours(-24)
    $reads = 0
    foreach ($d in $done) {
        if ($open -ge $rc.targetOpen) { $res.skipped = 'target-open'; break }
        if ($reads -ge 12) { break }
        $reads++
        $st = Get-State $d.number; $s = $st.state
        if (-not ((Test-Has $s 'merge') -and (Test-Has $s.merge 'done_at') -and $s.merge.done_at)) { continue }
        $doneAt = ConvertFrom-Iso $s.merge.done_at
        if (-not $doneAt -or ((Get-UtcNow) - $doneAt).TotalHours -gt 24) { continue }
        if ((Test-Has $s 'replenished') -and $s.replenished) { continue }
        if ($null -eq $issues) {
            $issues = Get-ReplenishIssues
            foreach ($i in $issues) { [void]$used.Add("$($i.title)".ToLowerInvariant()) }
            $today = @($issues | Where-Object { "$($_.body)" -match '<!-- keeper-replenish -->' -and $_.created_at -and ((ConvertFrom-Iso "$($_.created_at)") -gt $cutoff) }).Count
        }
        if ($today -ge $rc.maxPerDay) { $res.skipped = 'max-per-day'; break }
        $topic = Get-ReplenishTopic $used
        $body = @"
Docs-only change. Add or extend ``$($topic.file)`` with a short, accurate page about $($topic.about). Keep it to what the repository's code and existing documentation already show.

Created by the fworker gateway to keep activity going: work item #$($d.number) finished (Done, delivered to production), so the gateway added this one. <!-- keeper-replenish -->

## Acceptance criteria

- ``$($topic.file)`` exists and describes $($topic.about).
- The page is accurate against the code and existing documentation.
- Only Markdown files under ``docs/`` change; no source code, tests, scripts, workflows or configuration.
"@
        $labels = @('enhancement', "$($c.autoMergeLabel)", "$($c.label)", $envLabel) | Where-Object { $_ } | Select-Object -Unique
        $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues" -Body @{ title = $topic.title; body = $body; labels = @($labels) }
        $n = [int]$r.number
        $s.replenished = @{ item = $n; at = (ConvertTo-Iso (Get-UtcNow)) }; Save-State $d.number $st   # first: a failure below must not create a second item
        $info = Get-ItemInfo $n
        if (-not $info.cardId) { [void](Add-ToBoard $info.nodeId); $info = Get-ItemInfo $n }
        if ("$($info.column)" -ine $Column) { Move-Card $info $Column }
        [void](Add-Journal $d.number $null 'replenish' @{ new = $n; title = $topic.title })
        [void]$used.Add($topic.title.ToLowerInvariant())
        $open++; $today++; $res.created += $n
    }
    $res.open = $open; $res.createdToday = $today
    return $res
}
