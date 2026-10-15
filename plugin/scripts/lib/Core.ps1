# Core: configuration, rejection type, GitHub REST/GraphQL, per-item lock. Server side only: GH_TOKEN is read here and nowhere else.
Set-StrictMode -Version Latest
$script:Cfg = $null
$script:ServerLogin = $null
$script:AsToken = $null

class FactoryReject : System.Exception {
    [int]$Exit
    [string]$Code
    [hashtable]$Extra
    FactoryReject([int]$exit, [string]$code, [string]$reason, [hashtable]$extra) : base($reason) {
        $this.Exit = $exit; $this.Code = $code; $this.Extra = $extra
    }
}

# Exit codes: 0 ok, 2 usage, 10 gate refused, 11 stale, 12 not found, 20 retryable external, 21 accepted/pending, 30 config/secret, 40 claim held.
function Reject([int]$Exit, [string]$Code, [string]$Reason, [hashtable]$Extra = @{}) {
    throw [FactoryReject]::new($Exit, $Code, $Reason, $Extra)
}

function Get-FactoryRoot { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }

function Get-Cfg {
    if ($script:Cfg) { return $script:Cfg }
    $path = $env:FACTORY_CONFIG
    if (-not $path) { $path = Join-Path (Split-Path -Parent (Get-FactoryRoot)) 'config/instance.json' }
    if (-not (Test-Path $path)) { Reject 30 'config-missing' "instance config not found: $path" }
    $script:Cfg = Get-Content -Raw $path | ConvertFrom-Json -AsHashtable
    # Test overrides: FACTORY_TIMING_<name>=value
    foreach ($k in @($script:Cfg.timing.Keys)) {
        $v = [Environment]::GetEnvironmentVariable("FACTORY_TIMING_$k")
        if ($v) { $script:Cfg.timing[$k] = [double]$v }
    }
    return $script:Cfg
}

function Get-StateDir {
    return Get-FwSubDir 'state' $env:FACTORY_STATE_DIR
}

function Get-UtcNow { [DateTime]::UtcNow }
function ConvertTo-Iso([DateTime]$d) { $d.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
function ConvertFrom-Iso($s) { if ($null -eq $s -or "$s" -eq '') { return $null }; if ($s -is [DateTime]) { return $s.ToUniversalTime() }; [DateTime]::Parse("$s", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal) }

function Test-Has($h, [string]$k) { ($h -is [System.Collections.IDictionary]) -and $h.Contains($k) -and $null -ne $h[$k] }

function Get-RepoPath { $c = Get-Cfg; "repos/$($c.owner)/$($c.repo)" }

function Get-GhToken {
    $tok = if ($script:AsToken) { $script:AsToken } elseif ($env:FACTORY_AUTHOR_APP -eq '1') { Get-AppToken 'author' } elseif ($env:GH_TOKEN -ne 'unset') { $env:GH_TOKEN } else { $null }
    if (-not $tok) { Reject 30 'secret-missing' 'GH_TOKEN is not set on the gateway' }
    return $tok
}

function Invoke-GhRaw {
    # -OutFile: the body of the answer goes to that file and not through the JSON reader (job logs, artifact archives). GitHub answers those
    # with a redirect to a signed address; the Authorization header is not sent there (the default of Invoke-RestMethod, and what that store needs).
    param([string]$Method = 'GET', [string]$Path, $Body = $null, [string]$Accept = 'application/vnd.github+json', [string]$OutFile = '', [int]$TimeoutSec = 0, [int]$Tries = 3, [hashtable]$Headers = @{})
    # -TimeoutSec: the longest one attempt may take (0: the default of the client); -Tries: attempts on a network error or a 502-504.
    # The author identity: an organization GitHub App (cloud gateways; FACTORY_AUTHOR_APP=1) or the GH_TOKEN of a local gateway. The value
    # 'unset' is the vault placeholder written before an identity exists: it counts as no token.
    $tok = Get-GhToken
    $base = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL.TrimEnd('/') } else { 'https://api.github.com' }   # tests point this at the fake server
    $uri = if ($Path -match '^https?://') { $Path } else { "$base/$($Path.TrimStart('/'))" }
    $h = @{ Authorization = "Bearer $tok"; Accept = $Accept; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'factory-worker-gateway' }
    foreach ($k in $Headers.Keys) { if ($k -notin 'Authorization', 'Accept') { $h[$k] = $Headers[$k] } }   # (-Headers: a conditional read, If-None-Match)
    $params = @{ Uri = $uri; Method = $Method; Headers = $h; SkipHttpErrorCheck = $true; StatusCodeVariable = 'sc'; ResponseHeadersVariable = 'rh' }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 20 -Compress); $params.ContentType = 'application/json' }
    if ($OutFile) { $params.OutFile = $OutFile }
    if ($TimeoutSec -gt 0) { $params.ConnectionTimeoutSeconds = $TimeoutSec; $params.OperationTimeoutSeconds = $TimeoutSec }
    for ($try = 1; $try -le $Tries; $try++) {
        try { $resp = Invoke-RestMethod @params } catch { if ($try -ge $Tries) { Reject 20 'github-unreachable' $_.Exception.Message @{ retryAfterSeconds = 30 } }; Start-Sleep -Seconds (2 * $try); continue }
        if ($sc -in 502, 503, 504 -and $try -lt $Tries) { Start-Sleep -Seconds (2 * $try); continue }
        return @{ status = [int]$sc; body = $resp; headers = $rh }
    }
}

function Invoke-Gh {
    param([string]$Method = 'GET', [string]$Path, $Body = $null, [int[]]$Allow = @(), [string]$Accept = 'application/vnd.github+json')
    $r = Invoke-GhRaw -Method $Method -Path $Path -Body $Body -Accept $Accept
    if ($r.status -ge 200 -and $r.status -lt 300) { return $r.body }
    if ($r.status -in $Allow) { return $r }
    $detail = if ($r.body -is [string]) { $r.body } else { try { $r.body | ConvertTo-Json -Depth 4 -Compress } catch { "$($r.body)" } }
    if ($detail.Length -gt 400) { $detail = $detail.Substring(0, 400) }
    switch ($r.status) {
        404 { Reject 12 'github-not-found' "GitHub 404 on $Method $Path" @{ detail = $detail } }
        { $_ -in 403, 429 } {
            $ra = 60; if ($r.headers -and $r.headers['Retry-After']) { $ra = [int]($r.headers['Retry-After'] | Select-Object -First 1) }
            Reject 20 'github-rate-limited' "GitHub $($r.status) on $Method $Path" @{ retryAfterSeconds = $ra; detail = $detail }
        }
        default { Reject 20 'github-error' "GitHub $($r.status) on $Method $Path" @{ status = $r.status; detail = $detail; retryAfterSeconds = 30 } }
    }
}

function Invoke-GhGraphQL([string]$Query, [hashtable]$Variables = @{}) {
    $r = Invoke-GhRaw -Method POST -Path 'graphql' -Body @{ query = $Query; variables = $Variables }
    if ($r.status -ne 200) { Reject 20 'github-graphql-error' "GraphQL HTTP $($r.status)" @{ retryAfterSeconds = 30 } }
    if ($r.body.PSObject.Properties['errors'] -and $r.body.errors) {
        $msg = ($r.body.errors | ForEach-Object { $_.message }) -join '; '
        Reject 20 'github-graphql-error' $msg @{ retryAfterSeconds = 30 }
    }
    return $r.body.data
}

function Get-ServerLogin {
    if ($script:ServerLogin) { return $script:ServerLogin }
    # An App installation token cannot ask GitHub who it is (GET /user is 403 for it): the login of an App is <slug>[bot].
    $script:ServerLogin = if ($env:FACTORY_AUTHOR_APP -eq '1') { "$((Get-AppInfo 'author').slug)[bot]" } else { (Invoke-Gh -Path 'user').login }
    return $script:ServerLogin
}

# One verb at a time per item on this machine. Cross-machine exclusion is the claim ref, not this lock.
function Use-ItemLock([int]$Item, [scriptblock]$Body, [int]$WaitSeconds = 45) {
    $f = Join-Path (Get-StateDir) "item-$Item.lock"
    $fs = $null
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ($true) {
        try { $fs = [IO.File]::Open($f, 'OpenOrCreate', 'ReadWrite', 'None'); break }
        catch { if ((Get-Date) -gt $deadline) { Reject 20 'item-busy' "another operation on item $Item is running" @{ retryAfterSeconds = 5 } }; Start-Sleep -Milliseconds 300 }
    }
    try { & $Body } finally { $fs.Dispose() }
}

function Test-NameIn([string]$Name, $List) { foreach ($x in $List) { if ($x -ieq $Name) { return $true } }; return $false }
function Get-ColumnIndex([string]$Name) {
    $ai = (Get-Cfg).columns.ai
    for ($i = 0; $i -lt $ai.Count; $i++) { if ($ai[$i] -ieq $Name) { return $i } }
    return -1
}
