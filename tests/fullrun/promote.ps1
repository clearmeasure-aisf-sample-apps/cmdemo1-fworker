#!/usr/bin/env pwsh
# uat and prod are the operator's manual promotions (each starts with an Octopus sign-off). This prints the exact kit command per release and runs it
# only with -Run. The factory's own Octopus key is read-only and never deploys.
# usage: pwsh tests/fullrun/promote.ps1 -Release 2.4.120 -To uat [-Run]
param([Parameter(Mandatory)] [string] $Release, [Parameter(Mandatory)] [ValidateSet('uat', 'prod')] [string] $To, [switch] $Run)
$cmd = "cd /home/aiops/demo-environment-kit && pwsh .claude/skills/demo-environment/scripts/invoke-demo-promotion.ps1 -Config /home/aiops/demo-environment-kit/fleet/systems/cmdemo1.json -Project deployable -Environment $To -Release $Release -Wait -Approve -Reason 'factory-worker full test run: text replacement item, reviewed and approved'"
$full = "sudo -n -u aiops -H bash -lc '$cmd'"
if ($Run) { & bash -c $full } else { $full }
