param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ManagedActionProducerTestHelpers.ps1")

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot.Path "state\managed_action_retention_test\$sessionId"
$artifactRoot = Join-Path $stateRoot "_managed_actions"
$configPath = Join-Path $stateRoot "config.json"
$reportPath = Join-Path $projectRoot.Path "logs\managed_action_retention_report.json"
$junctionStateRoot = Join-Path $projectRoot.Path "state\managed_action_retention_junction_test\$sessionId"
$junctionArtifactRoot = Join-Path $junctionStateRoot "_managed_actions"
$junctionTargetRoot = Join-Path $projectRoot.Path "logs\managed_action_retention_junction_target\$sessionId"
$identityStateRoot = Join-Path $projectRoot.Path "state\managed_action_retention_identity_test\$sessionId"
$identityArtifactRoot = Join-Path $identityStateRoot "_managed_actions"
$identityPluginRoot = Join-Path $identityStateRoot "plugins"
$identityPluginDirectory = Join-Path $identityPluginRoot "duplicate_rule_identity"
$identityManifestPath = Join-Path $identityPluginDirectory "patches.json"
$identityConfigPath = Join-Path $identityStateRoot "config.json"

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
    $config.pluginDirectories = @("./plugins/_validation")
    $config.managedActionRetentionKeepLatestPerGroup = 2
    Write-JsonFile $configPath $config
}

function Write-Artifact {
    param(
        [string]$Name,
        [string]$GeneratedAtUtc,
        [string]$PluginId,
        [string]$ProfileId,
        [string]$ArtifactDirectory = $script:artifactRoot,
        [object]$Producer = $script:questBoardProducer,
        [System.Collections.IDictionary]$Plan = $null
    )

    $path = Join-Path $ArtifactDirectory $Name
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

    $resolvedPlan = if ($null -eq $Plan) {
        [ordered]@{
            kind = "questBoard.replaceWithFixedSet"
            effect = "replaceWithFixedSet"
            target = "profile.quest_board"
            arguments = [ordered]@{
                target = "profile.quest_board"
                questIds = @("plot_kill_prophet_3")
            }
        }
    } else {
        $Plan
    }

    Write-JsonFile $path ([ordered]@{
        version = 2
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
        plan = $resolvedPlan
    })
    $artifact = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
    Add-ManagedActionTestProducer -Artifact $artifact -Producer $Producer | Out-Null
    Write-JsonFile $path $artifact
    (Get-Item -LiteralPath $path).LastWriteTimeUtc = [DateTimeOffset]::Parse($GeneratedAtUtc).UtcDateTime
    return $path
}

function Invoke-Loader {
    param(
        [string[]]$LoaderArgs,
        [switch]$ExpectFailure
    )

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    $exitCode = $LASTEXITCODE
    if ($ExpectFailure) {
        if ($exitCode -eq 0) {
            throw "DDRuntimeLoader unexpectedly succeeded"
        }
        return
    }

    if ($exitCode -ne 0) {
        throw "DDRuntimeLoader failed with exit code $exitCode"
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

    Invoke-Loader -LoaderArgs @(
        "--config", $configPath,
        "--mod-state-dir", $stateRoot,
        "--validate-only",
        "--no-inject"
    )
    $script:questBoardProducer = Get-ManagedActionTestProducer `
        -ProjectRoot $projectRoot.Path `
        -ActionType "questBoard.replaceWithFixedSet"
    $script:townEventProducer = Get-ManagedActionTestProducer `
        -ProjectRoot $projectRoot.Path `
        -ActionType "townEvent.overrideCurrent"

    $oldProfile3 = Write-Artifact "001_old_profile3.json" "2026-06-01T00:00:00.0000000Z" "validation.retention" "profile_3"
    $midProfile3 = Write-Artifact "002_mid_profile3.json" "2026-06-02T00:00:00.0000000Z" "validation.retention" "profile_3"
    $newProfile3 = Write-Artifact "003_new_profile3.json" "2026-06-03T00:00:00.0000000Z" "validation.retention" "profile_3"
    $oldProfile4 = Write-Artifact "004_old_profile4.json" "2026-06-01T12:00:00.0000000Z" "validation.retention" "profile_4"
    $newProfile4 = Write-Artifact "005_new_profile4.json" "2026-06-02T12:00:00.0000000Z" "validation.retention" "profile_4"
    $invalid = Join-Path $artifactRoot "006_invalid.json"
    "{ invalid json" | Set-Content -LiteralPath $invalid -Encoding UTF8
    $oldVersion = Write-Artifact "007_old_version.json" "2026-06-04T00:00:00.0000000Z" "validation.retention" "profile_5"
    $oldVersionArtifact = Get-Content -Raw -LiteralPath $oldVersion | ConvertFrom-Json -AsHashtable
    $oldVersionArtifact.version = 1
    Write-JsonFile $oldVersion $oldVersionArtifact
    $corruptQuestBoard = Write-Artifact "008_corrupt_quest_board.json" "2026-06-05T00:00:00.0000000Z" "validation.retention" "profile_3"
    $corruptQuestBoardArtifact = Get-Content -Raw -LiteralPath $corruptQuestBoard | ConvertFrom-Json -AsHashtable
    $corruptQuestBoardArtifact.plan.arguments.Remove("questIds")
    Write-JsonFile $corruptQuestBoard $corruptQuestBoardArtifact
    $futureVersion = Write-Artifact "009_future_version.json" "2026-06-06T00:00:00.0000000Z" "validation.retention" "profile_6"
    $futureVersionArtifact = Get-Content -Raw -LiteralPath $futureVersion | ConvertFrom-Json -AsHashtable
    $futureVersionArtifact.version = 3
    Write-JsonFile $futureVersion $futureVersionArtifact
    $missingVersion = Write-Artifact "010_missing_version.json" "2026-06-07T00:00:00.0000000Z" "validation.retention" "profile_7"
    $missingVersionArtifact = Get-Content -Raw -LiteralPath $missingVersion | ConvertFrom-Json -AsHashtable
    $missingVersionArtifact.Remove("version")
    Write-JsonFile $missingVersion $missingVersionArtifact
    $townEventPlan = [ordered]@{
        kind = "townEvent.overrideCurrent"
        effect = "overrideCurrent"
        target = "profile.townEvent"
        arguments = [ordered]@{
            target = "profile.town_event"
            mode = "suppress"
        }
    }
    $oldTownEvent = Write-Artifact "011_old_town_event.json" "2026-06-08T00:00:00.0000000Z" "validation.retention" "profile_8" -Producer $script:townEventProducer -Plan $townEventPlan
    $newTownEvent = Write-Artifact "012_new_town_event.json" "2026-06-09T00:00:00.0000000Z" "validation.retention" "profile_8" -Producer $script:townEventProducer -Plan $townEventPlan
    $stringVersion = Write-Artifact "013_string_version.json" "2026-06-10T00:00:00.0000000Z" "validation.retention" "profile_9"
    $stringVersionArtifact = Get-Content -Raw -LiteralPath $stringVersion | ConvertFrom-Json -AsHashtable
    $stringVersionArtifact.version = "1"
    Write-JsonFile $stringVersion $stringVersionArtifact
    $objectVersion = Write-Artifact "014_object_version.json" "2026-06-11T00:00:00.0000000Z" "validation.retention" "profile_10"
    $objectVersionArtifact = Get-Content -Raw -LiteralPath $objectVersion | ConvertFrom-Json -AsHashtable
    $objectVersionArtifact.version = [ordered]@{ major = 1 }
    Write-JsonFile $objectVersion $objectVersionArtifact
    $decimalVersion = Write-Artifact "015_decimal_version.json" "2026-06-12T00:00:00.0000000Z" "validation.retention" "profile_11"
    $decimalVersionArtifact = Get-Content -Raw -LiteralPath $decimalVersion | ConvertFrom-Json -AsHashtable
    $decimalVersionArtifact.version = 1.0
    Write-JsonFile $decimalVersion $decimalVersionArtifact

    $baseArgs = @(
        "--config", $configPath,
        "--mod-state-dir", $stateRoot,
        "--no-inject"
    )

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "2"))
    $preview = Read-RetentionReport
    Assert-True ([string]$preview.mode -eq "dryRun") "Preview retention should use dryRun mode."
    Assert-True ([int]$preview.artifactCount -eq 15) "Preview should inspect all fixture artifacts."
    Assert-True ([int]$preview.groupCount -eq 2) "Preview should group by profile scope."
    Assert-True ([int]$preview.prunableCount -eq 2) "Preview should find one old eligible artifact and one stale v1 artifact."
    Assert-True ([int]$preview.deletedCount -eq 0) "Preview must not delete artifacts."
    Assert-True ([int]$preview.warningCount -eq 8) "Preview should warn about malformed JSON, stale v1, corrupt quest-board, and five unknown version forms."
    Assert-True (Test-Path -LiteralPath $oldProfile3 -PathType Leaf) "Preview deleted the oldest profile_3 artifact."
    Assert-True (Test-Path -LiteralPath $oldVersion -PathType Leaf) "Preview deleted the stale v1 artifact."
    $oldVersionPreview = @($preview.artifacts | Where-Object { $_.artifactPath -eq $oldVersion })[0]
    Assert-True (-not [bool]$oldVersionPreview.eligible) "A v1 artifact should be ineligible."
    Assert-True ([string]$oldVersionPreview.eligibilityCode -eq "managed-artifact-version-unsupported") "A v1 artifact should report the unsupported version code."
    Assert-True ([string]$oldVersionPreview.decision -eq "wouldDelete") "Retention preview should mark a stale v1 artifact for deletion."
    $corruptQuestBoardPreview = @($preview.artifacts | Where-Object { $_.artifactPath -eq $corruptQuestBoard })[0]
    Assert-True (-not [bool]$corruptQuestBoardPreview.eligible) "A quest-board artifact without questIds should be ineligible."
    Assert-True ([string]$corruptQuestBoardPreview.eligibilityCode -eq "managed-artifact-quest-board-contract-invalid") "A quest-board artifact without questIds should report the action contract code."
    Assert-True ([string]$corruptQuestBoardPreview.decision -eq "retain") "Retention should keep corrupt parseable quest-board artifacts for inspection."
    foreach ($unknownVersionPath in @($futureVersion, $missingVersion, $stringVersion, $objectVersion, $decimalVersion)) {
        $unknownVersionPreview = @($preview.artifacts | Where-Object { $_.artifactPath -eq $unknownVersionPath })[0]
        Assert-True (-not [bool]$unknownVersionPreview.eligible) "Unknown or missing artifact versions should be ineligible."
        Assert-True ([string]$unknownVersionPreview.eligibilityCode -eq "managed-artifact-version-unsupported") "Unknown or missing versions should report the unsupported version code."
        Assert-True ([string]$unknownVersionPreview.decision -eq "retain") "Unknown or missing versions must be retained for inspection."
    }
    foreach ($townEventPath in @($oldTownEvent, $newTownEvent)) {
        $townEventPreview = @($preview.artifacts | Where-Object { $_.artifactPath -eq $townEventPath })[0]
        Assert-True ([bool]$townEventPreview.eligible) "A valid town-event artifact should remain eligible for consumers."
        Assert-True ($null -eq $townEventPreview.rankInGroup) "An action without a complete retention validator must not enter chronological ranking."
        Assert-True ([string]$townEventPreview.decision -eq "retain") "An unranked eligible artifact must be retained."
    }

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs ($baseArgs + @("--prune-managed-actions", "--managed-action-retention-keep", "2"))
    $prune = Read-RetentionReport
    Assert-True ([string]$prune.mode -eq "prune") "Prune retention should use prune mode."
    Assert-True ([int]$prune.prunableCount -eq 2) "Prune should still report both prunable artifacts."
    Assert-True ([int]$prune.deletedCount -eq 2) "Prune should delete the old eligible artifact and stale v1 artifact."
    Assert-True (-not (Test-Path -LiteralPath $oldProfile3 -PathType Leaf)) "Prune should delete the oldest profile_3 artifact."
    Assert-True (Test-Path -LiteralPath $midProfile3 -PathType Leaf) "Prune should retain the middle profile_3 artifact."
    Assert-True (Test-Path -LiteralPath $newProfile3 -PathType Leaf) "Prune should retain the newest profile_3 artifact."
    Assert-True (Test-Path -LiteralPath $oldProfile4 -PathType Leaf) "Prune should retain profile_4 artifacts in a separate group."
    Assert-True (Test-Path -LiteralPath $newProfile4 -PathType Leaf) "Prune should retain profile_4 artifacts in a separate group."
    Assert-True (Test-Path -LiteralPath $invalid -PathType Leaf) "Prune should retain invalid artifacts for manual inspection."
    Assert-True (Test-Path -LiteralPath $corruptQuestBoard -PathType Leaf) "Prune should retain corrupt parseable artifacts for manual inspection."
    Assert-True (-not (Test-Path -LiteralPath $oldVersion -PathType Leaf)) "Prune should delete the stale v1 artifact."
    Assert-True (Test-Path -LiteralPath $futureVersion -PathType Leaf) "Prune must retain future artifact versions for inspection."
    Assert-True (Test-Path -LiteralPath $missingVersion -PathType Leaf) "Prune must retain artifacts with a missing version for inspection."
    Assert-True (Test-Path -LiteralPath $stringVersion -PathType Leaf) "Prune must retain artifacts with a string version for inspection."
    Assert-True (Test-Path -LiteralPath $objectVersion -PathType Leaf) "Prune must retain artifacts with an object version for inspection."
    Assert-True (Test-Path -LiteralPath $decimalVersion -PathType Leaf) "Prune must retain artifacts with a decimal version for inspection."
    Assert-True (Test-Path -LiteralPath $oldTownEvent -PathType Leaf) "Prune must retain older actions without a complete retention validator."
    Assert-True (Test-Path -LiteralPath $newTownEvent -PathType Leaf) "Prune must retain newer actions without a complete retention validator."

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-managed-action-retention", "--managed-action-retention-keep", "2"))
    $after = Read-RetentionReport
    Assert-True ([int]$after.artifactCount -eq 13) "Post-prune preview should see thirteen remaining artifacts."
    Assert-True ([int]$after.prunableCount -eq 0) "Post-prune preview should find no prunable artifacts."

    $duplicateRule = [ordered]@{
        id = "duplicate_rule_identity"
        enabled = $true
        on = "validation.duplicate_rule_identity"
        phase = "normal"
        priority = 0
        requiresCapabilities = @("quest_board.replace_with_fixed_set")
        actions = @(
            [ordered]@{
                type = "questBoard.replaceWithFixedSet"
                capability = "quest_board.replace_with_fixed_set"
                risk = "managed"
                required = $false
                args = [ordered]@{
                    target = "profile.quest_board"
                    questIds = @("plot_kill_prophet_3")
                }
            }
        )
    }
    Write-JsonFile $identityManifestPath ([ordered]@{
        id = "validation.duplicate_rule_identity"
        name = "Validation - Duplicate Rule Identity"
        version = "0.1.0"
        enabled = $true
        capabilities = @("quest_board.replace_with_fixed_set")
        virtualFileRules = @()
        mapTemplates = @()
        mapLayoutTemplates = @()
        questChains = @()
        eventRules = @(
            $duplicateRule,
            ($duplicateRule | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable)
        )
        factEventRules = @()
        stateSchema = [ordered]@{}
    })
    $identityConfig = Get-Content -Raw -LiteralPath (Join-Path $projectRoot.Path "config\default_config.json") | ConvertFrom-Json
    $identityConfig.modStateDirectory = $identityStateRoot
    $identityConfig.enableInjection = $false
    $identityConfig.pluginDirectories = @($identityPluginRoot)
    $identityConfig.managedActionRetentionKeepLatestPerGroup = 1
    Write-JsonFile $identityConfigPath $identityConfig
    Invoke-Loader -LoaderArgs @(
        "--config", $identityConfigPath,
        "--mod-state-dir", $identityStateRoot,
        "--validate-only",
        "--no-inject"
    )
    $identityCatalog = Read-ManagedActionProducerCatalog -ProjectRoot $projectRoot.Path
    $identityProducers = @($identityCatalog.producers | Where-Object {
        [string]$_.pluginId -eq "validation.duplicate_rule_identity"
    } | Sort-Object -Property ruleIndex)
    Assert-True ($identityProducers.Count -eq 2) "Duplicate rule ids at different rule indices should produce two active contracts."
    Assert-True ([int]$identityProducers[0].ruleIndex -eq 1 -and [int]$identityProducers[1].ruleIndex -eq 2) "Identity fixture producers should differ by ruleIndex."
    $identityOld = Write-Artifact "001_rule_index_1.json" "2026-06-10T00:00:00.0000000Z" "validation.duplicate_rule_identity" "identity_profile" -ArtifactDirectory $identityArtifactRoot -Producer $identityProducers[0]
    $identityNew = Write-Artifact "002_rule_index_2.json" "2026-06-11T00:00:00.0000000Z" "validation.duplicate_rule_identity" "identity_profile" -ArtifactDirectory $identityArtifactRoot -Producer $identityProducers[1]

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs @(
        "--config", $identityConfigPath,
        "--mod-state-dir", $identityStateRoot,
        "--preview-managed-action-retention",
        "--managed-action-retention-keep", "1",
        "--no-inject"
    )
    $identityRetention = Read-RetentionReport
    Assert-True ([int]$identityRetention.groupCount -eq 2) "Full producer identity should keep different rule indices in separate retention groups."
    Assert-True ([int]$identityRetention.prunableCount -eq 0) "One producer must not make another producer's artifact prunable."

    Invoke-Loader -LoaderArgs @(
        "--config", $identityConfigPath,
        "--mod-state-dir", $identityStateRoot,
        "--preview-quest-board",
        "--quest-board-profile-scope", "identity_profile",
        "--no-inject"
    )
    $identityQuestBoardReport = Get-Content -Raw -LiteralPath (Join-Path $projectRoot.Path "logs\quest_board_preview_report.json") | ConvertFrom-Json
    $identityOldPreview = @($identityQuestBoardReport.artifacts | Where-Object { $_.artifactPath -eq $identityOld })[0]
    $identityNewPreview = @($identityQuestBoardReport.artifacts | Where-Object { $_.artifactPath -eq $identityNew })[0]
    Assert-True ([string]$identityOldPreview.status -eq "wouldApply") "A different rule index must not supersede the older producer artifact."
    Assert-True ([string]$identityNewPreview.status -eq "wouldApply") "The newer producer artifact should remain independently consumable."

    New-Item -ItemType Directory -Force -Path $junctionStateRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $junctionTargetRoot | Out-Null
    $junctionTargetArtifact = Join-Path $junctionTargetRoot "external_valid_artifact.json"
    Copy-Item -LiteralPath $midProfile3 -Destination $junctionTargetArtifact
    New-Item -ItemType Junction -Path $junctionArtifactRoot -Target $junctionTargetRoot | Out-Null

    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs @(
        "--config", $configPath,
        "--mod-state-dir", $junctionStateRoot,
        "--prune-managed-actions",
        "--managed-action-retention-keep", "1",
        "--no-inject"
    ) -ExpectFailure
    $junctionReport = Read-RetentionReport
    Assert-True ([int]$junctionReport.errorCount -eq 1) "Retention should report one error for a reparse-point artifact directory."
    Assert-True ((@($junctionReport.issues | Where-Object { $_.code -eq "managed-action-retention-reparse-directory" })).Count -eq 1) "Retention should report the reparse-directory error code."
    Assert-True (Test-Path -LiteralPath $junctionTargetArtifact -PathType Leaf) "Retention must not delete through an artifact-directory junction."

    Write-Host "PASS: managed action retention version safety, validator gating, full producer identity, prune, and junction refusal passed."
}
finally {
    if (Test-Path -LiteralPath $junctionArtifactRoot) {
        $junctionItem = Get-Item -LiteralPath $junctionArtifactRoot -Force
        if (($junctionItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            [System.IO.Directory]::Delete($junctionArtifactRoot)
        }
    }
    Remove-Item -LiteralPath $junctionTargetRoot -Recurse -Force -ErrorAction SilentlyContinue
    Pop-Location
}
