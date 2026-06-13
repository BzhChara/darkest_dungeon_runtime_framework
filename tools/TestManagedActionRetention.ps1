param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot.Path "state\managed_action_retention_test\$sessionId"
$artifactRoot = Join-Path $stateRoot "_managed_actions"
$configPath = Join-Path $stateRoot "config.json"
$reportPath = Join-Path $projectRoot.Path "logs\managed_action_retention_report.json"

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

function Write-TestConfig {
    $config = Get-Content -Raw -LiteralPath (Join-Path $projectRoot.Path "config\default_config.json") | ConvertFrom-Json
    $config.modStateDirectory = $stateRoot
    $config.enableInjection = $false
    $config.managedActionRetentionKeepLatestPerGroup = 2
    Write-JsonFile $configPath $config
}

function Write-Artifact {
    param(
        [string]$Name,
        [string]$GeneratedAtUtc,
        [string]$PluginId,
        [string]$ProfileId
    )

    $path = Join-Path $artifactRoot $Name
    $profileScope = if ([string]::IsNullOrWhiteSpace($ProfileId)) {
        [ordered]@{
            kind = "global"
            profileId = ""
            profileRoot = ""
            source = ""
        }
    } else {
        [ordered]@{
            kind = "profile"
            profileId = $ProfileId
            profileRoot = "E:\Steam\userdata\1097809614\262060\remote\$ProfileId"
            source = "fixture"
        }
    }

    Write-JsonFile $path ([ordered]@{
        version = 1
        generatedAtUtc = $GeneratedAtUtc
        status = "materialized"
        eventId = "fixture.event"
        pluginId = $PluginId
        sourceName = "Retention Fixture"
        sourcePath = "fixtures/retention_source.json"
        loadOrder = 1
        ruleIndex = 1
        ruleId = "same_rule"
        actionIndex = 0
        profileScope = $profileScope
        action = [ordered]@{
            type = "questBoard.replaceWithFixedSet"
            capability = "quest_board.replace_with_fixed_set"
            risk = "managed"
            required = $false
        }
        payload = [ordered]@{}
        plan = [ordered]@{
            kind = "questBoard.replaceWithFixedSet"
            effect = "replaceWithFixedSet"
            target = "profile.quest_board"
            arguments = [ordered]@{
                target = "profile.quest_board"
                questIds = @("plot_kill_prophet_3")
            }
        }
    })
    (Get-Item -LiteralPath $path).LastWriteTimeUtc = [DateTimeOffset]::Parse($GeneratedAtUtc).UtcDateTime
    return $path
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Read-RetentionReport {
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) "Retention report was not written: $reportPath"
    return Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
}

Push-Location $projectRoot.Path
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    Write-TestConfig

    $oldProfile3 = Write-Artifact "001_old_profile3.json" "2026-06-01T00:00:00.0000000Z" "validation.retention" "profile_3"
    $midProfile3 = Write-Artifact "002_mid_profile3.json" "2026-06-02T00:00:00.0000000Z" "validation.retention" "profile_3"
    $newProfile3 = Write-Artifact "003_new_profile3.json" "2026-06-03T00:00:00.0000000Z" "validation.retention" "profile_3"
    $oldProfile4 = Write-Artifact "004_old_profile4.json" "2026-06-01T12:00:00.0000000Z" "validation.retention" "profile_4"
    $newProfile4 = Write-Artifact "005_new_profile4.json" "2026-06-02T12:00:00.0000000Z" "validation.retention" "profile_4"
    $invalid = Join-Path $artifactRoot "006_invalid.json"
    "{ invalid json" | Set-Content -LiteralPath $invalid -Encoding UTF8

    $baseArgs = @(
        "--config", $configPath,
        "--mod-state-dir", $stateRoot,
        "--no-inject"
    )

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "2"))
    $preview = Read-RetentionReport
    Assert-True ([string]$preview.mode -eq "dryRun") "Preview retention should use dryRun mode."
    Assert-True ([int]$preview.artifactCount -eq 6) "Preview should inspect all fixture artifacts."
    Assert-True ([int]$preview.groupCount -eq 2) "Preview should group by profile scope."
    Assert-True ([int]$preview.prunableCount -eq 1) "Preview should find one prunable artifact."
    Assert-True ([int]$preview.deletedCount -eq 0) "Preview must not delete artifacts."
    Assert-True ([int]$preview.warningCount -eq 1) "Preview should warn about the invalid artifact."
    Assert-True (Test-Path -LiteralPath $oldProfile3 -PathType Leaf) "Preview deleted the oldest profile_3 artifact."

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs ($baseArgs + @("--prune-managed-actions", "--managed-action-retention-keep", "2"))
    $prune = Read-RetentionReport
    Assert-True ([string]$prune.mode -eq "prune") "Prune retention should use prune mode."
    Assert-True ([int]$prune.prunableCount -eq 1) "Prune should still report one prunable artifact."
    Assert-True ([int]$prune.deletedCount -eq 1) "Prune should delete one artifact."
    Assert-True (-not (Test-Path -LiteralPath $oldProfile3 -PathType Leaf)) "Prune should delete the oldest profile_3 artifact."
    Assert-True (Test-Path -LiteralPath $midProfile3 -PathType Leaf) "Prune should retain the middle profile_3 artifact."
    Assert-True (Test-Path -LiteralPath $newProfile3 -PathType Leaf) "Prune should retain the newest profile_3 artifact."
    Assert-True (Test-Path -LiteralPath $oldProfile4 -PathType Leaf) "Prune should retain profile_4 artifacts in a separate group."
    Assert-True (Test-Path -LiteralPath $newProfile4 -PathType Leaf) "Prune should retain profile_4 artifacts in a separate group."
    Assert-True (Test-Path -LiteralPath $invalid -PathType Leaf) "Prune should retain invalid artifacts for manual inspection."

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "2"))
    $after = Read-RetentionReport
    Assert-True ([int]$after.artifactCount -eq 5) "Post-prune preview should see five remaining artifacts."
    Assert-True ([int]$after.prunableCount -eq 0) "Post-prune preview should find no prunable artifacts."

    Write-Host "PASS: managed action retention dry-run, prune, profile grouping, and invalid-artifact retention passed."
}
finally {
    Pop-Location
}
