param(
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\content_refs_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\content_refs_test\$sessionId"
$pluginRoot = Join-Path $testRoot "plugins\missing_content_refs"
$configPath = Join-Path $projectRoot.Path "config\_content_refs_test_$sessionId.json"

function Invoke-LoaderRaw {
    param([string[]]$LoaderArgs)

    $output = & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    return [int]$exitCode
}

Push-Location $projectRoot.Path
try {
    Invoke-DdrtLoader -LoaderArgs @("--config", "config/rule_contract_validation_config.json", "--explain-patches", "--no-inject")

    $contractReportPath = Join-Path $projectRoot.Path "state\mod_state\_content_refs\validation.content_refs_contract\content_refs.validation.json"
    Assert-DdrtTrue (Test-Path -LiteralPath $contractReportPath -PathType Leaf) "Content refs validation report was not written: $contractReportPath"
    $contractReport = Get-Content -Raw -LiteralPath $contractReportPath | ConvertFrom-Json
    Assert-DdrtTrue ([int]$contractReport.referenceCount -eq 31) "Validation content refs contract should declare 31 references."
    Assert-DdrtTrue ([int]$contractReport.satisfiedCount -eq 31) "Validation content refs contract should satisfy every reference."
    Assert-DdrtTrue ([int]$contractReport.missingRequiredCount -eq 0) "Validation content refs contract should not miss required references."
    Assert-DdrtTrue ([int]$contractReport.missingOptionalCount -eq 0) "Validation content refs contract should not miss optional references."

    New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
    $manifest = [ordered]@{
        id = "validation.content_refs_missing_contract"
        name = "Validation - Missing Content References Contract"
        version = "0.1.0"
        enabled = $true
        capabilities = @("content_refs.validate")
        contentRefs = [ordered]@{
            quests = @(
                [ordered]@{
                    id = "plot_tutorial_crypts"
                    provider = "base"
                },
                [ordered]@{
                    id = "missing_required_quest"
                    provider = "plugin"
                    required = $true
                },
                [ordered]@{
                    id = "missing_optional_quest"
                    provider = "plugin"
                    required = $false
                }
            )
        }
        virtualFileRules = @()
        mapTemplates = @()
        mapLayoutTemplates = @()
        questChains = @()
        eventRules = @()
        factEventRules = @()
        stateSchema = [ordered]@{}
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $pluginRoot "patches.json") -Encoding UTF8

    $config = [ordered]@{
        gameExecutablePath = (Join-Path $GameDirectory "_windows\win64\Darkest.exe")
        gameWorkingDirectory = $GameDirectory
        runtimeDllPath = "./runtime/bin/x64/Release/RuntimeHook.dll"
        logDirectory = "./logs"
        modStateDirectory = $stateRoot
        enableInjection = $false
        killGameOnInjectionFailure = $false
        startSuspendedForInjection = $false
        fileIoObserveOnly = $true
        fileIoLogExtensions = @(".json")
        fileIoMaxLogEntries = 20
        fileIoDeduplicate = $true
        eventProbeEnabled = $false
        saveWatchEnabled = $false
        saveWatchDirectories = @()
        pluginDirectories = @($pluginRoot)
        pluginPatchManifestName = "patches.json"
        virtualFileEnabled = $true
        virtualFileTarget = ""
        virtualFileFind = ""
        virtualFileReplace = ""
        virtualFileRules = @()
    }
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8

    Invoke-DdrtLoader -LoaderArgs @("--config", $configPath, "--explain-patches", "--no-inject")
    $missingReportPath = Join-Path $stateRoot "_content_refs\validation.content_refs_missing_contract\content_refs.validation.json"
    Assert-DdrtTrue (Test-Path -LiteralPath $missingReportPath -PathType Leaf) "Missing content refs report was not written: $missingReportPath"
    $missingReport = Get-Content -Raw -LiteralPath $missingReportPath | ConvertFrom-Json
    Assert-DdrtTrue ([int]$missingReport.referenceCount -eq 3) "Missing content refs contract should declare three references."
    Assert-DdrtTrue ([int]$missingReport.satisfiedCount -eq 1) "Missing content refs contract should satisfy the base reference."
    Assert-DdrtTrue ([int]$missingReport.missingRequiredCount -eq 1) "Missing content refs contract should report one missing required reference."
    Assert-DdrtTrue ([int]$missingReport.missingOptionalCount -eq 1) "Missing content refs contract should report one missing optional reference."

    $exitCode = Invoke-LoaderRaw -LoaderArgs @("--config", $configPath, "--validate-only", "--no-inject")
    Assert-DdrtTrue ($exitCode -ne 0) "Validate-only should fail when a required content reference is missing."

    Write-Host "PASS: contentRefs validates base/plugin content, reports optional misses, and blocks missing required content."
}
finally {
    Pop-Location
}
