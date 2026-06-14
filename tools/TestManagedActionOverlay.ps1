param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot.Path "state\managed_action_overlay_test\$sessionId"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Resolve-ProjectPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $projectRoot.Path $Path)
}

function Get-CampaignTrinketEntryFiles {
    param([string]$GameWorkingDirectory)

    $files = @()
    $baseTrinketDirectory = Join-Path $GameWorkingDirectory "trinkets"
    if (Test-Path -LiteralPath $baseTrinketDirectory -PathType Container) {
        $files += @(Get-ChildItem -LiteralPath $baseTrinketDirectory -Filter "*.entries.trinkets.json" -File | Sort-Object FullName | ForEach-Object { $_.FullName })
    }

    $dlcDirectory = Join-Path $GameWorkingDirectory "dlc"
    if (Test-Path -LiteralPath $dlcDirectory -PathType Container) {
        foreach ($directory in @(Get-ChildItem -LiteralPath $dlcDirectory -Directory | Sort-Object FullName)) {
            if ([string]::IsNullOrWhiteSpace($directory.Name) -or
                -not [char]::IsDigit($directory.Name[0]) -or
                $directory.Name.Contains("arena", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $files += @(Get-ChildItem -LiteralPath $directory.FullName -Filter "*.entries.trinkets.json" -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName })
        }
    }

    return $files
}

function Get-TrinketEntryFilesWithPositivePrice {
    param([string[]]$Paths)

    $result = @()
    foreach ($path in $Paths) {
        $content = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        $positive = @($content.entries | Where-Object { $null -ne $_.price -and [int]$_.price -gt 0 })
        if ($positive.Count -gt 0) {
            $result += $path
        }
    }

    return $result
}

function Get-TownBuildingFilesWithPositiveRequirements {
    param([string]$GameWorkingDirectory)

    $files = @()
    $roots = @()
    $baseBuildingDirectory = Join-Path $GameWorkingDirectory "campaign\town\buildings"
    if (Test-Path -LiteralPath $baseBuildingDirectory -PathType Container) {
        $roots += $baseBuildingDirectory
    }

    $dlcDirectory = Join-Path $GameWorkingDirectory "dlc"
    if (Test-Path -LiteralPath $dlcDirectory -PathType Container) {
        foreach ($directory in @(Get-ChildItem -LiteralPath $dlcDirectory -Directory | Sort-Object FullName)) {
            if ([string]::IsNullOrWhiteSpace($directory.Name) -or
                -not [char]::IsDigit($directory.Name[0]) -or
                $directory.Name.Contains("arena", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $buildingDirectory = Join-Path $directory.FullName "campaign\town\buildings"
            if (Test-Path -LiteralPath $buildingDirectory -PathType Container) {
                $roots += $buildingDirectory
            }
        }
    }

    foreach ($root in $roots) {
        foreach ($path in @(Get-ChildItem -LiteralPath $root -Filter "*.building.json" -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName })) {
            $content = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
            $requirements = $content.requirements
            if ($null -eq $requirements) {
                continue
            }

            $questRequirement = if ($null -ne $requirements.number_of_quests_finished) { [int]$requirements.number_of_quests_finished } else { 0 }
            $dungeonRequirement = if ($null -ne $requirements.highest_dungeon_level) { [int]$requirements.highest_dungeon_level } else { 0 }
            if ($questRequirement -gt 0 -or $dungeonRequirement -gt 0) {
                $files += $path
            }
        }
    }

    return $files
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

Push-Location $projectRoot.Path
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    $baseArgs = @(
        "--config", (Resolve-ProjectPath $ConfigPath),
        "--no-inject",
        "--allow-non-atomic-state-writes",
        "--mod-state-id", "validation.challenge_run_contract",
        "--mod-state-dir", $stateRoot
    )

    Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.run_started"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_selection_started"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_selection_started"))

    $artifactRoot = Join-Path $stateRoot "_managed_actions"
    $artifacts = @(Get-ChildItem -LiteralPath $artifactRoot -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
    Assert-True ($artifacts.Count -eq 6) "Expected six materialized managed action artifacts after two selection-start events, found $($artifacts.Count)."

    $config = Get-Content -Raw -LiteralPath (Resolve-ProjectPath $ConfigPath) | ConvertFrom-Json
    $gameWorkingDirectory = Resolve-ProjectPath ([string]$config.gameWorkingDirectory)
    $trinketEntryFiles = @(Get-CampaignTrinketEntryFiles -GameWorkingDirectory $gameWorkingDirectory)
    $positiveTrinketEntryFiles = @(Get-TrinketEntryFilesWithPositivePrice -Paths $trinketEntryFiles)
    Assert-True ($positiveTrinketEntryFiles.Count -gt 0) "Expected at least one trinket entry content file with positive prices."
    $lockedTownBuildingFiles = @(Get-TownBuildingFilesWithPositiveRequirements -GameWorkingDirectory $gameWorkingDirectory)
    Assert-True ($lockedTownBuildingFiles.Count -gt 0) "Expected at least one town building content file with positive unlock requirements."

    $inventoryArtifactPath = Join-Path $artifactRoot "manual_inventory.disableItemSale.json"
    $inventoryArtifact = [ordered]@{
        version = 1
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = "validation.managed_action_overlay_test"
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = "tools/TestManagedActionOverlay.ps1"
        ruleIndex = 1
        ruleId = "manual_inventory_policy"
        actionIndex = 0
        action = [ordered]@{
            type = "inventory.disableItemSale"
        }
        plan = [ordered]@{
            effect = "disableItemSale"
            target = "profile.inventory"
            arguments = [ordered]@{
                itemKind = "trinket"
                disabled = $true
            }
        }
    }
    $inventoryArtifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inventoryArtifactPath -Encoding UTF8

    $heroAvailabilityArtifactPath = Join-Path $artifactRoot "manual_roster.enforceAvailabilityFilter.json"
    $heroAvailabilityArtifact = [ordered]@{
        version = 1
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = "validation.managed_action_overlay_test"
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = "tools/TestManagedActionOverlay.ps1"
        ruleIndex = 2
        ruleId = "manual_hero_availability"
        actionIndex = 0
        action = [ordered]@{
            type = "roster.enforceAvailabilityFilter"
        }
        plan = [ordered]@{
            effect = "enforceAvailabilityFilter"
            target = "profile.roster.availability"
            arguments = [ordered]@{
                filterId = "validation.prefinale"
                unavailableHeroIds = @("hero_1", "hero_2")
            }
        }
    }
    $heroAvailabilityArtifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $heroAvailabilityArtifactPath -Encoding UTF8

    $trinketAvailabilityArtifactPath = Join-Path $artifactRoot "manual_equipment.enforceAvailabilityFilter.json"
    $trinketAvailabilityArtifact = [ordered]@{
        version = 1
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = "validation.managed_action_overlay_test"
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = "tools/TestManagedActionOverlay.ps1"
        ruleIndex = 3
        ruleId = "manual_trinket_availability"
        actionIndex = 0
        action = [ordered]@{
            type = "equipment.enforceAvailabilityFilter"
        }
        plan = [ordered]@{
            effect = "enforceAvailabilityFilter"
            target = "profile.equipment.availability"
            arguments = [ordered]@{
                filterId = "validation.prefinale"
                unavailableTrinketIds = @("dazzling_charm", "speed_stone")
            }
        }
    }
    $trinketAvailabilityArtifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $trinketAvailabilityArtifactPath -Encoding UTF8

    $questBoardArtifactPath = Join-Path $artifactRoot "manual_questBoard.replaceWithFixedSet.json"
    $questBoardArtifact = [ordered]@{
        version = 1
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = "validation.managed_action_overlay_test"
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = "tools/TestManagedActionOverlay.ps1"
        ruleIndex = 4
        ruleId = "manual_fixed_board"
        actionIndex = 0
        action = [ordered]@{
            type = "questBoard.replaceWithFixedSet"
        }
        plan = [ordered]@{
            kind = "questBoard.replaceWithFixedSet"
            effect = "replaceWithFixedSet"
            target = "profile.quest_board"
            arguments = [ordered]@{
                target = "profile.quest_board"
                questIds = @("plot_kill_prophet_3")
                removeCompleted = $false
            }
        }
    }
    $questBoardArtifact | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $questBoardArtifactPath -Encoding UTF8

    $townUnlockArtifactPath = Join-Path $artifactRoot "manual_town.unlockAllBuildings.json"
    $townUnlockArtifact = [ordered]@{
        version = 1
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = "validation.managed_action_overlay_test"
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = "tools/TestManagedActionOverlay.ps1"
        ruleIndex = 5
        ruleId = "manual_town_unlock"
        actionIndex = 0
        action = [ordered]@{
            type = "town.unlockAllBuildings"
        }
        plan = [ordered]@{
            effect = "unlockAllBuildings"
            target = "profile.town"
            arguments = [ordered]@{
                target = "profile.town"
                mode = "all_unlocked_and_maxed"
            }
        }
    }
    $townUnlockArtifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $townUnlockArtifactPath -Encoding UTF8

    $artifacts = @(Get-ChildItem -LiteralPath $artifactRoot -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
    Assert-True ($artifacts.Count -eq 11) "Expected eleven materialized managed action artifacts after adding inventory, availability, fixed-board, and town unlock artifacts, found $($artifacts.Count)."

    $dryRunArgs = @(
        "--config", (Resolve-ProjectPath $ConfigPath),
        "--no-inject",
        "--mod-state-dir", $stateRoot,
        "--dry-run"
    )
    Invoke-Loader -LoaderArgs $dryRunArgs

    $manifestPath = Join-Path $projectRoot.Path "logs\managed_action_overlay_manifest.json"
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Managed action overlay manifest was not written: $manifestPath"
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

    Assert-True ([int]$manifest.artifactCount -eq 11) "Overlay manifest should count all eleven artifacts."
    Assert-True ([int]$manifest.overlayCount -eq 4) "Overlay compiler should expose the latest quest.injectFixedStage overlay, the inventory policy overlay, the fixed-board overlay, and the town unlock overlay."
    Assert-True ([int]$manifest.availabilityPolicyCount -eq 2) "Overlay compiler should expose hero and trinket availability policies as manifest-only consumers."
    Assert-True ([int]$manifest.ignoredArtifactCount -eq 4) "Overlay manifest should still ignore hero/trinket preview filter artifacts for now."
    Assert-True ([int]$manifest.supersededOverlayCount -eq 1) "Overlay manifest should supersede the older quest injection artifact."
    Assert-True ([int]$manifest.supersededAvailabilityPolicyCount -eq 0) "Overlay manifest should not supersede unique availability policies."
    Assert-True ([int]$manifest.virtualFileRuleCount -eq (1 + $positiveTrinketEntryFiles.Count + $lockedTownBuildingFiles.Count)) "Overlay manifest should compile one quest plot virtual file rule plus trinket and town building sourcePath rules."
    Assert-True ([int]$manifest.virtualFileReplacementCount -eq 2) "Overlay manifest should compile quest plot replacements for the selected stage and fixed board; trinket price suppression uses sourcePath overlays."
    Assert-True ((@($manifest.issues)).Count -eq 0) "Overlay manifest should not contain issues."

    $overlays = @($manifest.overlays)
    Assert-True ($overlays.Count -eq 4) "Expected exactly four overlay entries."
    $overlay = @($overlays | Where-Object { $_.kind -eq "quest.injectFixedStage" })[0]
    Assert-True ($overlay.kind -eq "quest.injectFixedStage") "Overlay kind should be quest.injectFixedStage."
    Assert-True ($overlay.stageId -eq "stage_1_necromancer") "Overlay should target the first challenge stage."
    Assert-True ($overlay.sourceQuestId -eq "plot_kill_necromancer_1") "Overlay should carry the source quest id."
    Assert-True (Test-Path -LiteralPath ([string]$overlay.artifactPath) -PathType Leaf) "Overlay artifact path should point to an existing artifact."
    $inventoryOverlay = @($overlays | Where-Object { $_.kind -eq "inventory.disableItemSale" })[0]
    Assert-True ($inventoryOverlay.effect -eq "suppressSaleValue") "Inventory overlay should record sale-value suppression."
    Assert-True ($inventoryOverlay.itemKind -eq "trinket") "Inventory overlay should target trinkets."
    Assert-True ([bool]$inventoryOverlay.disabled) "Inventory overlay should be enabled."
    $questBoardOverlay = @($overlays | Where-Object { $_.kind -eq "questBoard.replaceWithFixedSet" })[0]
    Assert-True ($questBoardOverlay.effect -eq "replaceWithFixedSet") "Fixed-board overlay should record board replacement intent."
    Assert-True ($questBoardOverlay.target -eq "profile.quest_board") "Fixed-board overlay should target the profile quest board."
    Assert-True (@($questBoardOverlay.questIds).Count -eq 1) "Fixed-board overlay should keep the declared quest id list."
    $townUnlockOverlay = @($overlays | Where-Object { $_.kind -eq "town.unlockAllBuildings" })[0]
    Assert-True ($townUnlockOverlay.effect -eq "suppressBuildingRequirements") "Town unlock overlay should record building requirement suppression."
    Assert-True ($townUnlockOverlay.target -eq "content.town.buildingRequirements") "Town unlock overlay should target building requirement content."
    Assert-True ($townUnlockOverlay.mode -eq "all_unlocked_and_maxed") "Town unlock overlay should preserve the requested mode."

    $availabilityPolicies = @($manifest.availabilityPolicies)
    Assert-True ($availabilityPolicies.Count -eq 2) "Expected exactly two availability policy entries."
    $heroAvailability = @($availabilityPolicies | Where-Object { $_.kind -eq "roster.enforceAvailabilityFilter" })[0]
    Assert-True ($heroAvailability.itemKind -eq "hero") "Hero availability policy should record itemKind=hero."
    Assert-True ($heroAvailability.filterId -eq "validation.prefinale") "Hero availability policy should preserve filter id."
    Assert-True ([int]$heroAvailability.unavailableCount -eq 2) "Hero availability policy should count unavailable heroes."
    Assert-True (-not [bool]$heroAvailability.liveEnforced) "Hero availability policy should not claim live hard enforcement."
    Assert-True ($heroAvailability.consumerStatus -eq "manifestOnly") "Hero availability policy should be marked as manifest-only."
    $trinketAvailability = @($availabilityPolicies | Where-Object { $_.kind -eq "equipment.enforceAvailabilityFilter" })[0]
    Assert-True ($trinketAvailability.itemKind -eq "trinket") "Trinket availability policy should record itemKind=trinket."
    Assert-True ([int]$trinketAvailability.unavailableCount -eq 2) "Trinket availability policy should count unavailable trinkets."
    Assert-True (-not [bool]$trinketAvailability.liveEnforced) "Trinket availability policy should not claim live hard enforcement."

    $virtualRules = @($manifest.virtualFileRules)
    Assert-True ($virtualRules.Count -eq (1 + $positiveTrinketEntryFiles.Count + $lockedTownBuildingFiles.Count)) "Expected quest overlay plus trinket and town building content sourcePath overlays."
    $virtualRule = @($virtualRules | Where-Object { $_.effect -eq "forcePlotQuestAvailable" })[0]
    Assert-True ($virtualRule.target -eq "campaign/quest/quest.plot_quests.json") "Overlay virtual rule should target the base plot quest file."
    Assert-True ($virtualRule.effect -eq "forcePlotQuestAvailable") "Overlay virtual rule should force the selected plot quest available."
    $virtualReplacements = @($virtualRule.replacements)
    Assert-True ($virtualReplacements.Count -eq 2) "Expected exactly two overlay virtual replacements."
    $virtualReplacement = @($virtualReplacements | Where-Object { $_.sourceQuestId -eq "plot_kill_necromancer_1" })[0]
    Assert-True ($virtualReplacement.sourceQuestId -eq "plot_kill_necromancer_1") "Overlay virtual replacement should use the current stage source quest."
    Assert-True ($virtualReplacement.stageId -eq "stage_1_necromancer") "Overlay virtual replacement should carry the current stage id."
    Assert-True ([int]$virtualReplacement.setDungeonLevel -eq 0) "Overlay virtual replacement should force dungeon_level to 0."
    Assert-True ([bool]$virtualReplacement.setRepeatable) "Overlay virtual replacement should force the quest to repeatable."
    Assert-True ([int]$virtualReplacement.findChars -gt 0) "Overlay virtual replacement should contain non-empty find text."
    Assert-True ([int]$virtualReplacement.replaceChars -gt 0) "Overlay virtual replacement should contain non-empty replacement text."
    $questBoardReplacement = @($virtualReplacements | Where-Object { $_.sourceQuestId -eq "plot_kill_prophet_3" })[0]
    Assert-True ($questBoardReplacement.kind -eq "questBoard.replaceWithFixedSet") "Fixed-board replacement should keep the originating overlay kind."
    Assert-True ([int]$questBoardReplacement.setDungeonLevel -eq 0) "Fixed-board replacement should force dungeon_level to 0."
    Assert-True ([bool]$questBoardReplacement.setRepeatable) "Fixed-board replacement should force repeatable availability."
    $trinketRules = @($virtualRules | Where-Object { $_.effect -eq "suppressTrinketSaleValue" })
    Assert-True ($trinketRules.Count -eq $positiveTrinketEntryFiles.Count) "Expected one trinket sale-value sourcePath rule per positive-price trinket file."
    Assert-True (($trinketRules | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.sourcePath) }).Count -eq 0) "Trinket sale-value overlays should use generated sourcePath files."
    Assert-True (($trinketRules | Where-Object { [int]$_.affectedEntryCount -le 0 }).Count -eq 0) "Trinket sale-value overlays should affect at least one entry each."
    $baseTrinketRule = @($trinketRules | Where-Object { $_.target -eq "trinkets/base.entries.trinkets.json" })[0]
    Assert-True ($null -ne $baseTrinketRule) "Expected a generated sourcePath overlay for base trinkets."
    $baseTrinketOverlayBytes = [System.IO.File]::ReadAllBytes([string]$baseTrinketRule.sourcePath)
    Assert-True ($baseTrinketOverlayBytes.Length -gt 3) "Base trinket sourcePath overlay should not be empty."
    $baseTrinketOverlayHasBom = $baseTrinketOverlayBytes[0] -eq 0xEF -and $baseTrinketOverlayBytes[1] -eq 0xBB -and $baseTrinketOverlayBytes[2] -eq 0xBF
    Assert-True (-not $baseTrinketOverlayHasBom) "Base trinket sourcePath overlay must be UTF-8 without BOM for the DD JSON parser."
    Assert-True ($baseTrinketOverlayBytes[0] -eq 123 -or $baseTrinketOverlayBytes[0] -eq 91) "Base trinket sourcePath overlay should start with a JSON object or array root byte."
    $townBuildingRules = @($virtualRules | Where-Object { $_.effect -eq "suppressTownBuildingRequirements" })
    Assert-True ($townBuildingRules.Count -eq $lockedTownBuildingFiles.Count) "Expected one town building requirement sourcePath rule per locked building file."
    $campingTrainerRule = @($townBuildingRules | Where-Object { $_.target -eq "campaign/town/buildings/camping_trainer/camping_trainer.building.json" })[0]
    Assert-True ($null -ne $campingTrainerRule) "Expected a generated sourcePath overlay for camping trainer requirements."
    Assert-True ([int]$campingTrainerRule.affectedRequirementCount -gt 0) "Camping trainer overlay should affect at least one requirement."

    $previewRoot = Join-Path $projectRoot.Path "logs\managed_action_overlay_preview"
    $previewPath = Join-Path $previewRoot "campaign_quest_quest.plot_quests.json.preview.txt"
    $diffPath = Join-Path $previewRoot "campaign_quest_quest.plot_quests.json.diff.txt"
    $summaryPath = Join-Path $previewRoot "summary.txt"
    Assert-True (Test-Path -LiteralPath $previewPath -PathType Leaf) "Managed overlay preview file was not written: $previewPath"
    Assert-True (Test-Path -LiteralPath $diffPath -PathType Leaf) "Managed overlay diff file was not written: $diffPath"
    Assert-True (Test-Path -LiteralPath $summaryPath -PathType Leaf) "Managed overlay preview summary was not written: $summaryPath"

    $preview = Get-Content -Raw -LiteralPath $previewPath | ConvertFrom-Json
    $previewQuest = @($preview.plot_quests | Where-Object { $_.id -eq "plot_kill_necromancer_1" })
    Assert-True ($previewQuest.Count -eq 1) "Managed overlay preview should contain the selected plot quest exactly once."
    Assert-True ([int]$previewQuest[0].dungeon_level -eq 0) "Managed overlay preview should force dungeon_level to 0."
    Assert-True ([bool]$previewQuest[0].is_repeatable) "Managed overlay preview should force is_repeatable to true."
    $previewBoardQuest = @($preview.plot_quests | Where-Object { $_.id -eq "plot_kill_prophet_3" })
    Assert-True ($previewBoardQuest.Count -eq 1) "Managed overlay preview should contain the fixed-board plot quest exactly once."
    Assert-True ([int]$previewBoardQuest[0].dungeon_level -eq 0) "Managed overlay preview should force fixed-board dungeon_level to 0."
    Assert-True ([bool]$previewBoardQuest[0].is_repeatable) "Managed overlay preview should force fixed-board repeatable availability."

    $baseTrinketPreviewPath = Join-Path $previewRoot "trinkets_base.entries.trinkets.json.preview.bin"
    Assert-True (Test-Path -LiteralPath $baseTrinketPreviewPath -PathType Leaf) "Managed overlay trinket sourcePath preview was not written: $baseTrinketPreviewPath"
    $baseTrinketPreview = Get-Content -Raw -LiteralPath $baseTrinketPreviewPath | ConvertFrom-Json
    $positivePreviewPrices = @($baseTrinketPreview.entries | Where-Object { $null -ne $_.price -and [int]$_.price -gt 0 })
    Assert-True ($positivePreviewPrices.Count -eq 0) "Trinket sale-value overlay preview should suppress all positive base trinket prices."
    $campingTrainerPreviewPath = Join-Path $previewRoot "campaign_town_buildings_camping_trainer_camping_trainer.building.json.preview.bin"
    Assert-True (Test-Path -LiteralPath $campingTrainerPreviewPath -PathType Leaf) "Managed overlay camping trainer sourcePath preview was not written: $campingTrainerPreviewPath"
    $campingTrainerPreview = Get-Content -Raw -LiteralPath $campingTrainerPreviewPath | ConvertFrom-Json
    Assert-True ([int]$campingTrainerPreview.requirements.highest_dungeon_level -eq 0) "Camping trainer overlay preview should suppress highest dungeon level requirement."

    Write-Host "PASS: managed action artifacts compiled into quest, trinket sale-value, and town building unlock overlay manifest."
}
finally {
    Pop-Location
}
