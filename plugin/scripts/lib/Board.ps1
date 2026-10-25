# Board: GitHub Projects v2 reads and moves, issue reads, state comment (durable per-item state).

function Get-ProjectMeta {
    $cache = Join-Path (Get-StateDir) 'project-meta.json'
    if ((Test-Path $cache) -and ((Get-Date) - (Get-Item $cache).LastWriteTime).TotalMinutes -lt 10) {
        return Get-Content -Raw $cache | ConvertFrom-Json -AsHashtable
    }
    $c = Get-Cfg
    $q = 'query($o:String!,$n:Int!){ organization(login:$o){ projectV2(number:$n){ id fields(first:30){ nodes{ ... on ProjectV2SingleSelectField{ id name options{ id name } } } } } } }'
    $d = Invoke-GhGraphQL $q @{ o = $c.owner; n = [int]$c.project }
    $p = $d.organization.projectV2
    $f = $p.fields.nodes | Where-Object { $_ -and $_.PSObject.Properties['name'] -and $_.name -eq 'Status' } | Select-Object -First 1
    $meta = @{ projectId = $p.id; fieldId = $f.id; options = [ordered]@{} }
    foreach ($o in $f.options) { $meta.options[$o.name] = $o.id }
    $meta | ConvertTo-Json -Depth 6 | Set-Content $cache
    return $meta
}

function Get-BoardColumns { @((Get-ProjectMeta).options.Keys) }

function Get-NextColumn([string]$Column) {
    $cols = Get-BoardColumns
    for ($i = 0; $i -lt $cols.Count; $i++) { if ($cols[$i] -ieq $Column) { if ($i + 1 -lt $cols.Count) { return $cols[$i + 1] } else { return $null } } }
    return $null
}

function Get-LastColumn { $cols = Get-BoardColumns; $cols[$cols.Count - 1] }

# Everything one verb needs to know about an issue and its card.
function Get-ItemInfo([int]$Item) {
    $c = Get-Cfg
    $q = @'
query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id number state title body url createdAt
  labels(first:50){ nodes{ name } }
  projectItems(first:20){ nodes{ id project{ number } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
'@
    $d = Invoke-GhGraphQL $q @{ o = $c.owner; r = $c.repo; n = $Item }
    $i = $d.repository.issue
    if (-not $i) { Reject 12 'item-not-found' "issue #$Item not found" }
    $labels0 = @($i.labels.nodes | ForEach-Object { $_.name })
    if (-not (Test-ItemInScope $labels0)) { Reject 10 'out-of-scope' "issue #$Item is outside this gateway's scope: it does not carry exactly one env: label, '$(Get-EnvLabel)'" }
    $card = $i.projectItems.nodes | Where-Object { $_.project.number -eq [int]$c.project } | Select-Object -First 1
    $status = $null; $cardId = $null
    if ($card) { $cardId = $card.id; if ($card.fieldValueByName) { $status = $card.fieldValueByName.name } }
    return @{
        number = [int]$i.number; nodeId = $i.id; open = ($i.state -eq 'OPEN'); title = $i.title; body = "$($i.body)"; url = $i.url; createdAt = $i.createdAt
        labels = @($i.labels.nodes | ForEach-Object { $_.name }); cardId = $cardId; column = $status
    }
}

function Test-Label($Info, [string]$Name) { Test-NameIn $Name $Info.labels }

# Environment scope (docs/design/environment-test-factories.md 3): with scoping on, a gateway acts only on items that carry exactly one env: label
# and that label is env:<FACTORY_ENVIRONMENT>; an unlabelled item, or one with two env: labels, is nobody's. Scoping is on in the environments the
# configuration's envScope lists (config/deploy.overrides.json; the source default is none, so the Tier 1 suites run unscoped); FACTORY_ENV_SCOPE
# 1 or 0 on the gateway app overrides the list. Get-EnvLabel is '' with scoping off.
function Get-EnvLabel {
    $c = Get-Cfg
    $e = "$env:FACTORY_ENVIRONMENT".Trim().ToLowerInvariant(); if (-not $e) { $e = 'tdd' }
    $on = if ("$env:FACTORY_ENV_SCOPE" -in '0', '1') { $env:FACTORY_ENV_SCOPE -eq '1' } else { (Test-Has $c 'envScope') -and (@($c.envScope) | Where-Object { "$_".ToLowerInvariant() -eq $e }) }
    if (-not $on) { return '' }
    return "env:$e"
}
function Test-ItemInScope($Labels) {
    $mine = Get-EnvLabel
    if (-not $mine) { return $true }
    $envs = @(@($Labels) | Where-Object { "$_" -ilike 'env:*' })
    return ($envs.Count -eq 1 -and "$($envs[0])" -ieq $mine)
}

# Labels of an issue. GitHub creates a label that does not exist yet when it is added to an issue; removing one the issue does not carry is a 404,
# which is no failure here.
function Add-ItemLabels([int]$Item, [string[]]$Names) { Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$Item/labels" -Body @{ labels = @($Names) } | Out-Null }
function Remove-ItemLabel([int]$Item, [string]$Name) { Invoke-Gh -Method DELETE -Path "$(Get-RepoPath)/issues/$Item/labels/$([uri]::EscapeDataString($Name))" -Allow 404 | Out-Null }

# Priority (docs/design/plant-manager.md 10e): a label priority:top or priority:high (priorityLabels of the config) puts an item ahead of the others
# when the plant offers work; no label is normal. Pins keep their place: a pinned item still takes its worker's slot first.
function Get-PriorityLabels {
    $c = Get-Cfg; $p = if (Test-Has $c 'priorityLabels') { $c.priorityLabels } else { @{} }
    return @{ top = $(if (Test-Has $p 'top') { "$($p.top)" } else { 'priority:top' }); high = $(if (Test-Has $p 'high') { "$($p.high)" } else { 'priority:high' }) }
}
function Get-ItemPriority($Labels) {
    $pl = Get-PriorityLabels
    if (Test-NameIn $pl.top $Labels) { return 'top' }
    if (Test-NameIn $pl.high $Labels) { return 'high' }
    return ''
}

# Issue 109: the labels by which a person holds an item for themselves (holdLabels, default "hold:owner"). Honoured like the Demo label: no offer,
# an open offer is dropped (the sweep no longer asks for it), claim-item is refused. Returns the first such label the item carries, or ''.
function Get-HoldLabel($Labels) {
    $c = Get-Cfg
    $hold = if (Test-Has $c 'holdLabels') { @($c.holdLabels) } else { @('hold:owner') }
    foreach ($h in $hold) { if ("$h" -and (Test-NameIn "$h" $Labels)) { return "$h" } }
    return ''
}

function Move-Card([hashtable]$Info, [string]$Column) {
    $meta = Get-ProjectMeta
    if (-not $meta.options.Contains($Column)) { Reject 10 'unknown-column' "board has no column '$Column'" }
    if (-not $Info.cardId) { Reject 10 'not-on-board' "issue #$($Info.number) is not on the board" }
    $m = 'mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){ updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$o}}){ projectV2Item{ id } } }'
    Invoke-GhGraphQL $m @{ p = $meta.projectId; i = $Info.cardId; f = $meta.fieldId; o = $meta.options[$Column] } | Out-Null
}

function Add-ToBoard([string]$IssueNodeId) {
    $meta = Get-ProjectMeta
    $m = 'mutation($p:ID!,$c:ID!){ addProjectV2ItemById(input:{projectId:$p,contentId:$c}){ item{ id } } }'
    (Invoke-GhGraphQL $m @{ p = $meta.projectId; c = $IssueNodeId }).addProjectV2ItemById.item.id
}

# All cards on the board with their issue facts: one paged query per sweep.
function Get-BoardList {
    $c = Get-Cfg
    $q = @'
query($o:String!,$n:Int!,$after:String){ organization(login:$o){ projectV2(number:$n){ items(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
  nodes{ id fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } }
    content{ ... on Issue{ number state title body labels(first:30){ nodes{ name } } } } } } } } }
'@
    $after = $null; $out = @()
    do {
        $d = Invoke-GhGraphQL $q @{ o = $c.owner; n = [int]$c.project; after = $after }
        $items = $d.organization.projectV2.items
        foreach ($n in $items.nodes) {
            if (-not $n.content -or -not $n.content.PSObject.Properties['number']) { continue }
            $labels1 = @($n.content.labels.nodes | ForEach-Object { $_.name })
            if (-not (Test-ItemInScope $labels1)) { continue }   # another environment's card (or an unlabelled one with scoping on): not seen here
            $out += @{ cardId = $n.id; number = [int]$n.content.number; open = ($n.content.state -eq 'OPEN'); title = $n.content.title
                body = $(if ($n.content.PSObject.Properties['body']) { "$($n.content.body)" } else { '' })
                column = $(if ($n.fieldValueByName) { $n.fieldValueByName.name } else { $null })
                labels = $labels1 }
        }
        $after = $items.pageInfo.endCursor
    } while ($items.pageInfo.hasNextPage)
    return $out
}

function Close-Issue([int]$Item) {
    Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/issues/$Item" -Body @{ state = 'closed'; state_reason = 'completed' } | Out-Null
}

# The last line of a comment written for an AI session's verb: "AI session: [<name>](<url>)", the name alone without a page; '' when the gateway
# runs the verb for itself, the operator or a click (no _session). An https or http page only, no query (a token never travels in a link).
function Get-SessionFooter {
    $s = $script:FactorySession
    if (-not ($s -is [System.Collections.IDictionary]) -or "$($s['name'])" -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$') { return '' }
    $u = "$($s['url'])"
    if ($u -and $u -match '^https?://[^\s?#()<>\[\]]+$') { return "AI session: [$($s['name'])]($u)" }
    return "AI session: $($s['name'])"
}
# $Body with the session footer as its last line, once: only this session's exact footer at the very end counts as present (a worker's own
# text that quotes or imitates a footer never suppresses the real one)
function Add-SessionFooter([string]$Body) {
    $f = Get-SessionFooter
    if (-not $f -or $Body.TrimEnd().EndsWith($f)) { return $Body }
    return "$Body`n`n$f"
}

function Add-Comment([int]$Item, [string]$Text) {
    $body = "🤖 $((Get-Cfg).name): $(Add-SessionFooter $Text)`n`n<!-- factory-journal:$((Get-Cfg).name) -->"
    Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$Item/comments" -Body @{ body = $body } | Out-Null
}

# ---- state comment ----------------------------------------------------------------------------------------------
function Get-StateMarker { "<!-- factory-state:$((Get-Cfg).name) v1 -->" }

function Get-Comments([int]$Item) {
    $all = @(); $page = 1
    do {
        $chunk = @(Invoke-Gh -Path "$(Get-RepoPath)/issues/$Item/comments?per_page=100&page=$page")
        $all += $chunk; $page++
    } while ($chunk.Count -eq 100)
    return $all
}

# Returns @{ commentId; state } . Only a comment written by the gateway's own login counts.
function Get-State([int]$Item) {
    $marker = Get-StateMarker; $login = Get-ServerLogin
    $all = @(Get-Comments $Item)
    $found = $all | Where-Object { $_.user.login -eq $login -and $_.body.StartsWith($marker) } | Select-Object -First 1
    if (-not $found) {
        # A change of identity (the personal/bot token of a local gateway to a GitHub App) must not orphan the items in flight: with none of its own,
        # the gateway adopts the newest state written by one of the logins the instance names in stateAuthors, and writes its own comment on the next save.
        $cfg = Get-Cfg; $trusted = @(if (Test-Has $cfg 'stateAuthors') { $cfg.stateAuthors })
        $old = if ($trusted.Count -gt 0) { @($all | Where-Object { $_.user.login -in $trusted -and $_.body.StartsWith($marker) }) | Select-Object -Last 1 } else { $null }
        if ($old) {
            $m0 = [regex]::Match($old.body, '(?s)```json\s*(.*?)\s*```')
            if ($m0.Success) { return @{ commentId = $null; state = ($m0.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String); comments = $all; adopted_from = $old.user.login } }
        }
        return @{ commentId = $null; state = @{ schema = 1; item = $Item }; comments = $all }
    }
    $m = [regex]::Match($found.body, '(?s)```json\s*(.*?)\s*```')
    $s = if ($m.Success) { $m.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String } else { @{ schema = 1; item = $Item } }
    return @{ commentId = $found.id; state = $s; comments = $all }
}

function Save-State([int]$Item, $Holder) {
    $s = $Holder.state; $s.item = $Item; $s.updated = ConvertTo-Iso (Get-UtcNow)
    $json = $s | ConvertTo-Json -Depth 12
    $summary = "state of #$Item for ``$((Get-Cfg).name)`` (machine-read; edits by others are ignored)"
    $body = "$(Get-StateMarker)`n$summary`n`n``````json`n$json`n```````n"
    # a state comment that is gone since it was read (deleted by a person) is written again as a new one, never an error
    if ($Holder.commentId) {
        $r0 = Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/issues/comments/$($Holder.commentId)" -Body @{ body = $body } -Allow 404
        if ($r0 -is [hashtable] -and $r0.ContainsKey('status') -and $r0.status -eq 404) { $Holder.commentId = $null }
    }
    if (-not $Holder.commentId) {
        $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$Item/comments" -Body @{ body = $body }
        $Holder.commentId = $r.id
    }
}

function Get-Park($State) {
    if (-not (Test-Has $State 'park')) { return $null }
    $until = ConvertFrom-Iso $State.park.until
    if ($until -and $until -lt (Get-UtcNow)) { return $null }
    return $State.park
}

function Set-Park($State, [string]$Reason, [double]$Minutes) {
    $State.park = @{ reason = $Reason; at = ConvertTo-Iso (Get-UtcNow); until = ConvertTo-Iso ((Get-UtcNow).AddMinutes($Minutes)) }
}

function Test-ClaimLive($State) {
    if (-not (Test-Has $State 'claim')) { return $false }
    $u = ConvertFrom-Iso $State.claim.lease_until
    return ($u -and $u -gt (Get-UtcNow))
}
