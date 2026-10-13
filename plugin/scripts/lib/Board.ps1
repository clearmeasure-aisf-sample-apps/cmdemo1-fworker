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
# writes: HMAC-SHA256 of the item number and the JSON text, keyed with FACTORY_TOKEN_SECRET, with a key id (the first 8 hex of the SHA-256 of
# the secret) so that "signed with another key" is told apart from "edited". FACTORY_TOKEN_SECRET_PREVIOUS is accepted when reading, so a
# changed secret does not orphan the items in flight; the next write signs with the current one.
# A state that cannot be verified is NEVER overwritten or removed by the gateway: reads see no state and say why (holder.rejected), every write
# is refused (exit 30 state-unverifiable), the sweep leaves the item alone and reports it once on the item. The operator decides (restore the
# comment from its edit history, give the gateway the key that signed it, or delete the comment to start the item anew).
# States written before signing existed carry no signature: one is accepted only when its comment was created AND last edited before
# timing.docsOnlyLegacyBefore (GitHub sets both; taking the signature off a comment is an edit). A gateway without a token secret (local, not
# in owner mode) cannot sign or check: it writes and reads as before.
function Get-LegacyCutoff { $t = (Get-Cfg).timing; if (Test-Has $t 'docsOnlyLegacyBefore') { return ConvertFrom-Iso $t.docsOnlyLegacyBefore }; return $null }
function Get-StateKeyId([string]$Secret) { ([BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Secret))) -replace '-', '').ToLowerInvariant().Substring(0, 8) }
function Get-StateSignature([int]$Item, [string]$Json, [string]$Secret = "$env:FACTORY_TOKEN_SECRET") {
    if ($Secret.Length -lt 16) { return '' }
    $h = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    try { return ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes("factory-state:v1`n$Item`n" + ($Json -replace "`r", '').Trim()))) -replace '-', '').ToLowerInvariant() }
    finally { $h.Dispose() }
}
# The signature line of a state, or '' for a gateway that cannot sign.
function Get-StateSignatureLine([int]$Item, [string]$Json) {
    $sig = Get-StateSignature $Item $Json
    if ($sig -eq '') { return '' }
    return "<!-- factory-state-sig v1 $(Get-StateKeyId "$env:FACTORY_TOKEN_SECRET") $sig -->"
}
# @{ why; code }: why is '' when the comment's state may be trusted; else the reason, with code 'edited', 'other-key' or 'unsigned'.
function Test-StateComment([int]$Item, $Comment, [string]$Json) {
    $ok = @{ why = ''; code = '' }
    $cur = "$env:FACTORY_TOKEN_SECRET"; if ($cur.Length -lt 16) { return $ok }
    $m = [regex]::Match("$($Comment.body)", '<!-- factory-state-sig v1 ([0-9a-f]{8}) ([0-9a-f]{64}) -->')
    if ($m.Success) {
        $kid = $m.Groups[1].Value; $prev = "$env:FACTORY_TOKEN_SECRET_PREVIOUS"
        $key = if ($kid -eq (Get-StateKeyId $cur)) { $cur } elseif ($prev.Length -ge 16 -and $kid -eq (Get-StateKeyId $prev)) { $prev } else { $null }
        if ($null -eq $key) { return @{ why = "it is signed by another key ($kid) than this gateway's: another gateway wrote it, or the token secret changed"; code = 'other-key' } }
        if ([Security.Cryptography.CryptographicOperations]::FixedTimeEquals([Text.Encoding]::ASCII.GetBytes($m.Groups[2].Value), [Text.Encoding]::ASCII.GetBytes((Get-StateSignature $Item $Json $key)))) { return $ok }
        return @{ why = 'its content does not match its signature: someone other than the gateway edited it'; code = 'edited' }
    }
    $cut = Get-LegacyCutoff; if ($null -eq $cut) { return $ok }
    $made = try { ConvertFrom-Iso $Comment.created_at } catch { $null }
    $edited = try { ConvertFrom-Iso $Comment.updated_at } catch { $null }
    if ($made -and $edited -and $made -lt $cut -and $edited -lt $cut) { return $ok }
    return @{ why = 'it carries no signature and was written or edited after signing began'; code = 'unsigned' }
}

# Returns @{ commentId; state } . Only a comment written by the gateway's own login counts, and only with a state that passes Test-StateComment;
# one that does not gives an empty state and `rejected` (why) and `rejectedCode`, and Save-State refuses to write over it.
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
            $bad0 = if ($m0.Success) { (Test-StateComment $Item $old $m0.Groups[1].Value).why } else { '' }
            if ($bad0) { [Console]::Error.WriteLine("state of #$Item by $($old.user.login) is not adopted: $bad0") }
            elseif ($m0.Success) { return @{ commentId = $null; state = ($m0.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String); comments = $all; adopted_from = $old.user.login } }
        }
        return @{ commentId = $null; state = @{ schema = 1; item = $Item }; comments = $all }
    }
    $m = [regex]::Match($found.body, '(?s)```json\s*(.*?)\s*```')
    # (a gateway that signs never writes a state comment without the block: one without it was edited, and is kept as it is like any other)
    $bad = if ($m.Success) { Test-StateComment $Item $found $m.Groups[1].Value } elseif ("$env:FACTORY_TOKEN_SECRET".Length -ge 16) { @{ why = 'it holds no state block: someone other than the gateway edited it'; code = 'edited' } } else { @{ why = ''; code = '' } }
    if ($bad.why) {
        [Console]::Error.WriteLine("state of #$Item (comment $($found.id)) is not read: $($bad.why)")
        return @{ commentId = $found.id; state = @{ schema = 1; item = $Item }; comments = $all; rejected = $bad.why; rejectedCode = $bad.code }
    }
    $s = if ($m.Success) { $m.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String } else { @{ schema = 1; item = $Item } }
    return @{ commentId = $found.id; state = $s; comments = $all }
}

# Refuses (exit 30 state-unverifiable) when the item's state could not be verified. Save-State calls it; a verb that changes something on GitHub
# before it saves (the claim ref) calls it first.
function Assert-StateReadable([int]$Item, $Holder) {
    if (Test-Has $Holder 'rejected') { Reject 30 'state-unverifiable' "the recorded state of item #$Item cannot be verified ($($Holder.rejected)); nothing is written until the operator has resolved it" @{ state_code = "$($Holder.rejectedCode)" } }
}

function Save-State([int]$Item, $Holder) {
    # nothing is ever written over a state that could not be verified: a wrong key (a rotated secret, another environment's gateway on the same
    # board) would otherwise wipe the claim, the reviews and the verdict of every item in flight
    Assert-StateReadable $Item $Holder
    $s = $Holder.state; $s.item = $Item; $s.updated = ConvertTo-Iso (Get-UtcNow)
    $json = $s | ConvertTo-Json -Depth 12
    $summary = "state of #$Item for ``$((Get-Cfg).name)`` (machine-read; edits by others are ignored)"
    $sig = Get-StateSignatureLine $Item $json
    $body = "$(Get-StateMarker)`n$summary`n`n``````json`n$json`n```````n$(if ($sig) { "$sig`n" })"
    if ($Holder.commentId) {
        Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/issues/comments/$($Holder.commentId)" -Body @{ body = $body } | Out-Null
    } else {
        $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$Item/comments" -Body @{ body = $body }
        $Holder.commentId = $r.id
    }
}

# The sweep found an item whose state cannot be verified: said once on the item (per state comment and reason), never per sweep.
function Add-StateUnverifiableNote([int]$Item, $Holder) {
    $mark = "<!-- factory-state-unverifiable comment=$($Holder.commentId) code=$($Holder.rejectedCode) -->"
    if (@($Holder.comments | Where-Object { "$($_.body)".Contains($mark) }).Count -gt 0) { return $false }
    $what = if ("$($Holder.rejectedCode)" -eq 'other-key') { 'It is signed by another key than this gateway''s (another gateway on this board, or a changed token secret); that is no sign of tampering.' } else { "It cannot be verified: $($Holder.rejected)." }
    Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$Item/comments" -Body @{ body = "🤖 $((Get-Cfg).name): the recorded state of this item is not read and not changed. $what The item waits for the operator: restore the state comment, give the gateway the key that signed it (FACTORY_TOKEN_SECRET_PREVIOUS), or delete the state comment to start the item anew.`n`n$mark" } | Out-Null
    return $true
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
