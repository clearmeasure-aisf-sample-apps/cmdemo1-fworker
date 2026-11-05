# Why a check failed: what pr-checks adds to the status of the checks of a pull request head.
#   per failed or cancelled check: the output title and summary, the annotations (where Qodana and the analyzers report), for a GitHub Actions
#   job the failed steps and an excerpt of the job log, the artifacts of the workflow run, and the BASELINE: the same check on the base branch
#   (the last 5 commits), so a session can tell "caused by my change" from "already red".
#   on request: the whole log of one job, or one text file of an artifact, in pages (the client saves them to a file with --save).
# Everything a check, a log or an artifact says is text that a pull request can control. It is returned only in fields named "data" (and
# "content" for a page), each marked author_trust "untrusted", after ANSI sequences and control characters are removed and the secret patterns
# of the instance (Protect-JournalText) are applied. Nothing here writes that text to a log. Read-only: no state is saved, nothing is commented.
# GitHub permissions of the gateway's identity: "Checks: read" (check runs, annotations) and "Actions: read" (jobs, logs, artifacts). A call
# GitHub refuses becomes a code in the answer (logs-not-permitted, annotations-not-permitted, artifacts-not-permitted), never an error of the verb.

$script:CkLogBudget = 60000        # characters of log excerpts in one answer, shared by the failed jobs
$script:CkPageChars = 48000        # characters of one page of a whole log or of an artifact file
$script:CkMaxTextBytes = 5MB       # hard cap of a whole log or an artifact file: of a longer log only the end is served
$script:CkMaxZipBytes = 25MB       # a larger artifact is listed, never downloaded
$script:CkMaxDetailed = 6          # failed checks that get their detail in one answer; the others are named (ask for one with --check)
$script:CkCallSeconds = 10          # the longest one GitHub call for detail may take
$script:CkDeadline = $null         # set by Add-CheckDetail: after it no further detail call is made
$script:CkMaxAnnotations = 50      # annotations returned per check (all are counted, up to 300)
$script:CkTextExt = '\.(sarif|json|txt|log|xml|trx|md|csv|tsv|html?|ya?ml|out|err|props|targets|config|ini|toml|diff|patch)$'
$script:CkFailed = @('failure', 'timed_out', 'action_required', 'startup_failure')
$script:CkErrorRx = [regex]::new('(?i:##\[error\]|\berror\b|warning as error|test run failed|\b\d+\s+failed\b|\bfailed:\s*[1-9])|FAILED|\bFailed\b|\[FAIL\]|\bFAIL\b')
$script:CkTsRx = [regex]::new('^\uFEFF?(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.\d+)?Z ?')

# A property of an object GitHub answered, or $null when it has none (strict mode refuses a missing property).
function Get-CkProp($o, [string]$n) {
    if ($null -eq $o) { return $null }
    if ($o -is [System.Collections.IDictionary]) { if ($o.Contains($n)) { return $o[$n] }; return $null }
    $p = $o.PSObject.Properties[$n]; if ($p) { return $p.Value }
    return $null
}

# One GET that GitHub may refuse. @{ ok = $true; body } or @{ ok = $false; code; reason }: the refusal is an answer, not an error.
# $What names the thing for the code: logs, annotations, artifacts, baseline.
# It never throws: a network error or a timeout is a refusal too (github-unreachable). One attempt, at most $script:CkCallSeconds, and none
# at all once the deadline of the answer has passed (detail-deadline): collecting detail must never cost the caller its answer.
function Invoke-GhOptional([string]$Path, [string]$What, [string]$OutFile = '') {
    $limit = $script:CkCallSeconds
    if ($script:CkDeadline) {
        $left = [int]($script:CkDeadline - [DateTime]::UtcNow).TotalSeconds
        if ($left -lt 1) { return @{ ok = $false; code = 'detail-deadline'; reason = 'not read: the time this answer may take was used up; ask again, or for one check with --check' } }
        $limit = [Math]::Min($limit, $left)
    }
    try { $r = Invoke-GhRaw -Path $Path -OutFile $OutFile -TimeoutSec $limit -Tries 1 }
    catch [FactoryReject] { return @{ ok = $false; code = $_.Exception.Code; reason = "GitHub could not be read for this ($What): it did not answer in $limit s or the connection failed" } }
    catch { return @{ ok = $false; code = 'github-unreachable'; reason = "GitHub could not be read for this ($What)" } }
    if ($r.status -ge 200 -and $r.status -lt 300) { return @{ ok = $true; body = $r.body } }
    $msg = "$(Get-CkProp $r.body 'message')"
    $limited = $r.status -eq 429 -or $msg -match '(?i)rate limit|abuse' -or ($r.headers -and "$($r.headers['x-ratelimit-remaining'] | Select-Object -First 1)" -eq '0')
    $need = if ($What -eq 'annotations' -or $What -eq 'baseline') { 'Checks: read' } else { 'Actions: read' }
    switch ($r.status) {
        { $limited } { return @{ ok = $false; code = 'github-rate-limited'; reason = "GitHub $($r.status): the rate limit of the gateway's identity is reached; ask again later" } }
        403 { return @{ ok = $false; code = "$What-not-permitted"; reason = "GitHub refused (403): the gateway's GitHub identity lacks the repository permission '$need'. Only the owner can grant it; nothing a session does changes this." } }
        404 { return @{ ok = $false; code = "$What-not-found"; reason = "GitHub has none (404). For a private repository this is also what a missing permission '$need' looks like." } }
        410 { return @{ ok = $false; code = "$What-expired"; reason = 'GitHub no longer keeps it (410): past the retention period' } }
        default { return @{ ok = $false; code = 'github-error'; reason = "GitHub answered $($r.status)" } }
    }
}

# The checks of the last 5 commits of the base branch. @{ available; branch; commits = @(@{ sha; runs = name -> run }) }, newest first.
function Get-BaseHistory([string]$Branch) {
    $r = Invoke-GhOptional "$(Get-RepoPath)/commits?sha=$([uri]::EscapeDataString($Branch))&per_page=5" 'baseline'
    if (-not $r.ok) { return @{ available = $false; branch = $Branch; code = $r.code; reason = $r.reason } }
    $commits = @()
    foreach ($c in @($r.body | Where-Object { $_ } | Select-Object -First 5)) {
        $cr = Invoke-GhOptional "$(Get-RepoPath)/commits/$($c.sha)/check-runs?per_page=100" 'baseline'
        if (-not $cr.ok) { return @{ available = $false; branch = $Branch; code = $cr.code; reason = $cr.reason } }
        $latest = @{}
        foreach ($run in @(Get-CkProp $cr.body 'check_runs' | Where-Object { $_ })) {
            $k = "$($run.name)"
            $at = Get-CkProp $run 'started_at'; $was = if ($latest.ContainsKey($k)) { Get-CkProp $latest[$k] 'started_at' } else { $null }
            if (-not $latest.ContainsKey($k) -or ($at -and $was -and (ConvertFrom-Iso $at) -gt (ConvertFrom-Iso $was))) { $latest[$k] = $run }
        }
        $commits += @{ sha = "$($c.sha)"; runs = $latest }
    }
    return @{ available = $true; branch = $Branch; commits = $commits }
}

# The baseline of one check: its newest finished conclusion on the base branch and how many of the last 5 commits failed it.
function Get-CheckBaseline([string]$Name, $History) {
    if (-not $History.available) { return @{ available = $false; branch = $History.branch; code = $History.code; reason = $History.reason } }
    $newest = $null; $failed = 0; $ran = 0
    foreach ($c in $History.commits) {
        if (-not $c.runs.ContainsKey($Name)) { continue }
        $run = $c.runs[$Name]; if ("$($run.status)" -ne 'completed') { continue }
        $ran++; $con = "$(Get-CkProp $run 'conclusion')"
        if ($null -eq $newest) { $newest = $con }
        if ($con -in $script:CkFailed) { $failed++ }
    }
    return @{ available = $true; branch = $History.branch; conclusion = $(if ($null -eq $newest) { 'absent' } else { $newest }); failedOfLast5 = $failed; ranOfLast5 = $ran; commits = @($History.commits).Count }
}

