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

$RunnerGateScenarios['runner gate: a rule script shares no .NET startup profile; a corrupt one in HOME stops no sweep (issue 375) [full]'] = {
    $knob = 'DOTNET_MultiCoreJitMinNumCpus'; $oldKnob = [Environment]::GetEnvironmentVariable($knob)
    $dir = Rg-ProfileDir; $corrupt = @('StartupProfileData-NonInteractive', 'StartupProfileData-Interactive')
    try {
        Check 'the suite sets the knob for its own processes (run.ps1)' ($oldKnob -match '^\d+$' -and [long]$oldKnob -gt [Environment]::ProcessorCount) "$knob=$oldKnob"
        # (a) sweeps of more than 15 s (a rule script that lives 12 s writes the profile, and so does every one that exits) and one rule script of the harness
        $mark = Rg-Total
        Wait-Sweeps -Count 3 -MinSeconds 16
        $lease = Qc-Server 'leader-lease' @{ action = 'check'; holder = 'rg-375'; ttl_seconds = 60; version = 'test' }
        Check 'rule scripts ran in the window' ((Rg-Total) -gt $mark -and $lease.ok -eq $true -and $lease.exit -eq 0) "starts $((Rg-Total) - $mark), lease $($lease | ConvertTo-Json -Compress -Depth 4)"
        Check 'no pwsh of the suite wrote a .NET startup profile in its HOME' (@(Rg-Profiles).Count -eq 0) "$dir : $((Rg-Profiles) -join ', ')"
        # (b) the gateway's own part: a gateway whose environment does not have the knob still starts its rule scripts with it
        Remove-Item (Join-Path $dir 'StartupProfileData-*') -Force -ErrorAction SilentlyContinue
        [Environment]::SetEnvironmentVariable($knob, $null)
        Restart-Gateway
        [Environment]::SetEnvironmentVariable($knob, $oldKnob)   # the harness's own pwsh calls keep the knob; only the gateway runs without it
        $mark = Rg-Total
        Wait-Sweeps -Count 3 -MinSeconds 16
        Check 'a gateway started without the knob: its rule scripts ran' ((Rg-Total) -gt $mark) "starts $((Rg-Total) - $mark)"
        Check 'and wrote no .NET startup profile' (@(Rg-Profiles).Count -eq 0) "$dir : $((Rg-Profiles) -join ', ')"
        # (c) a corrupt profile in HOME, against the same gateway: a synthetic header (record and version fields shaped like those .NET 10 writes,
        # then zeros; 64 bytes) and 64 KiB of 0xFF
        $hdr = [Convert]::FromHexString('4000000166000000000000000d000000110500000e000000' + ('00' * 40))
        $bytes = [byte[]]::new($hdr.Length + 65536); [Array]::Copy($hdr, $bytes, $hdr.Length); for ($i = $hdr.Length; $i -lt $bytes.Length; $i++) { $bytes[$i] = 0xFF }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        foreach ($f in $corrupt) { [IO.File]::WriteAllBytes((Join-Path $dir $f), $bytes) }
        $failed = Rg-SweepFailures; $mark = Rg-Total
        Wait-Sweeps -Count 2
        Check 'the two full sweeps after it ran their rule scripts' ((Rg-Total) -gt $mark) "starts $((Rg-Total) - $mark)"
        Check 'and succeeded (no failed sweep in the gateway log since the write)' ((Rg-SweepFailures) -eq $failed) "failed sweep lines $failed -> $(Rg-SweepFailures)"
        $lease = Qc-Server 'leader-lease' @{ action = 'check'; holder = 'rg-375'; ttl_seconds = 60; version = 'test' }
        Check 'a new rule-script process of the harness answers (exit 0 and output)' ($lease.ok -eq $true -and $lease.exit -eq 0) ($lease | ConvertTo-Json -Compress -Depth 4)
        Check 'the corrupt profile is still in place: no process read it into a crash or rewrote it' (@($corrupt | Where-Object { $p = Join-Path $dir $_; (Test-Path $p) -and (Get-Item $p).Length -eq $bytes.Length }).Count -eq $corrupt.Count) "$dir : $((Rg-Profiles) -join ', ')"
    } finally {
        [Environment]::SetEnvironmentVariable($knob, $oldKnob)
        Remove-Item $corrupt.ForEach({ Join-Path $dir $_ }) -Force -ErrorAction SilentlyContinue
        Restart-Gateway
    }
}

foreach ($k in $RunnerGateScenarios.Keys) { $OwnerScenarios[$k] = $RunnerGateScenarios[$k] }
