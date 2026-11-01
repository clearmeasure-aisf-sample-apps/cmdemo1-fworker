# The declared differences of the v2 engine (issue #468, W2.4b): the pure functions that read tests/scenarios/golden/v2-differences.txt and apply it to the
# golden files. Format and rules: tests/scenarios/golden/v2-differences.README.md. Dot-sourced by lib.ps1 (the Tier 1 harness) and by tests/smoke.ps1; it
# defines functions only and has no side effect. lib.ps1 reads them for every suite.
#
# The comparison itself is the one of lib.ps1 (Compare-WritesFiles), unchanged: a v2 run compares its record with an *effective* golden directory, a copy of
# tests/scenarios/golden in which each declared block has been applied to the golden file it names. So a caused ('= ') line v2 writes that no block declares
# fails the scenario exactly as a v1 difference does, because the effective file still holds the golden's line there. A '+ ' or '~ ' line of a block changes
# the background lines the scenario must write, in the same way.

# The section of a golden line, by its prefix: '= ' (caused) 1, '+ ' (settled background) 2, '~ ' (tail) 3; a header line ('# ') or anything else 0.
function Get-WritesLineRank([string]$Line) {
    if ($Line.StartsWith('= ', [StringComparison]::Ordinal)) { return 1 }
    if ($Line.StartsWith('+ ', [StringComparison]::Ordinal)) { return 2 }
    if ($Line.StartsWith('~ ', [StringComparison]::Ordinal)) { return 3 }
    return 0
}

# A block is complete when it has its '# why:' line and at least one '-' or '+' line; one that is not (or has a line in error) is marked bad and applied to nothing.
function Complete-V2Block($Block, $Errors) {
    if ("$($Block.why)".Trim() -eq '') { $Errors.Add("@ $($Block.name) (line $($Block.line)): the block has no '# why: <reason>; issue: #<n>' line"); $Block.bad = $true }
    if ($Block.changes.Count -eq 0) { $Errors.Add("@ $($Block.name) (line $($Block.line)): the block declares no '-' or '+' line"); $Block.bad = $true }
}

# The file as blocks: @{ blocks = ordered name -> @{ name; line; why; changes = list of @{ op ('-' or '+'); text (the golden line, prefix and all); line } }; errors = list of strings }.
# A line that is not understood, a block without '# why:' or without a change, a name twice and a name that is not <suite>/<slug> are errors.
function Read-V2Differences([string]$Path) {
    $blocks = [ordered]@{}
    $errors = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Path)) {
        $errors.Add("the declared differences file $Path does not exist")
        return @{ blocks = $blocks; errors = $errors }
    }
    $cur = $null; $n = 0
    foreach ($raw in [IO.File]::ReadAllLines($Path, [Text.UTF8Encoding]::new($false))) {
        $n++
        if ($raw.Trim() -eq '') {
            if ($null -ne $cur) { Complete-V2Block $cur $errors; $cur = $null }
            continue
        }
        if ($raw.StartsWith('@ ', [StringComparison]::Ordinal)) {
            if ($null -ne $cur) { Complete-V2Block $cur $errors }
            $name = $raw.Substring(2).Trim()
            $cur = @{ name = $name; line = $n; why = ''; bad = $false; changes = [System.Collections.Generic.List[hashtable]]::new() }
            if ($name -cnotmatch '^[a-z0-9][a-z0-9_-]*/[a-z0-9][a-z0-9_-]*$') { $errors.Add("line ${n}: '$name' is not <suite>/<slug>, the path of a golden file without .writes.txt"); $cur.bad = $true }
            elseif ($blocks.Contains($name)) { $errors.Add("line ${n}: the scenario $name has a block already (line $($blocks[$name].line))"); $blocks[$name].bad = $true; $cur.bad = $true }
            else { $blocks[$name] = $cur }
            continue
        }
        if ($raw.StartsWith('#', [StringComparison]::Ordinal)) {
            $m = [regex]::Match($raw, '^#\s*why:\s*(.*)$')
            if ($m.Success -and $null -ne $cur) { $cur.why = $m.Groups[1].Value.Trim() }
            continue   # (any other '#' line is a comment)
        }
        if ($raw.StartsWith('- ', [StringComparison]::Ordinal) -or $raw.StartsWith('+ ', [StringComparison]::Ordinal)) {
            if ($null -eq $cur) { $errors.Add("line ${n}: a '-' or '+' line outside a block (no '@ <suite>/<slug>' before it)"); continue }
            $text = $raw.Substring(2)
            if ((Get-WritesLineRank $text) -eq 0) { $errors.Add("line ${n}: '$raw' does not hold a golden line (it must start with '= ', '+ ' or '~ ' after the '-' or '+')"); $cur.bad = $true; continue }
            $cur.changes.Add(@{ op = $raw.Substring(0, 1); text = $text; line = $n })
            continue
        }
        $errors.Add("line ${n}: not understood (a block is '@ ', '-', '+' and '# why:' lines): $raw")
        if ($null -ne $cur) { $cur.bad = $true }
    }
    if ($null -ne $cur) { Complete-V2Block $cur $errors }
    return @{ blocks = $blocks; errors = $errors }
}

# The edits of a block, in file order. A '-' line immediately followed by a '+' line with the same prefix is a change (the line stays where it is); a '-' line
# alone is a removal and a '+' line alone an addition. Each edit: @{ kind = change | remove | add; old; new; line }.
function Get-V2Edits($Block) {
    $edits = [System.Collections.Generic.List[hashtable]]::new()
    $c = $Block.changes
    $i = 0
    while ($i -lt $c.Count) {
        $x = $c[$i]
        if ($x.op -eq '-') {
            if (($i + 1) -lt $c.Count -and $c[$i + 1].op -eq '+' -and (Get-WritesLineRank $c[$i + 1].text) -eq (Get-WritesLineRank $x.text)) {
                $edits.Add(@{ kind = 'change'; old = $x.text; new = $c[$i + 1].text; line = $x.line }); $i += 2
            }
            else { $edits.Add(@{ kind = 'remove'; old = $x.text; new = ''; line = $x.line }); $i++ }
        }
        else { $edits.Add(@{ kind = 'add'; old = ''; new = $x.text; line = $x.line }); $i++ }
    }
    return , $edits
}

# One block applied to the lines of its golden file (all of them, headers too): @{ lines = list; errors = list }. A '-' line must be a line of the golden file
# (the first one equal is the one removed or changed; a line the golden holds twice and a block names once stays once). An addition goes after the last line of
# its own section (or of an earlier one), so a '= ' line comes after the last caused line (after the last one of its object, since objects are compared apart).
function Merge-V2Block([string[]]$GoldenLines, $Block) {
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($l in $GoldenLines) { $lines.Add($l) }
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($e in (Get-V2Edits $Block)) {
        $where = "@ $($Block.name) (line $($e.line))"
        if ($e.kind -ne 'add') {
            $i = -1
            for ($k = 0; $k -lt $lines.Count; $k++) { if ([string]::Equals($lines[$k], $e.old, [StringComparison]::Ordinal)) { $i = $k; break } }
            if ($i -lt 0) { $errors.Add("${where}: the '-' line is not in the golden file: $($e.old)"); continue }
            if ($e.kind -eq 'remove') { $lines.RemoveAt($i); continue }
            if ([string]::Equals($e.old, $e.new, [StringComparison]::Ordinal)) { $errors.Add("${where}: the '+' line is the '-' line, so it declares no difference: $($e.old)"); continue }
            $lines[$i] = $e.new
            continue
        }
        $rank = Get-WritesLineRank $e.new
        $at = 0
        for ($k = 0; $k -lt $lines.Count; $k++) { if ((Get-WritesLineRank $lines[$k]) -le $rank) { $at = $k + 1 } }
        $lines.Insert($at, $e.new)
    }
    return @{ lines = $lines; errors = $errors }
}

# The effective golden directory of v2: -OutDir gets a copy of -GoldenDir in which every declared block is applied to the golden file it names. The file must stay
# true to the baseline (golden/v2-differences.README.md): a block for a scenario with no golden file, a '-' line the golden file does not hold, a block without
# '# why:' and a line not understood are errors, reported all at once; a block with an error (one of its own, or a second block of its scenario) is left out (its scenario compares with the plain golden file).
# With no valid block the directory returned is -GoldenDir itself and nothing is copied. Result: @{ dir; blocks (applied); errors }.
function New-V2EffectiveGolden([string]$GoldenDir, [string]$DifferencesPath, [string]$OutDir) {
    $diff = Read-V2Differences $DifferencesPath
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($e in $diff.errors) { $errors.Add($e) }
    $applied = @{}
    foreach ($name in @($diff.blocks.Keys)) {
        $b = $diff.blocks[$name]
        if ($b.bad) { continue }   # (its error is in the list already)
        $file = Join-Path $GoldenDir "$name.writes.txt"
        if (-not (Test-Path -LiteralPath $file)) { $errors.Add("@ $name (line $($b.line)): there is no golden file $name.writes.txt for this scenario"); continue }
        $m = Merge-V2Block ([IO.File]::ReadAllLines($file, [Text.UTF8Encoding]::new($false))) $b
        foreach ($e in $m.errors) { $errors.Add($e) }
        if ($m.errors.Count -eq 0) { $applied[$name] = $m.lines }
    }
    if ($applied.Count -eq 0) { return @{ dir = $GoldenDir; blocks = 0; errors = $errors } }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    Copy-Item -Path (Join-Path $GoldenDir '*') -Destination $OutDir -Recurse -Force
    foreach ($name in @($applied.Keys)) {
        [IO.File]::WriteAllText((Join-Path $OutDir "$name.writes.txt"), ((@($applied[$name]) -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    }
    return @{ dir = $OutDir; blocks = $applied.Count; errors = $errors }
}
