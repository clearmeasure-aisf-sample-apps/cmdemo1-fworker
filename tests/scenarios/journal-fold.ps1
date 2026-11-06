# The journal folds its repeats (plugin/scripts/server/lib/Journal.ps1, issue 93): a repeated matched, waiting or reassigned line edits the comment of its
# kind, a line of another kind (offered aside) ends the folds, only new comments count against the cap of 30, and the state keeps per-kind counts.
# Runs in the owner suite; the rule script journal is called directly (as the gateway calls it), on an item that is not factory work, so no sweep
# touches it.

function Jf-Journal([int]$n, [string]$Ev, [string]$Key, [hashtable]$Fields = @{}) { Sg-Server 'journal' @{ item = $n; event = $Ev; key = $Key; fields = $Fields } }
# (written to the pipeline one by one: callers wrap them in @())
function Jf-Comments([int]$n) { (Get-FakeIssue $n).comments | Where-Object { "$($_.body)" -match '<!-- plant event=' } }
function Jf-Events([int]$n) { Jf-Comments $n | ForEach-Object { [regex]::Match("$($_.body)", 'plant event=([a-z-]+)').Groups[1].Value } }
# the item's state comment with journal.count set to a value (as an item that has journaled that much already)
function Jf-SetCount([int]$n, [int]$Count) {
    $st = Pm-State $n; $st.journal.count = $Count; $json = $st | ConvertTo-Json -Depth 12
    $cm = @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)".StartsWith('<!-- factory-state:') })[0]
    $nb = "$($cm.body)" -replace '(?s)```json.*```', "``````json`n$json`n``````"
    Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/comments/$($cm.id)" -Method PATCH -Headers @{ Authorization = "Bearer $env:GH_TOKEN" } -Body (@{ body = $nb } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
}

$JournalFoldScenarios = [ordered]@{}

foreach ($k in $JournalFoldScenarios.Keys) { $OwnerScenarios[$k] = $JournalFoldScenarios[$k] }
