param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot "state\project_root_resolution_test\$sessionId"
$configRoot = Join-Path $testRoot "launch_configs"
$logPath = Join-Path $projectRoot "logs\project_root_resolution_test\$sessionId\launcher.log"

New-Item -ItemType Directory -Force -Path $configRoot | Out-Null

$sourceConfigPath = Get-DdrtResolvedPath `
    -Path $ConfigPath `
    -Leaf `
    -MissingMessage "Config file was not found: $ConfigPath"

$config = Get-Content -Raw -LiteralPath $sourceConfigPath | ConvertFrom-Json
$config.logDirectory = "./logs/project_root_resolution_test/$sessionId"
$config.modStateDirectory = "./state/project_root_resolution_test/$sessionId/mod_state"
$config.enableInjection = $false
$config.saveWatchEnabled = $false
$config.saveEventBridgeEnabled = $false

$probeConfigPath = Join-Path $configRoot "config.json"
$config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $probeConfigPath -Encoding UTF8

Invoke-DdrtLoader -LoaderArgs @(
    "--config", $probeConfigPath,
    "--no-inject",
    "--dry-run"
)

Assert-DdrtTrue (Test-Path -LiteralPath $logPath -PathType Leaf) "Expected launcher log was not written under the project root: $logPath"
$log = Get-Content -Raw -LiteralPath $logPath
Assert-DdrtContainsText -Text $log -Needle "Project root: $projectRoot" -Message "Launcher should resolve project root from a config stored below state/."
Assert-DdrtContainsText -Text $log -Needle "Config: $probeConfigPath" -Message "Launcher log should identify the nested config probe."
Assert-DdrtContainsText -Text $log -Needle "Dry run requested. No process was started." -Message "Project root probe should remain a dry run."

Write-Host "PASS: nested config project root resolution assertions passed."
