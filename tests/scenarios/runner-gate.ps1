# The rule scripts' startup profile (issue 375). Runs in the owner suite (one process per call).

function Rg-Runner { (Get-Plant).runner }
function Rg-Total { $t = 0; foreach ($p in (Rg-Runner).started.PSObject.Properties) { $t += [int]$p.Value }; $t }

$RunnerGateScenarios = [ordered]@{}

# Issue 375: every pwsh that shares one HOME reads and rewrites the .NET multicore-JIT startup profile $HOME/.cache/powershell/StartupProfileData-*
# (a process that exits normally writes it, whatever its age). Concurrent or killed writers corrupt it, and from then on every new pwsh of the suite
# dies with SIGSEGV (exit 139) before it writes anything. The gateway (RunnerEnv.cs) and run.ps1 turn multicore JIT off for their pwsh processes.
function Rg-ProfileDir { Join-Path $(if ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME } else { Join-Path $env:HOME '.cache' }) 'powershell' }
function Rg-Profiles { @(Get-ChildItem -Path (Rg-ProfileDir) -File -Filter 'StartupProfileData-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) }
function Rg-SweepFailures { @(Get-ChildItem -Path $script:Work -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'gateway*.out' -or $_.Name -like 'gateway*.err' } | ForEach-Object { Select-String -Path $_.FullName -Pattern 'sweepError|"sweep":"error"|"sweep":"failed"' }).Count }

foreach ($k in $RunnerGateScenarios.Keys) { $OwnerScenarios[$k] = $RunnerGateScenarios[$k] }
