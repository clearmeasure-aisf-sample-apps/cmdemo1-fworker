# Model choice. Two halves, deliberately apart:
#   server (gateway): WHEN to escalate. Verb_model_tier turns an item's history (relaunches, review findings, demotions, fix-forward rounds, gate
#     refusals) into a tier number, never lower than before in the same column. A script rule, so a looping model cannot argue its way out of it.
#   client (this machine): WHICH model sits on each rung (~/.config/factory-worker/models.json), and what to run when the mode is fixed.
# The default ladder is cost-first: economy -> standard -> premium.

function Get-RefusalFile { Join-Path (Get-StateDir) 'refusals.json' }
function Read-Refusals { $f = Get-RefusalFile; if (Test-Path $f) { try { return Get-Content -Raw $f | ConvertFrom-Json -AsHashtable -DateKind String } catch { } }; return @{} }
function Add-Refusal([int]$Item) { $r = Read-Refusals; $r["$Item"] = $(if ($r.ContainsKey("$Item")) { [int]$r["$Item"] + 1 } else { 1 }); $r | ConvertTo-Json | Set-Content (Get-RefusalFile) }

