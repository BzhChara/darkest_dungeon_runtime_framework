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

function Get-TrinketEntryFilesWithPositivePrices {
    param([string]$GameWorkingDirectory)

    $files = @()
    $roots = @()
    $baseTrinketDirectory = Join-Path $GameWorkingDirectory "trinkets"
    if (Test-Path -LiteralPath $baseTrinketDirectory -PathType Container) {
        $roots += [ordered]@{
            Path = $baseTrinketDirectory
            Recurse = $false
        }
    }

    $dlcDirectory = Join-Path $GameWorkingDirectory "dlc"
    if (Test-Path -LiteralPath $dlcDirectory -PathType Container) {
        foreach ($directory in @(Get-ChildItem -LiteralPath $dlcDirectory -Directory | Sort-Object FullName)) {
            if ([string]::IsNullOrWhiteSpace($directory.Name) -or
                -not [char]::IsDigit($directory.Name[0]) -or
                $directory.Name.Contains("arena", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $roots += [ordered]@{
                Path = $directory.FullName
                Recurse = $true
            }
        }
    }

    foreach ($root in $roots) {
        $searchOption = if ([bool]$root.Recurse) { [System.IO.SearchOption]::AllDirectories } else { [System.IO.SearchOption]::TopDirectoryOnly }
        foreach ($path in @([System.IO.Directory]::EnumerateFiles([string]$root.Path, "*.entries.trinkets.json", $searchOption) | Sort-Object)) {
            $content = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
            $positivePrices = @($content.entries | Where-Object {
                $null -ne $_.price -and [int]$_.price -ne 0
            })
            if ($positivePrices.Count -gt 0) {
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
    $lockedTownBuildingFiles = @(Get-TownBuildingFilesWithPositiveRequirements -GameWorkingDirectory $gameWorkingDirectory)
    Assert-True ($lockedTownBuildingFiles.Count -gt 0) "Expected at least one town building content file with positive unlock requirements."
    $trinketEntryFilesWithPositivePrices = @(Get-TrinketEntryFilesWithPositivePrices -GameWorkingDirectory $gameWorkingDirectory)
    Assert-True ($trinketEntryFilesWithPositivePrices.Count -gt 0) "Expected at least one trinket entry content file with positive prices."

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
                method = "content_price_zero"
                disabled = $true
            }
        }
    }
    $inventoryArtifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inventoryArtifactPath -Encoding UTF8

    $shardStoreArtifactPath = Join-Path $artifactRoot "manual_trinket.projectShardStore.json"
    $shardStoreArtifact = [ordered]@{
        version = 1
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = "validation.managed_action_overlay_test"
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = "tools/TestManagedActionOverlay.ps1"
        ruleIndex = 2
        ruleId = "manual_trinket_shard_store"
        actionIndex = 0
        action = [ordered]@{
            type = "trinket.projectShardStore"
        }
        plan = [ordered]@{
            effect = "projectShardStore"
            target = "content.trinkets.shardStore"
            arguments = [ordered]@{
                enabled = $true
                items = @(
                    [ordered]@{
                        id = "focus_ring"
                        shard = 1
                        limit = 1
                        rarity = "comet"
                    }
                )
            }
        }
    }
    $shardStoreArtifact | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $shardStoreArtifactPath -Encoding UTF8

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
    Assert-True ($artifacts.Count -eq 10) "Expected ten materialized managed action artifacts after adding inventory, shard-store, fixed-board, and town unlock artifacts, found $($artifacts.Count)."

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

    Assert-True ([int]$manifest.artifactCount -eq 10) "Overlay manifest should count all ten artifacts."
    Assert-True ([int]$manifest.overlayCount -eq 5) "Overlay compiler should expose the latest quest.injectFixedStage overlay, the inventory policy overlay, the shard-store overlay, the fixed-board overlay, and the town unlock overlay."
    Assert-True ([int]$manifest.ignoredArtifactCount -eq 4) "Overlay manifest should still ignore hero/trinket preview filter artifacts for now."
    Assert-True ([int]$manifest.supersededOverlayCount -eq 1) "Overlay manifest should supersede the older quest injection artifact."
    Assert-True ([int]$manifest.virtualFileRuleCount -eq (1 + $lockedTownBuildingFiles.Count + $trinketEntryFilesWithPositivePrices.Count)) "Overlay manifest should compile one quest plot virtual file rule plus town building and trinket sourcePath rules."
    Assert-True ([int]$manifest.virtualFileReplacementCount -eq 2) "Overlay manifest should compile quest plot replacements for the selected stage and fixed board."
    Assert-True ((@($manifest.issues)).Count -eq 0) "Overlay manifest should not contain issues."

    $overlays = @($manifest.overlays)
    Assert-True ($overlays.Count -eq 5) "Expected exactly five overlay entries."
    $overlay = @($overlays | Where-Object { $_.kind -eq "quest.injectFixedStage" })[0]
    Assert-True ($overlay.kind -eq "quest.injectFixedStage") "Overlay kind should be quest.injectFixedStage."
    Assert-True ($overlay.stageId -eq "stage_1_necromancer") "Overlay should target the first challenge stage."
    Assert-True ($overlay.sourceQuestId -eq "plot_kill_necromancer_1") "Overlay should carry the source quest id."
    Assert-True (Test-Path -LiteralPath ([string]$overlay.artifactPath) -PathType Leaf) "Overlay artifact path should point to an existing artifact."
    $inventoryOverlay = @($overlays | Where-Object { $_.kind -eq "inventory.disableItemSale" })[0]
    Assert-True ($inventoryOverlay.effect -eq "suppressSaleValue") "Inventory overlay should request original content sale-value suppression."
    Assert-True ($inventoryOverlay.target -eq "content.trinkets.price") "Inventory overlay should target trinket price content."
    Assert-True ($inventoryOverlay.itemKind -eq "trinket") "Inventory overlay should target trinkets."
    Assert-True ($inventoryOverlay.method -eq "content_price_zero") "Inventory overlay should preserve the explicit content price-zero method."
    Assert-True ([bool]$inventoryOverlay.disabled) "Inventory overlay should be enabled."
    $shardStoreOverlay = @($overlays | Where-Object { $_.kind -eq "trinket.projectShardStore" })[0]
    Assert-True ($shardStoreOverlay.effect -eq "projectShardStore") "Shard-store overlay should record trinket shard-store projection intent."
    Assert-True ($shardStoreOverlay.target -eq "content.trinkets.shardStore") "Shard-store overlay should target trinket shard-store content."
    Assert-True ([bool]$shardStoreOverlay.enabled) "Shard-store overlay should be enabled."
    Assert-True (@($shardStoreOverlay.items).Count -eq 1) "Shard-store overlay should keep the declared item list."
    $questBoardOverlay = @($overlays | Where-Object { $_.kind -eq "questBoard.replaceWithFixedSet" })[0]
    Assert-True ($questBoardOverlay.effect -eq "replaceWithFixedSet") "Fixed-board overlay should record board replacement intent."
    Assert-True ($questBoardOverlay.target -eq "profile.quest_board") "Fixed-board overlay should target the profile quest board."
    Assert-True (@($questBoardOverlay.questIds).Count -eq 1) "Fixed-board overlay should keep the declared quest id list."
    $townUnlockOverlay = @($overlays | Where-Object { $_.kind -eq "town.unlockAllBuildings" })[0]
    Assert-True ($townUnlockOverlay.effect -eq "suppressBuildingRequirements") "Town unlock overlay should record building requirement suppression."
    Assert-True ($townUnlockOverlay.target -eq "content.town.buildingRequirements") "Town unlock overlay should target building requirement content."
    Assert-True ($townUnlockOverlay.mode -eq "all_unlocked_and_maxed") "Town unlock overlay should preserve the requested mode."

    $virtualRules = @($manifest.virtualFileRules)
    Assert-True ($virtualRules.Count -eq (1 + $lockedTownBuildingFiles.Count + $trinketEntryFilesWithPositivePrices.Count)) "Expected quest overlay plus town building and trinket content sourcePath overlays."
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
    Assert-True ($trinketRules.Count -eq $trinketEntryFilesWithPositivePrices.Count) "Explicit content_price_zero inventory policy should generate one trinket price sourcePath overlay per priced trinket entry file."
    $baseTrinketRule = @($trinketRules | Where-Object { $_.target -eq "trinkets/base.entries.trinkets.json" })[0]
    Assert-True ($null -ne $baseTrinketRule) "Expected a generated sourcePath overlay for base trinket entries."
    Assert-True ([int]$baseTrinketRule.affectedEntryCount -gt 0) "Base trinket overlay should affect at least one entry."
    Assert-True ([int]$baseTrinketRule.shardStoreAffectedEntryCount -eq 1) "Base trinket overlay should also project the declared single trinket into shard-store form."
    Assert-True (@($baseTrinketRule.shardStoreItemIds | Where-Object { $_ -eq "focus_ring" }).Count -eq 1) "Base trinket overlay should report the projected trinket id."
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

    $campingTrainerPreviewPath = Join-Path $previewRoot "campaign_town_buildings_camping_trainer_camping_trainer.building.json.preview.bin"
    Assert-True (Test-Path -LiteralPath $campingTrainerPreviewPath -PathType Leaf) "Managed overlay camping trainer sourcePath preview was not written: $campingTrainerPreviewPath"
    $campingTrainerPreview = Get-Content -Raw -LiteralPath $campingTrainerPreviewPath | ConvertFrom-Json
    Assert-True ([int]$campingTrainerPreview.requirements.highest_dungeon_level -eq 0) "Camping trainer overlay preview should suppress highest dungeon level requirement."

    $baseTrinketPreviewPath = Join-Path $previewRoot "trinkets_base.entries.trinkets.json.preview.bin"
    Assert-True (Test-Path -LiteralPath $baseTrinketPreviewPath -PathType Leaf) "Managed overlay base trinket sourcePath preview was not written: $baseTrinketPreviewPath"
    $baseTrinketPreview = Get-Content -Raw -LiteralPath $baseTrinketPreviewPath | ConvertFrom-Json
    $pricedPreviewEntries = @($baseTrinketPreview.entries | Where-Object {
        $null -ne $_.price -and [int]$_.price -ne 0
    })
    Assert-True ($pricedPreviewEntries.Count -eq 0) "Base trinket overlay preview should suppress all nonzero prices to zero."
    $focusRingPreviewEntries = @($baseTrinketPreview.entries | Where-Object { $_.id -eq "focus_ring" })
    Assert-True ($focusRingPreviewEntries.Count -eq 1) "Base trinket overlay preview should keep exactly one focus_ring entry."
    Assert-True ($focusRingPreviewEntries[0].rarity -eq "comet") "Shard-store projection should set the requested rarity."
    Assert-True ([int]$focusRingPreviewEntries[0].shard -eq 1) "Shard-store projection should set the requested shard cost."
    Assert-True ([int]$focusRingPreviewEntries[0].limit -eq 1) "Shard-store projection should set the requested ownership limit."
    Assert-True ($null -eq $focusRingPreviewEntries[0].price) "Shard-store projection should remove ordinary price by default."

    Write-Host "PASS: managed action artifacts compiled into quest, trinket entry, and town building unlock overlay manifest."
}
finally {
    Pop-Location
}
