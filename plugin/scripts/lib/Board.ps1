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
    $card = $i.projectItems.nodes | Where-Object { $_.project.number -eq [int]$c.project } | Select-Object -First 1
    $status = $null; $cardId = $null
    if ($card) { $cardId = $card.id; if ($card.fieldValueByName) { $status = $card.fieldValueByName.name } }
    return @{
        number = [int]$i.number; nodeId = $i.id; open = ($i.state -eq 'OPEN'); title = $i.title; body = "$($i.body)"; url = $i.url; createdAt = $i.createdAt
        labels = @($i.labels.nodes | ForEach-Object { $_.name }); cardId = $cardId; column = $status
    }
}

function Test-Label($Info, [string]$Name) { Test-NameIn $Name $Info.labels }

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
    content{ ... on Issue{ number state title labels(first:30){ nodes{ name } } } } } } } } }
'@
    $after = $null; $out = @()
    do {
        $d = Invoke-GhGraphQL $q @{ o = $c.owner; n = [int]$c.project; after = $after }
        $items = $d.organization.projectV2.items
        foreach ($n in $items.nodes) {
            if (-not $n.content -or -not $n.content.PSObject.Properties['number']) { continue }
            $out += @{ cardId = $n.id; number = [int]$n.content.number; open = ($n.content.state -eq 'OPEN'); title = $n.content.title
                column = $(if ($n.fieldValueByName) { $n.fieldValueByName.name } else { $null })
                labels = @($n.content.labels.nodes | ForEach-Object { $_.name }) }
        }
        $after = $items.pageInfo.endCursor
    } while ($items.pageInfo.hasNextPage)
    return $out
}

function Close-Issue([int]$Item) {
    Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/issues/$Item" -Body @{ state = 'closed'; state_reason = 'completed' } | Out-Null
}

function Add-Comment([int]$Item, [string]$Text) {
    $body = "🤖 $((Get-Cfg).name): $Text`n`n<!-- factory-journal:$((Get-Cfg).name) -->"
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

# ---- the state is tamper-evident --------------------------------------------------------------------------------
# The author login alone proves little: anyone with write access to the repository can edit the gateway's comment. So the gateway signs what it
# writes (HMAC-SHA256 of the item number and the JSON text, keyed with FACTORY_TOKEN_SECRET) and checks the signature when it reads. A state
# that fails the check is treated as absent, and that is reported (a line on stderr at every read, a comment on the item when the gateway
# writes the state anew). States written before signing existed carry no signature: one is accepted only when its comment was created before
# timing.docsOnlyLegacyBefore (GitHub sets created_at; an edit cannot change it). A gateway without a token secret (local, not in owner mode)
# cannot sign or check: it writes and reads as before. A changed secret makes every signed state unreadable: change it only when no item is in flight.
function Get-LegacyCutoff { $t = (Get-Cfg).timing; if (Test-Has $t 'docsOnlyLegacyBefore') { return ConvertFrom-Iso $t.docsOnlyLegacyBefore }; return $null }
function Get-StateSignature([int]$Item, [string]$Json) {
    $sec = "$env:FACTORY_TOKEN_SECRET"; if ($sec.Length -lt 16) { return '' }
    $h = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($sec))
    try { return ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes("factory-state:v1`n$Item`n" + ($Json -replace "`r", '').Trim()))) -replace '-', '').ToLowerInvariant() }
    finally { $h.Dispose() }
}
# '' when the comment's state may be trusted, else the reason it may not.
function Test-StateComment([int]$Item, $Comment, [string]$Json) {
    $want = Get-StateSignature $Item $Json
    if ($want -eq '') { return '' }
    $m = [regex]::Match("$($Comment.body)", '<!-- factory-state-sig v1 ([0-9a-f]{64}) -->')
    if ($m.Success) {
        if ([Security.Cryptography.CryptographicOperations]::FixedTimeEquals([Text.Encoding]::ASCII.GetBytes($m.Groups[1].Value), [Text.Encoding]::ASCII.GetBytes($want))) { return '' }
        return 'the signature does not match the content'
    }
    $cut = Get-LegacyCutoff; if ($null -eq $cut) { return '' }
    $made = try { ConvertFrom-Iso $Comment.created_at } catch { $null }
    if ($made -and $made -lt $cut) { return '' }
    return 'it carries no signature and was not written before signing began'
}

# Returns @{ commentId; state } . Only a comment written by the gateway's own login counts, and only with a state that passes Test-StateComment.
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
            $bad0 = if ($m0.Success) { Test-StateComment $Item $old $m0.Groups[1].Value } else { '' }
            if ($bad0) { [Console]::Error.WriteLine("state of #$Item by $($old.user.login) is not adopted: $bad0") }
            elseif ($m0.Success) { return @{ commentId = $null; state = ($m0.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String); comments = $all; adopted_from = $old.user.login } }
        }
        return @{ commentId = $null; state = @{ schema = 1; item = $Item }; comments = $all }
    }
    $m = [regex]::Match($found.body, '(?s)```json\s*(.*?)\s*```')
    $bad = if ($m.Success) { Test-StateComment $Item $found $m.Groups[1].Value } else { '' }
    if ($bad) {
        # treated as absent; the comment is the gateway's own, so the next save writes the new state into it
        [Console]::Error.WriteLine("state of #$Item (comment $($found.id)) is ignored: $bad")
        return @{ commentId = $found.id; state = @{ schema = 1; item = $Item }; comments = $all; rejected = $bad }
    }
    $s = if ($m.Success) { $m.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String } else { @{ schema = 1; item = $Item } }
    return @{ commentId = $found.id; state = $s; comments = $all }
}

function Save-State([int]$Item, $Holder) {
    $s = $Holder.state; $s.item = $Item; $s.updated = ConvertTo-Iso (Get-UtcNow)
    $json = $s | ConvertTo-Json -Depth 12
    $summary = "state of #$Item for ``$((Get-Cfg).name)`` (machine-read; edits by others are ignored)"
    $sig = Get-StateSignature $Item $json
    $body = "$(Get-StateMarker)`n$summary`n`n``````json`n$json`n```````n$(if ($sig) { "<!-- factory-state-sig v1 $sig -->`n" })"
    if ($Holder.commentId) {
        Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/issues/comments/$($Holder.commentId)" -Body @{ body = $body } | Out-Null
    } else {
        $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$Item/comments" -Body @{ body = $body }
        $Holder.commentId = $r.id
    }
    if (Test-Has $Holder 'rejected') {
        $why = "$($Holder.rejected)"; $Holder.Remove('rejected')
        try { Add-Comment $Item "the recorded state of this item was not the gateway's own ($why): it was ignored and the state was started anew. Someone other than the gateway edited the state comment." } catch { }
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
