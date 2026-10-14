#!/usr/bin/env pwsh
# fworker-loop.ps1: runs factory worker sessions one after another, one work item per session (docs/launcher.md). PowerShell 7 or later
# on any system (the connect prompt installs it where it is missing; Windows PowerShell 5.1 lacks what this script uses).
#
#   fworker-loop.ps1 -Dir BASE -PromptFile FILE [-Backoff 10] [-MaxBackoff 300] [-StopFile FILE] [-Claude claude] [-ClaudeArgs '--model ID --effort low']
#
# Each round starts a new `claude` session with the connect prompt (the content of FILE) as its first message. When the worker's item has ended the
# worker runs `factory.ps1 end-session`, which writes BASE/work/session-ended (this script sets FACTORY_SESSION_END_FILE to that path); the script
# then ends that session and starts the next one after a short pause. A session that exits by itself (a crash, or a person quit it) is started again
# too, with a pause that doubles up to -MaxBackoff.
# Stop: create the stop file (default BASE/work/STOP; the session that runs finishes its item first), or press Ctrl-C while no session runs.
param(
    [Parameter(Mandatory)][string]$Dir,
    [Parameter(Mandatory)][string]$PromptFile,
    [int]$Backoff = 10,
    [int]$MaxBackoff = 300,
    [string]$StopFile = '',
    [string]$Claude = 'claude',
    [string[]]$ClaudeArgs = @()
)
$ErrorActionPreference = 'Stop'
# pwsh -File passes an array as one string: one string is split at spaces (a value with a space needs the array form under pwsh -Command)
if ($ClaudeArgs.Count -eq 1) { $ClaudeArgs = @($ClaudeArgs[0] -split '\s+' | Where-Object { $_ }) }
function Say([string]$Text) { Write-Host "$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')) fworker-loop: $Text" }

if (-not (Test-Path -LiteralPath $PromptFile -PathType Leaf)) { Say "no prompt file at $PromptFile"; exit 2 }
$cmd = Get-Command $Claude -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $cmd) { Say "$Claude is not on the PATH"; exit 2 }
$work = Join-Path $Dir 'work'; New-Item -ItemType Directory -Force -Path $work | Out-Null
$marker = Join-Path $work 'session-ended'
if (-not $StopFile) { $StopFile = Join-Path $work 'STOP' }
$env:FACTORY_SESSION_END_FILE = $marker
$promptText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PromptFile).Path)

# A .cmd or .ps1 shim (an npm install on Windows) cannot take a long argument with line breaks: that session gets a short first message that names the
# prompt file instead. An executable gets the prompt itself.
$exe = $cmd.Source; $pre = @()
if ($exe -match '\.(cmd|bat)$') { $pre = @('/d', '/c', $exe); $exe = $env:ComSpec; $promptText = "Read the file $((Resolve-Path -LiteralPath $PromptFile).Path) and follow it exactly: it is the connect prompt of this factory worker session." }
elseif ($exe -match '\.ps1$') { $pre = @('-NoProfile', '-File', $exe); $exe = (Get-Process -Id $PID).Path; $promptText = "Read the file $((Resolve-Path -LiteralPath $PromptFile).Path) and follow it exactly: it is the connect prompt of this factory worker session." }

$delay = $Backoff; $round = 0; $proc = $null
try {
    while ($true) {
        if (Test-Path -LiteralPath $StopFile) { Say "stop file $StopFile is present: not starting another session"; break }
        Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
        $round++; Say "session $round starts"
        if (-not $pre) { $promptText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PromptFile).Path) }     # a refreshed prompt is used by the next session
        # ProcessStartInfo.ArgumentList quotes each argument for the system; no redirection, so the session has this console
        $psi = [Diagnostics.ProcessStartInfo]::new($exe); $psi.UseShellExecute = $false
        foreach ($x in @($pre) + @($ClaudeArgs) + @($promptText)) { $psi.ArgumentList.Add([string]$x) }
        $proc = [Diagnostics.Process]::Start($psi)
        $ended = $false
        while (-not $proc.HasExited) {
            if (Test-Path -LiteralPath $marker) {
                $ended = $true; Say 'the worker ended its session (end marker written); stopping it'
                Start-Sleep -Seconds 5
                # the whole tree: a shim's child is the real session
                if (-not $proc.HasExited) { try { $proc.Kill($true) } catch { } }
                [void]$proc.WaitForExit(10000)
                break
            }
            Start-Sleep -Seconds 2
        }
        $code = if ($proc.HasExited) { $proc.ExitCode } else { -1 }; $proc = $null
        if (Test-Path -LiteralPath $marker) { $ended = $true }
        if ($ended) { $delay = $Backoff; Say "session $round finished its item" } else { Say "session $round exited by itself (exit $code)" }
        Say "next session in $delay s (Ctrl-C or the stop file $StopFile to stop)"
        Start-Sleep -Seconds $delay
        if (-not $ended) { $delay = [Math]::Min($delay * 2, $MaxBackoff) }
    }
} finally {
    # Ctrl-C ends this script here: a session still running is stopped with it
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill($true) } catch { } }
    Say 'stopped'
}
