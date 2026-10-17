# Tier 1 scenarios of the gateway's MCP server (docs/design/agents-roles-launch.md 6, gateway/Mcp/): Streamable HTTP at /mcp, the bearer
# credential, tools/list by role and tools/call through the /api/v1 endpoint in-process. Suite 'mcp' (owner mode). Uses the helpers of owner.ps1.

$script:McpId = 0
# One JSON-RPC request to /mcp. The answer is JSON or one SSE event ("data: {...}"): both are read. Returns @{ status; json; text }.
function Mcp([string]$Method, $Params = @{}, [string]$Token = 'test-token') {
    $script:McpId++
    $body = @{ jsonrpc = '2.0'; id = $script:McpId; method = $Method; params = $Params } | ConvertTo-Json -Depth 12 -Compress
    $h = @{ Accept = 'application/json, text/event-stream' }; if ($Token) { $h.Authorization = "Bearer $Token" }
    $r = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/mcp" -Method POST -Headers $h -Body $body -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 120
    $text = "$($r.Content)"
    $data = @([regex]::Matches($text, '(?m)^data: ?(.*)$') | ForEach-Object { $_.Groups[1].Value })
    $raw = $(if ($data.Count) { $data[-1] } else { $text })
    $json = $null; try { $json = $raw | ConvertFrom-Json -AsHashtable -Depth 30 } catch { }
    return @{ status = [int]$r.StatusCode; json = $json; text = $text }
}
# tools/call: @{ status; isError; body (the tool's JSON text, parsed) }
function Mcp-Call([string]$Name, [hashtable]$Arguments = @{}, [string]$Token = 'test-token') {
    $r = Mcp 'tools/call' @{ name = $Name; arguments = $Arguments } $Token
    $res = $(if ($r.json) { $r.json['result'] } else { $null })
    $b = $null; if ($res -and $res['content']) { try { $b = $res['content'][0]['text'] | ConvertFrom-Json -AsHashtable -Depth 30 } catch { } }
    return @{ status = $r.status; isError = [bool]($res -and $res['isError']); body = $b; rpc = $r.json }
}
function Mcp-ToolNames([string]$Token = 'test-token') { $r = Mcp 'tools/list' @{} $Token; , @($r.json.result.tools | ForEach-Object { $_.name } | Sort-Object) }
function Api-As([string]$Method, [string]$Path, $Body = $null, [string]$Token = 'test-token') {
    $p = @{ Uri = "http://127.0.0.1:$($script:ApiPort)$Path"; Method = $Method; Headers = @{ Authorization = "Bearer $Token" }; SkipHttpErrorCheck = $true; StatusCodeVariable = 'sc' }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 8 -Compress); $p.ContentType = 'application/json' }
    $r = Invoke-RestMethod @p
    return @{ status = [int]$sc; body = $r }
}

$McpScenarios = [ordered]@{

'mcp: the owner (admin token) lists the owner read tools; a missing, forged or roleless credential is refused before the server' = {
    $names = Mcp-ToolNames
    Check 'tools/list for the admin token shows exactly the owner tools' (($names -join ',') -eq 'factory_status,item_detail,metrics,workers_and_runs') ($names -join ',')
    $none = Mcp 'tools/list' @{} ''
    Check 'no credential: 401 unauthorized' ($none.status -eq 401 -and $none.text -match '"unauthorized"') "$($none.status) $($none.text)"
    $forged = Mcp 'tools/list' @{} 'w1.alice.0000'
    Check 'a forged credential: 401' ($forged.status -eq 401)
    $w1 = Mcp 'tools/list' @{} (New-WorkerToken 'mcpw1')
    Check 'a worker credential (a seat) has no tools at /mcp yet: 403' ($w1.status -eq 403 -and $w1.text -match '"forbidden"') "$($w1.status) $($w1.text)"
}

'mcp: owner read tools answer what the /api/v1 endpoints answer; refusals keep the endpoint''s code' = {
    $s = Mcp-Call 'factory_status'
    $api = Api-As GET '/api/v1/status'
    Check 'factory_status answers the status JSON' (-not $s.isError -and $s.body.ownerMode -eq $true -and $s.body.ContainsKey('plant')) ($s | ConvertTo-Json -Compress -Depth 4)
    Check 'the same process: the start time is the endpoint''s' ([datetime]$s.body.started -eq [datetime]$api.body.started)
    $w = Mcp-Call 'workers_and_runs'
    Check 'workers_and_runs answers the workers and the runtimes' (-not $w.isError -and $w.body.ContainsKey('workers') -and $w.body.ContainsKey('runtimes')) ($w | ConvertTo-Json -Compress -Depth 4)
    $n = New-Item-Fake 'MCP item detail' 'The body of the item.' $OwnerLabels
    $d = Mcp-Call 'item_detail' @{ item = $n }
    $v = Api-As POST '/api/v1/verbs/get-item' @{ item = $n }
    Check 'item_detail answers the get-item verb''s JSON' (-not $d.isError -and $d.body.ok -eq $true -and $d.body.title -eq 'MCP item detail' -and $d.body.title -eq $v.body.title -and $d.body.status -eq $v.body.status -and $d.body.status -eq 'Technical Design') ($d | ConvertTo-Json -Compress -Depth 4)
    $gone = Mcp-Call 'item_detail' @{ item = 999999 }
    $gv = Api-As POST '/api/v1/verbs/get-item' @{ item = 999999 }
    Check 'a refusal of the verb comes back as an error with the verb''s code and exit' ($gone.isError -and $gone.body.code -eq $gv.body.code -and $gone.body.exit -eq $gv.body.exit) "$($gone | ConvertTo-Json -Compress -Depth 4) / $($gv | ConvertTo-Json -Compress -Depth 4)"
    $u = Mcp-Call 'item_detail' @{}
    Check 'a missing argument is a usage error' ($u.isError -and $u.body.code -eq 'usage' -and $u.body.exit -eq 2) ($u | ConvertTo-Json -Compress -Depth 4)
    $x = Mcp-Call 'no_such_tool'
    Check 'an unknown tool is refused' ($x.isError -and $x.body.code -eq 'unknown-tool')
    $m = Mcp-Call 'metrics' @{ since = '1y' }
    Check 'metrics checks its window' ($m.isError -and $m.body.code -eq 'usage')
    Check 'every call is logged with its tool, role and caller, never a credential' (Wait-Until -Seconds 5 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch '"mcp":"item_detail","role":"owner","caller":"admin:"').Count -gt 0 })
}
}

# ---- the role token r1 (Auth.cs, agents-roles-launch.md 4) ----------------------------------------------------------------------------------
function Role-Token([hashtable]$Claims) { (Api-As POST '/api/v1/_test/role-token' $Claims).body.token }

$McpScenarios['mcp: an r1 round trip: minted, verified for its role, expired, revoked by generation, another environment and another kind''s MAC refused'] = {
    $t = Role-Token @{ name = 'r1own'; role = 'owner'; mode = 'manual' }
    Check 'an r1 is minted' ("$t" -like 'r1.*.*')
    Check 'an owner r1 lists the owner tools at /mcp' (((Mcp-ToolNames $t) -join ',') -eq 'factory_status,item_detail,metrics,workers_and_runs')
    $st = Mcp-Call 'factory_status' @{} $t
    Check 'and its tools answer' (-not $st.isError -and $st.body.ownerMode -eq $true) ($st | ConvertTo-Json -Compress -Depth 3)
    Check 'an r1 cannot wait on an item''s triggers at /api' ((Api-As GET '/api/v1/wait?item=1&timeout=1' $null $t).status -eq 403)
    $v = Api-As POST '/api/v1/verbs/get-item' @{ item = 1 } $t
    Check 'an r1 calls no verb directly' ($v.status -eq 403) ($v | ConvertTo-Json -Compress)
    $p = $t.Split('.'); $forged = "r1.$($p[1]).$('0' * 64)"
    Check 'a forged MAC is refused' ((Mcp 'tools/list' @{} $forged).status -eq 401)
    $asItem = "i1.$($p[1]).$($p[2])"
    Check 'the same payload and MAC as an i1 is refused (domain separation)' ((Api-As POST '/api/v1/verbs/get-item' @{ item = 1 } $asItem).status -eq 401)
    $short = Role-Token @{ name = 'r1short'; role = 'owner'; mode = 'manual'; tokenSeconds = 2 }
    Check 'a short r1 answers before its expiry' ((Mcp 'tools/list' @{} $short).status -eq 200)
    Start-Sleep -Seconds 4
    Check 'and is refused after it' ((Mcp 'tools/list' @{} $short).status -eq 401)
    $prod = Role-Token @{ name = 'r1prod'; role = 'owner'; mode = 'manual'; env = 'prod' }
    Check 'an r1 of another environment is refused' ((Mcp 'tools/list' @{} $prod).status -eq 401)
    $rv = Role-Token @{ name = 'r1rev'; role = 'owner'; mode = 'manual' }
    Check 'before the revocation it answers' ((Mcp 'tools/list' @{} $rv).status -eq 200)
    [void](Api-As POST '/api/v1/workers/r1rev/revoke' @{})
    Check 'a revocation of its name refuses it' (Wait-Until -Seconds 20 { (Mcp 'tools/list' @{} $rv).status -eq 401 })
    $seat = Role-Token @{ name = 'r1seat'; role = 'seat'; mode = 'manual' }
    Check 'a seat r1 has no tools at /mcp in this step' ((Mcp 'tools/list' @{} $seat).status -eq 403)
}

$McpScenarios['mcp: the legacy kinds map to role principals: the admin token is the owner, a worker credential a seat; /status shows mode and role'] = {
    Start-OwnerClient 'r1legacy'
    try {
        $row = @((Get-Plant).plant.workers | Where-Object { $_.name -eq 'r1legacy' })
        Check 'a local worker shows mode manual and role seat' ($row.Count -eq 1 -and $row[0].mode -eq 'manual' -and $row[0].role -eq 'seat') ($row | ConvertTo-Json -Compress)
        Check 'its w1 still works at /api (auth.legacyKinds true)' ((Api-As POST '/api/v1/verbs/my-items' @{} (New-WorkerToken 'r1legacy')).status -eq 200)
    } finally { Stop-OwnerClient 'r1legacy' }
}
