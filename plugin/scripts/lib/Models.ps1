# Model choice. Two halves, deliberately apart:
#   server (gateway): WHEN to escalate. Verb_model_tier turns an item's history (relaunches, review findings, demotions, fix-forward rounds, gate
#     refusals) into a tier number, never lower than before in the same column. A script rule, so a looping model cannot argue its way out of it.
#   client (this machine): WHICH model sits on each rung (~/.config/factory-worker/models.json), and what to run when the mode is fixed.
# The default ladder is cost-first: economy -> standard -> premium.

# ---- client half ------------------------------------------------------------------------------------------------------
function Get-ModelsConfig {
    $path = $env:FACTORY_MODELS_CONFIG
    if (-not $path) { $user = Get-FwConfigPath 'models.json'; if ($user -and (Test-Path $user)) { $path = $user } }
    if (-not $path) { $path = Join-Path $PSScriptRoot '../models.default.json' }
    return Get-Content -Raw $path | ConvertFrom-Json -AsHashtable
}

# The short name an Agent tool accepts when it does not take full model ids (haiku, sonnet, opus, fable); '' when the id is not recognised.
function Get-ModelAlias([string]$Model) {
    foreach ($a in 'haiku', 'sonnet', 'opus', 'fable') { if ($Model -match "(?i)^claude-$a(-|$)") { return $a } }
    return ''
}

# Resolve the model and effort for a role. -Tier overrides the gateway lookup (tests, explicit pins).
function Resolve-ModelFor([string]$Role, $Tier, $Cfg, [string]$Profile = '') {
    if ($Profile -and $Cfg.ContainsKey('profiles') -and $Cfg.profiles.ContainsKey($Profile)) {
        $p = $Cfg.profiles[$Profile]; return @{ role = $Role; mode = 'profile'; profile = $Profile; tier = $null; name = $Profile; model = $p.model; effort = $p.effort; escalates = [bool]$p.escalate }
    }
    if ($Cfg.mode -eq 'fixed') {
        $f = $Cfg.fixed[$Role]; return @{ role = $Role; mode = 'fixed'; tier = $null; name = 'fixed'; model = $f.model; effort = $f.effort }
    }
    $r = $Cfg[$Role]; $ladder = @($Cfg.ladder)
    $t = [Math]::Max([int]$r.startTier, [int]$Tier); $t = [Math]::Min($t, [Math]::Min([int]$r.maxTier, $ladder.Count - 1))
    $rung = $ladder[$t]
    return @{ role = $Role; mode = 'auto'; tier = $t; name = $rung.name; model = $rung.model; effort = $rung.effort }
}
