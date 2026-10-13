# No network, no secrets: parses every script and exercises the pure rule functions.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fail = 0
function Check($name, $ok) { if ($ok) { "  ok   $name" } else { "  FAIL $name"; $script:fail++ } }
"parse"
Get-ChildItem (Join-Path $root 'plugin/scripts') -Recurse -Filter *.ps1 | ForEach-Object {
    $errs = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errs)
    Check $_.Name ($errs.Count -eq 0); $errs | ForEach-Object { "       $($_.Message) line $($_.Extent.StartLineNumber)" }
}
$env:FACTORY_CONFIG = Join-Path $root 'config/instance.json'
foreach ($f in 'Core', 'Board', 'Prs', 'Verbs.Item', 'Apps', 'Verbs.Pr', 'Verbs.Inspect', 'Verbs.Merge', 'Verbs.Delivery', 'Verbs.Sweep', 'Models', 'Journal', 'Owner') { . (Join-Path $root "plugin/scripts/lib/$f.ps1") }
"rules"
Check 'docs-only: docs and md only' (Test-DocsOnlyFiles @(@{filename='docs/a.md'}, @{filename='README.md'}))
Check 'docs-only: src file is code' (-not (Test-DocsOnlyFiles @(@{filename='docs/a.md'}, @{filename='src/X.cs'})))
Check 'docs-only: workflow is code' (-not (Test-DocsOnlyFiles @(@{filename='.github/workflows/build.yml'})))
foreach ($f in 'docs/x.ps1', 'docs/ci.yml', 'src/UI/README.md', '.github/pull_request_template.md', 'CLAUDE.md', 'AGENTS.md', 'sub/CLAUDE.md', '.claude/skills/x/SKILL.md', '.cursor/rules/x.md', '.cursor/commands/y.md', 'plugin/skills/x/SKILL.md', 'LICENSE.exe', 'LICENSES/x.cs') { Check "docs-only: $f is not documentation" (-not (Test-DocsOnlyFiles @(@{filename=$f}))) }
foreach ($f in 'docs/a/b.md', 'docs/diagram.svg', 'README.md', 'arch/Workflow.md', 'LICENSE', 'LICENSE.txt') { Check "docs-only: $f is documentation" (Test-DocsOnlyFiles @(@{filename=$f})) }
Check 'docs-only: empty list is not docs-only' (-not (Test-DocsOnlyFiles @()))
$fpInfo = @{ body = "Criteria:`n- The guide names the route.`n`n## Technical Design`nFiles: docs/guide.md`n<!-- aisf:docs-only -->`n- add the sentence." }
Check 'fast path: a criterion that contains double quotes is matched whole' ((Get-GroundedFindings @{ body = "- The page says `"Ready`" in bold.`n## Technical Design`n- x" } 'Technical Design' @('FINDING: criterion "The page says "Ready" in bold." - not covered')).accepted.Count -eq 1)
$mk = "x`n<!-- aisf:docs-only -->"
$legacy = '2026-10-01T00:00:00Z'
Check 'docs-only flag: the body counts only for an item created before timing.docsOnlyLegacyBefore' ((Get-LegacyCutoff) -eq [DateTime]::new(2026, 10, 16, 0, 0, 0, [DateTimeKind]::Utc) -and -not (Test-ItemDocsOnly @{} @{ column = 'Development'; body = $mk; createdAt = '2026-10-16T00:00:00Z' }) -and -not (Test-ItemDocsOnly @{} @{ column = 'Development'; body = $mk; createdAt = [DateTime]::UtcNow.AddYears(1) }) -and -not (Test-ItemDocsOnly @{} @{ column = 'Development'; body = $mk }) -and (Test-ItemDocsOnly @{} @{ column = 'Development'; body = $mk; createdAt = '2026-10-15T23:59:59Z' }))
Check 'fast path: the quote of a finding ends at the first separator when that grounds it, so a remark with quotes and a dash is not read into it' ((Get-GroundedFindings $fpInfo 'Technical Design' @('FINDING: criterion "The guide names the route." - the design says "the route" - which one?')).accepted[0].quote -eq 'The guide names the route.' -and (Get-GroundedFindings @{ body = "- The label reads `"A`" - not `"B`" in bold.`n## Technical Design`n- x" } 'Technical Design' @('FINDING: criterion "The label reads "A" - not "B" in bold." - missing')).accepted.Count -eq 1)
Check 'docs-only flag: the field decides once it exists; only a field-less state past the design column reads the body' ((Test-ItemDocsOnly @{} @{ column = 'Development'; body = $mk; createdAt = $legacy }) -and -not (Test-ItemDocsOnly @{ design = @{ docs_only = $false } } @{ column = 'Development'; body = $mk }) -and -not (Test-ItemDocsOnly @{} @{ column = 'Technical Design'; body = $mk }) -and (Test-ItemDocsOnly @{ design = @{ docs_only = $true } } @{ column = 'Development'; body = 'x' }))
$gf = Get-GroundedFindings $fpInfo 'Technical Design' @('FINDING: criterion "The guide names the route." - not covered', '- FINDING: file "docs/guide.md" - outside the scope', 'FINDING: criterion "add the sentence." - vague', 'FINDING: better wording', 'a remark')
Check 'fast path: only findings that quote the item count' ($gf.accepted.Count -eq 2 -and $gf.dropped -eq 2)
Check 'fast path: the design review of a marked item, and only that' ((Test-DocsOnlyDesignReview @{ design = @{ docs_only = $true } } 'Technical Design') -and -not (Test-DocsOnlyDesignReview @{ design = @{ docs_only = $true } } 'Development') -and -not (Test-DocsOnlyDesignReview @{ design = @{ docs_only = $false } } 'Technical Design') -and -not (Test-DocsOnlyDesignReview @{} 'Technical Design'))
$pr = [pscustomobject]@{ head = [pscustomobject]@{ ref = 'factory-worker/12-development' }; title = 'x (#12)' }
Check 'belongs: branch /12-' (Test-PrBelongs $pr 12)
$pr2 = [pscustomobject]@{ head = [pscustomobject]@{ ref = 'other/branch' }; title = 'fix #123' }
Check 'belongs: #12 does not match #123' (-not (Test-PrBelongs $pr2 12))
Check 'belongs: #123 matches' (Test-PrBelongs $pr2 123)
Check 'column order' ((Get-ColumnIndex 'Development') -eq 1 -and (Get-ColumnIndex 'Nope') -eq -1)
Check 'name in list is case-insensitive' (Test-NameIn 'FACTORY WORKER' @('factory worker'))
try { Reject 10 'x' 'y' } catch { Check 'reject carries exit and code' ($_.Exception.Exit -eq 10 -and $_.Exception.Code -eq 'x') }
$d = '{"a":"2026-10-10T21:09:32.250Z"}' | ConvertFrom-Json -AsHashtable -DateKind String
Check 'state dates stay strings through the JSON round trip (milliseconds kept)' ((ConvertTo-Iso (ConvertFrom-Iso $d.a)) -eq '2026-10-10T21:09:32.250Z')
$d2 = '{"a":"2026-10-10T21:09:32.250Z"}' | ConvertFrom-Json -AsHashtable
Check 'a DateTime from the default parser keeps its milliseconds' ((ConvertTo-Iso (ConvertFrom-Iso $d2.a)) -eq '2026-10-10T21:09:32.250Z')
Check 'iso roundtrip' ((ConvertTo-Iso (ConvertFrom-Iso '2026-10-10T12:00:00.250Z')) -eq '2026-10-10T12:00:00.250Z' -and (ConvertFrom-Iso '2026-10-10T12:00:00Z') -lt (ConvertFrom-Iso '2026-10-10T12:00:00.001Z'))
"model ladder"
Check 'tier thresholds: below, at, past' (((Get-TierFor 1 @(2,3)) -eq 0) -and ((Get-TierFor 2 @(2,3)) -eq 1) -and ((Get-TierFor 3 @(2,3)) -eq 2) -and ((Get-TierFor 99 @(2,3)) -eq 2))
$mc = Get-Content -Raw (Join-Path $root 'plugin/scripts/models.default.json') | ConvertFrom-Json -AsHashtable
$m0 = Resolve-ModelFor 'orchestrator' 0 $mc; $m1 = Resolve-ModelFor 'subagent' 1 $mc; $m2 = Resolve-ModelFor 'subagent' 2 $mc; $m9 = Resolve-ModelFor 'subagent' 9 $mc
Check 'default ladder starts on haiku 5.5 at xhigh' ($m0.model -eq 'claude-haiku-5-5' -and $m0.effort -eq 'xhigh')
Check 'tier 1 is sonnet 5.5 at medium' ($m1.model -eq 'claude-sonnet-5-5' -and $m1.effort -eq 'medium')
Check 'tier 2 is opus 5.5 at medium' ($m2.model -eq 'claude-opus-5-5' -and $m2.effort -eq 'medium')
Check 'a tier past the ladder is capped at the top rung' ($m9.model -eq 'claude-opus-5-5')
$cap = $mc | ConvertTo-Json -Depth 6 | ConvertFrom-Json -AsHashtable; $cap.subagent.maxTier = 1
Check 'a machine can cap the ladder' ((Resolve-ModelFor 'subagent' 2 $cap).model -eq 'claude-sonnet-5-5')
$st0 = $mc | ConvertTo-Json -Depth 6 | ConvertFrom-Json -AsHashtable; $st0.orchestrator.startTier = 1
Check 'a machine can raise the starting rung' ((Resolve-ModelFor 'orchestrator' 0 $st0).model -eq 'claude-sonnet-5-5')
$fx = $mc | ConvertTo-Json -Depth 6 | ConvertFrom-Json -AsHashtable; $fx.mode = 'fixed'
Check 'fixed mode ignores the tier' ((Resolve-ModelFor 'subagent' 2 $fx).model -eq 'claude-sonnet-5-5' -and (Resolve-ModelFor 'subagent' 2 $fx).mode -eq 'fixed')
$tp = Resolve-ModelFor 'subagent' 2 $mc 'testing'
Check 'testing profile is haiku 5.5 at low effort' ($tp.model -eq 'claude-haiku-5-5' -and $tp.effort -eq 'low' -and $tp.mode -eq 'profile')
Check 'testing profile never climbs, whatever the tier' ((Resolve-ModelFor 'orchestrator' 2 $mc 'testing').model -eq 'claude-haiku-5-5' -and -not $tp.escalates)
Check 'an unknown profile falls back to the ladder' ((Resolve-ModelFor 'subagent' 1 $mc 'nope').model -eq 'claude-sonnet-5-5')
Check 'the testing profile wins over fixed mode too' ((Resolve-ModelFor 'subagent' 0 $fx 'testing').effort -eq 'low')
if ($fail) { "FAILED: $fail"; exit 1 } else { 'all passed' }
