#!/usr/bin/env pwsh
# fworker-loop.ps1: runs factory worker sessions one after another, one work item per session (docs/launcher.md). PowerShell 7 or later
# on any system (the connect prompt installs it where it is missing; Windows PowerShell 5.1 lacks what this script uses).
#
#   fworker-loop.ps1 -Dir BASE -PromptFile FILE [-Backoff 10] [-MaxBackoff 300] [-StopFile FILE] [-Claude claude] [-ClaudeArgs '--model ID --effort low']
#                    [-StreamStale 600] [-Silent 1800] [-Unsent 600] [-ScreenCmd CMD]
#
# Each round starts a new `claude` session with the connect prompt (the content of FILE) as its first message. When the worker's item has ended the
# worker runs `factory.ps1 end-session`, which writes BASE/work/session-ended (this script sets FACTORY_SESSION_END_FILE to that path); the script
# then ends that session and starts the next one after a short pause. A session that exits by itself (a crash, or a person quit it) is started again
# too, with a pause that doubles up to -MaxBackoff.
# Stop: create the stop file (default BASE/work/STOP; the session that runs finishes its item first), or press Ctrl-C while no session runs.
# Watchdog (as fworker-loop.sh): the session is ended and restarted when its event stream showed no line for -StreamStale seconds, when its screen did
# not change for -Silent seconds while it holds an item, or when its prompt holds typed, unsent input for -Unsent seconds. The screen is read with
# -ScreenCmd (a command line whose output is the screen), default `tmux capture-pane -p` inside tmux; without one the screen checks are off. 0 switches
# a check off. Every restart and its reason is written to BASE/work/launcher.log.
param(
    [Parameter(Mandatory)][string]$Dir,
    [Parameter(Mandatory)][string]$PromptFile,
    [int]$Backoff = 10,
    [int]$MaxBackoff = 300,
    [string]$StopFile = '',
    [string]$Claude = 'claude',
    [string[]]$ClaudeArgs = @(),
    [int]$StreamStale = 600,
    [int]$Silent = 1800,
    [int]$Unsent = 600,
    [string]$ScreenCmd = ''
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
$alive = Join-Path $work 'stream-alive'; $owning = Join-Path $work 'session-owning'; $log = Join-Path $work 'launcher.log'
if (-not $ScreenCmd -and $env:TMUX_PANE -and (Get-Command tmux -ErrorAction SilentlyContinue)) { $ScreenCmd = "tmux capture-pane -p -t $($env:TMUX_PANE)" }
function Write-Restart([string]$Why) {
    Say "restart session ${round}: $Why"
    try { Add-Content -LiteralPath $log -Value "$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')) session $round restart: $Why" } catch { }
}
# the watchdog's verdict for the running session, or '': stream silence, screen silence while it holds an item, typed input left unsent
function Test-Watchdog {
    $now = [DateTime]::UtcNow
    if ($StreamStale -gt 0) {
        $ref = $script:started
        if (Test-Path -LiteralPath $alive) { $m = (Get-Item -LiteralPath $alive).LastWriteTimeUtc; if ($m -gt $ref) { $ref = $m } }
        $q = [int]($now - $ref).TotalSeconds
        if ($q -ge $StreamStale) { return "stream-disconnected: no line on the event stream for $q s" }
    }
    if (-not $ScreenCmd) { return '' }
    $scr = try { if ($IsWindows) { ($null | & cmd /d /c $ScreenCmd 2>$null) -join "`n" } else { ($null | & sh -c $ScreenCmd 2>$null) -join "`n" } } catch { return '' }
    if ($scr -ne $script:lastScreen) { $script:lastScreen = $scr; $script:screenAt = $now; return '' }
    $quiet = [int]($now - $script:screenAt).TotalSeconds
    # only the bottom-most prompt line among the last 8 non-blank lines counts (tmux pads the capture to the pane height) (the transcript above shows earlier messages with the same marker), without the
    # box border, the "Try ..." placeholder or a numbered menu row
    $line = @(@($scr -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) | Where-Object { $_ -match '^( |│|\|)*(>|❯)( |$)' } | Select-Object -Last 1)
    $text = if ($line.Count) { ($line[0] -replace '^( |│|\|)*(>|❯) *', '' -replace '( |│|\|)*$', '') } else { '' }
    $typed = $text -and $text -notmatch '^Try "' -and $text -notmatch '^\d+\.'
    if ($Unsent -gt 0 -and $quiet -ge $Unsent -and $typed) { return "unsent-input: typed input sat unsent in the prompt for $quiet s" }
    if ($Silent -gt 0 -and (Test-Path -LiteralPath $owning) -and $quiet -ge $Silent) { return "no-output-while-owning: the screen did not change for $quiet s while the session holds item $("$(Get-Content -Raw -LiteralPath $owning -ErrorAction SilentlyContinue)".Trim())" }
    return ''
}
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
        foreach ($f in $marker, "$marker.reason", $alive, $owning) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
        $script:started = [DateTime]::UtcNow; $script:screenAt = $script:started; $script:lastScreen = $null; $checkAt = [DateTime]::MinValue; $restarted = ''
        $round++; Say "session $round starts"
        if (-not $pre) { $promptText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PromptFile).Path) }     # a refreshed prompt is used by the next session
        # ProcessStartInfo.ArgumentList quotes each argument for the system; no redirection, so the session has this console
        $psi = [Diagnostics.ProcessStartInfo]::new($exe); $psi.UseShellExecute = $false
        foreach ($x in @($pre) + @($ClaudeArgs) + @($promptText)) { $psi.ArgumentList.Add([string]$x) }
        $proc = [Diagnostics.Process]::Start($psi)
        $ended = $false
        while (-not $proc.HasExited) {
            $why = ''
            if (Test-Path -LiteralPath $marker) {
                $why = "$(Get-Content -Raw -LiteralPath "$marker.reason" -ErrorAction SilentlyContinue)".Trim()
                if (-not $why) { $why = 'end-session: the worker ended its session' }
                Write-Restart $why
                Start-Sleep -Seconds 5
            } elseif ([DateTime]::UtcNow -ge $checkAt) {
                $checkAt = [DateTime]::UtcNow.AddSeconds(5); $why = Test-Watchdog
                if ($why) { $why = "watchdog $why"; Write-Restart $why }
            }
            if ($why) {
                $ended = $true; $restarted = $why
                # the whole tree: a shim's child is the real session
                if (-not $proc.HasExited) { try { $proc.Kill($true) } catch { } }
                [void]$proc.WaitForExit(10000)
                break
            }
            Start-Sleep -Seconds 2
        }
        $code = if ($proc.HasExited) { $proc.ExitCode } else { -1 }; $proc = $null
        if (Test-Path -LiteralPath $marker) { $ended = $true }
        if ($ended) { $delay = $Backoff; Say $(if ($restarted -like 'watchdog*') { "session $round was restarted by the watchdog" } else { "session $round finished its item" }) } else { Say "session $round exited by itself (exit $code)" }
        Say "next session in $delay s (Ctrl-C or the stop file $StopFile to stop)"
        Start-Sleep -Seconds $delay
        if (-not $ended) { $delay = [Math]::Min($delay * 2, $MaxBackoff) }
    }
} finally {
    # Ctrl-C ends this script here: a session still running is stopped with it
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill($true) } catch { } }
    Say 'stopped'
}
