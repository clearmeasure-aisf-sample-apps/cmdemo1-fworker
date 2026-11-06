# Verbs on one item: read, write the body, comment, claim, review record, move, complete. Each Verb_* returns a hashtable or rejects.

function Need($a, [string]$k) { if (-not (Test-Has $a $k) -or "$($a[$k])" -eq '') { Reject 2 'usage' "missing argument: $k" }; $a[$k] }
function Get-ItemNumber($a) { [int](Need $a 'item') }

function Assert-Open($Info) { if (-not $Info.open) { Reject 11 'item-closed' "issue #$($Info.number) is closed" } }

function Get-RefPath { "factory/$((Get-Cfg).name)/claims" }

function Remove-ClaimRef([int]$Item) {
    Invoke-Gh -Method DELETE -Path "$(Get-RepoPath)/git/refs/$(Get-RefPath)/$Item" -Allow 404, 422 | Out-Null
}

# Text that a session wrote into an item because of a message (worker-messaging.md 3.8): the gateway wraps it in one block between these markers.
# get-item labels the blocks as data and inspect-facts leaves them out, so they never read as specification.
$script:DerivedBlockRx = '(?s)<!-- aisf:assumption source=([A-Za-z0-9-]{1,40}) from=([A-Za-z0-9_-]{1,40})(?: in_reply_to=[A-Za-z0-9-]{1,40})? -->(?:(?!<!-- aisf:assumption ).)*?<!-- /aisf:assumption -->'

