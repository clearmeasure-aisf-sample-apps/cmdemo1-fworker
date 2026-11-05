# The instance configuration as the scripts read it, for the client (lib/Client.Messages.ps1: the secret patterns) and the server (server/rule.ps1, which also dot-sources this file):
# config/instance.json beside the plugin, or FACTORY_CONFIG, with the FACTORY_TIMING_<name> overrides of the tests. A missing file is a refusal (exit 30) where the server's Reject is loaded, an error elsewhere.
$script:Cfg = $null
function Get-FactoryRoot { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }

function Get-Cfg {
    if ($script:Cfg) { return $script:Cfg }
    $path = $env:FACTORY_CONFIG
    if (-not $path) { $path = Join-Path (Split-Path -Parent (Get-FactoryRoot)) 'config/instance.json' }
    if (-not (Test-Path $path)) { if (Get-Command Reject -ErrorAction SilentlyContinue) { Reject 30 'config-missing' "instance config not found: $path" }; throw "instance config not found: $path" }
    $script:Cfg = Get-Content -Raw $path | ConvertFrom-Json -AsHashtable
    # Test overrides: FACTORY_TIMING_<name>=value
    foreach ($k in @($script:Cfg.timing.Keys)) {
        $v = [Environment]::GetEnvironmentVariable("FACTORY_TIMING_$k")
        if ($v) { $script:Cfg.timing[$k] = [double]$v }
    }
    return $script:Cfg
}
