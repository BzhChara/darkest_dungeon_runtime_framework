param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot "logs\managed_action_producer_staleness_test\$sessionId"
$pluginCollectionRoot = Join-Path $testRoot "plugins"
$pluginRoot = Join-Path $pluginCollectionRoot "producer_staleness"
$manifestPath = Join-Path $pluginRoot "patches.json"
$logRoot = Join-Path $testRoot "runtime_logs"
$stateRoot = Join-Path $projectRoot "state\managed_action_producer_staleness_test\$sessionId"
$configPath = Join-Path $testRoot "config.json"
$retentionReportPath = Join-Path $logRoot "managed_action_retention_report.json"
$pluginId = "validation.managed_action_producer_staleness"
$eventId = "validation.managed_action_producer_staleness"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Value
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Copy-JsonObject {
    param([object]$Value)

    return $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Read-RetentionArtifact {
    param([string]$ArtifactPath)

    Assert-True (Test-Path -LiteralPath $retentionReportPath -PathType Leaf) "Retention report was not written: $retentionReportPath"
    $report = Get-Content -Raw -LiteralPath $retentionReportPath | ConvertFrom-Json
    $matches = @($report.artifacts | Where-Object { $_.artifactPath -eq $ArtifactPath })
    Assert-True ($matches.Count -eq 1) "Retention report should contain exactly one row for the generated artifact."
    return [pscustomobject]@{
        Report = $report
        Artifact = $matches[0]
    }
}

$originalManifest = [ordered]@{
    id = $pluginId
    name = "Validation - Managed Action Producer Staleness"
    version = "0.1.0"
    enabled = $true
    capabilities = @("state.sidecar", "town_event.override_current")
    virtualFileRules = @()
    mapTemplates = @()
    mapLayoutTemplates = @()
    questChains = @()
    eventRules = @(
        [ordered]@{
            id = "materialize_original_town_event"
            enabled = $true
            on = $eventId
            phase = "normal"
            priority = 0
            requiresCapabilities = @("town_event.override_current")
            actions = @(
                [ordered]@{
                    type = "townEvent.overrideCurrent"
                    capability = "town_event.override_current"
                    risk = "managed"
                    required = $true
                    args = [ordered]@{
                        target = "profile.town_event"
                        mode = "suppress"
                    }
                }
            )
        }
    )
    factEventRules = @()
    stateSchema = [ordered]@{
        initialized = [ordered]@{
            type = "boolean"
            default = $false
        }
    }
}

Push-Location $projectRoot
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    Write-JsonFile $manifestPath $originalManifest
    $config = Get-Content -Raw -LiteralPath (Join-Path $projectRoot "config\default_config.json") | ConvertFrom-Json
    $config.pluginDirectories = @($pluginCollectionRoot)
    $config.logDirectory = $logRoot
    $config.modStateDirectory = $stateRoot
    $config.enableInjection = $false
    $config.managedActionRetentionKeepLatestPerGroup = 1
    Write-JsonFile $configPath $config

    $baseArgs = @(
        "--config", $configPath,
        "--mod-state-dir", $stateRoot,
        "--no-inject"
    )
    Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state", "--mod-state-id", $pluginId))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", $eventId, "--mod-state-id", $pluginId))

    $runtimeEventReportPath = Join-Path $logRoot "runtime_event_report.json"
    $runtimeEventReport = Get-Content -Raw -LiteralPath $runtimeEventReportPath | ConvertFrom-Json
    Assert-True ([int]$runtimeEventReport.materializedActionCount -eq 1) "The original manifest should materialize exactly one managed action."
    $materializedAction = @($runtimeEventReport.rules.actions | Where-Object { $_.type -eq "townEvent.overrideCurrent" })[0]
    $artifactPath = [string]$materializedAction.artifactPath
    Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "The original managed action artifact was not written: $artifactPath"
    $artifact = Get-Content -Raw -LiteralPath $artifactPath | ConvertFrom-Json
    Assert-True ([int]$artifact.version -eq 2) "The generated managed action should use artifact version 2."

    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "1"))
    $baseline = Read-RetentionArtifact -ArtifactPath $artifactPath
    Assert-True ([bool]$baseline.Artifact.eligible) "The artifact should be eligible before its manifest changes."
    Assert-True ([string]$baseline.Artifact.decision -eq "retain") "The current artifact should be retained."
    Assert-True ([int]$baseline.Report.groupCount -eq 0) "An action without a complete retention structure validator must not enter chronological ranking."
    Assert-True ($null -eq $baseline.Artifact.rankInGroup) "An unranked eligible artifact should not receive a retention rank."

    $mutatedManifest = Copy-JsonObject $originalManifest
    $mutatedManifest.eventRules[0].actions[0].args.target = "profile.town_event.changed"
    Write-JsonFile $manifestPath $mutatedManifest
    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "1"))
    $definitionMismatch = Read-RetentionArtifact -ArtifactPath $artifactPath
    Assert-True (-not [bool]$definitionMismatch.Artifact.eligible) "Changing action arguments should make the old artifact ineligible."
    Assert-True ([string]$definitionMismatch.Artifact.eligibilityCode -eq "managed-artifact-producer-definition-mismatch") "Changing action arguments should report producer-definition-mismatch."
    Assert-True ([string]$definitionMismatch.Artifact.decision -eq "wouldDelete") "A definition-mismatched artifact should be explicitly prunable."

    $prependedManifest = Copy-JsonObject $originalManifest
    $insertedRule = Copy-JsonObject $prependedManifest.eventRules[0]
    $insertedRule.id = "inserted_before_original"
    $insertedRule.on = "validation.managed_action_producer_staleness.inserted"
    $prependedManifest.eventRules = @($insertedRule, $prependedManifest.eventRules[0])
    Write-JsonFile $manifestPath $prependedManifest
    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "1"))
    $shiftedRule = Read-RetentionArtifact -ArtifactPath $artifactPath
    Assert-True (-not [bool]$shiftedRule.Artifact.eligible) "Moving the original rule to a new index should make the old producer identity inactive."
    Assert-True ([string]$shiftedRule.Artifact.eligibilityCode -eq "managed-artifact-producer-inactive") "A shifted rule index should report producer-inactive."
    Assert-True ([string]$shiftedRule.Artifact.decision -eq "wouldDelete") "An artifact from an inactive producer should be explicitly prunable."

    $removedManifest = Copy-JsonObject $originalManifest
    $removedManifest.eventRules = @()
    Write-JsonFile $manifestPath $removedManifest
    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "1"))
    $removedRule = Read-RetentionArtifact -ArtifactPath $artifactPath
    Assert-True (-not [bool]$removedRule.Artifact.eligible) "Removing the original rule should make its artifact ineligible."
    Assert-True ([string]$removedRule.Artifact.eligibilityCode -eq "managed-artifact-producer-inactive") "A removed producer should report producer-inactive."
    Assert-True ([string]$removedRule.Artifact.decision -eq "wouldDelete") "An artifact from a removed producer should be explicitly prunable."
    Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Dry-run retention must not delete the stale artifact."

    Write-Host "PASS: real manifest mutation invalidates stale managed action producers without pruning during preview."
}
finally {
    Pop-Location
}
