param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ManagedActionProducerTestHelpers.ps1")

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot.Path "state\managed_action_overlay_test\$sessionId"
$ownerPluginId = "validation.managed_action_owner_contract"
$ownerManifestPath = (Resolve-Path -LiteralPath (Join-Path $projectRoot.Path "plugins\_validation\managed_action_owner_contract\patches.json")).Path

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

function Get-TrinketEntryFilesMatchingPatchCriteria {
    param(
        [string]$GameWorkingDirectory,
        [string]$ItemId,
        [string[]]$Rarities
    )

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
            $matches = @($content.entries | Where-Object {
                ([string]$_.id -eq $ItemId) -or ($Rarities -contains [string]$_.rarity)
            })
            if ($matches.Count -gt 0) {
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

    $artifactRoot = Join-Path $stateRoot "_managed_actions"
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

    Invoke-Loader -LoaderArgs @(
        "--config", (Resolve-ProjectPath $ConfigPath),
        "--mod-state-dir", $stateRoot,
        "--validate-only",
        "--no-inject"
    )
    $patchEntryProducer = Get-ManagedActionTestProducer -ProjectRoot $projectRoot.Path -ActionType "trinket.patchEntry"
    $questBoardProducer = Get-ManagedActionTestProducer -ProjectRoot $projectRoot.Path -ActionType "questBoard.replaceWithFixedSet"
    $townUnlockProducer = Get-ManagedActionTestProducer -ProjectRoot $projectRoot.Path -ActionType "town.unlockAllBuildings"

    $config = Get-Content -Raw -LiteralPath (Resolve-ProjectPath $ConfigPath) | ConvertFrom-Json
    $gameWorkingDirectory = Resolve-ProjectPath ([string]$config.gameWorkingDirectory)
    $lockedTownBuildingFiles = @(Get-TownBuildingFilesWithPositiveRequirements -GameWorkingDirectory $gameWorkingDirectory)
    Assert-True ($lockedTownBuildingFiles.Count -gt 0) "Expected at least one town building content file with positive unlock requirements."
    $trinketPatchEntryFiles = @(Get-TrinketEntryFilesMatchingPatchCriteria -GameWorkingDirectory $gameWorkingDirectory -ItemId "focus_ring" -Rarities @("common"))
    Assert-True ($trinketPatchEntryFiles.Count -gt 0) "Expected at least one trinket entry content file matching the patch selector."

    $patchEntryArtifactPath = Join-Path $artifactRoot "manual_trinket.patchEntry.json"
    $patchEntryArtifact = [ordered]@{
        version = 2
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = $ownerPluginId
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = $ownerManifestPath
        ruleIndex = 2
        ruleId = "manual_trinket_patch_entry"
        actionIndex = 0
        action = [ordered]@{
            type = "trinket.patchEntry"
        }
        plan = [ordered]@{
            kind = "trinket.patchEntry"
            effect = "patchEntry"
            target = "content.trinkets.entries"
            arguments = [ordered]@{
                enabled = $true
                items = @(
                    [ordered]@{
                        id = "focus_ring"
                        set = [ordered]@{
                            rarity = "comet"
                            shard = 1
                            limit = 1
                        }
                        remove = @("price")
                    },
                    [ordered]@{
                        where = [ordered]@{
                            rarity = "common"
                        }
                        set = [ordered]@{
                            price = 0
                        }
                    }
                )
            }
        }
    }
    Add-ManagedActionTestProducer -Artifact $patchEntryArtifact -Producer $patchEntryProducer | Out-Null
    $patchEntryArtifact | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $patchEntryArtifactPath -Encoding UTF8

    $questBoardArtifactPath = Join-Path $artifactRoot "manual_questBoard.replaceWithFixedSet.json"
    $questBoardArtifact = [ordered]@{
        version = 2
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = $ownerPluginId
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = $ownerManifestPath
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
                questIds = @("plot_kill_necromancer_3", "plot_kill_prophet_3")
                removeCompleted = $false
            }
        }
    }
    Add-ManagedActionTestProducer -Artifact $questBoardArtifact -Producer $questBoardProducer | Out-Null
    $questBoardArtifact | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $questBoardArtifactPath -Encoding UTF8

    $townUnlockArtifactPath = Join-Path $artifactRoot "manual_town.unlockAllBuildings.json"
    $townUnlockArtifact = [ordered]@{
        version = 2
        status = "materialized"
        eventId = "manual.overlay-test"
        pluginId = $ownerPluginId
        sourceName = "Validation - Managed Action Overlay Test"
        sourcePath = $ownerManifestPath
        ruleIndex = 5
        ruleId = "manual_town_unlock"
        actionIndex = 0
        action = [ordered]@{
            type = "town.unlockAllBuildings"
        }
        plan = [ordered]@{
            kind = "town.unlockAllBuildings"
            effect = "unlockAllBuildings"
            target = "profile.town"
            arguments = [ordered]@{
                target = "profile.town"
                mode = "all_unlocked_and_maxed"
            }
        }
    }
    Add-ManagedActionTestProducer -Artifact $townUnlockArtifact -Producer $townUnlockProducer | Out-Null
    $townUnlockArtifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $townUnlockArtifactPath -Encoding UTF8

    $inactiveOwnerArtifact = $questBoardArtifact | ConvertTo-Json -Depth 10 | ConvertFrom-Json -AsHashtable
    $inactiveOwnerArtifact.pluginId = "validation.disabled_managed_action_owner"
    $inactiveOwnerArtifact.producer.pluginId = "validation.disabled_managed_action_owner"
    $inactiveOwnerArtifact.plan.arguments.questIds = @("plot_darkest_dungeon_3")
    $inactiveOwnerArtifactPath = Join-Path $artifactRoot "manual_inactive_owner_questBoard.replaceWithFixedSet.json"
    $inactiveOwnerArtifact | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $inactiveOwnerArtifactPath -Encoding UTF8

    $sourceMismatchArtifact = $questBoardArtifact | ConvertTo-Json -Depth 10 | ConvertFrom-Json -AsHashtable
    $sourceMismatchArtifact.sourcePath = Join-Path $projectRoot.Path "tools\TestManagedActionOverlay.ps1"
    $sourceMismatchArtifact.producer.sourcePath = $sourceMismatchArtifact.sourcePath
    $sourceMismatchArtifact.plan.arguments.questIds = @("plot_darkest_dungeon_4")
    $sourceMismatchArtifactPath = Join-Path $artifactRoot "manual_source_mismatch_questBoard.replaceWithFixedSet.json"
    $sourceMismatchArtifact | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $sourceMismatchArtifactPath -Encoding UTF8

    $artifacts = @(Get-ChildItem -LiteralPath $artifactRoot -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
    Assert-True ($artifacts.Count -eq 5) "Expected three eligible and two ineligible managed action artifacts, found $($artifacts.Count)."

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

    Assert-True ([int]$manifest.artifactCount -eq 5) "Overlay manifest should count all five artifacts."
    Assert-True ([int]$manifest.overlayCount -eq 3) "Overlay compiler should expose the trinket patch overlay, the fixed-board overlay, and the town unlock overlay."
    Assert-True ([int]$manifest.ignoredArtifactCount -eq 2) "Overlay manifest should ignore both ineligible artifacts."
    Assert-True ([int]$manifest.supersededOverlayCount -eq 0) "Overlay manifest should not supersede single-source artifacts in this focused fixture."
    Assert-True ([int]$manifest.virtualFileRuleCount -eq (1 + $lockedTownBuildingFiles.Count + $trinketPatchEntryFiles.Count)) "Overlay manifest should compile one quest plot virtual file rule plus town building and trinket sourcePath rules."
    Assert-True ([int]$manifest.virtualFileReplacementCount -eq 2) "Overlay manifest should compile quest plot replacements for both fixed-board boss quests."
    Assert-True ((@($manifest.issues)).Count -eq 2) "Overlay manifest should report both ineligible artifacts."
    Assert-True ((@($manifest.issues | Where-Object { $_.code -eq "managed-artifact-owner-inactive" })).Count -eq 1) "Overlay manifest should report the inactive owner."
    Assert-True ((@($manifest.issues | Where-Object { $_.code -eq "managed-artifact-owner-source-mismatch" })).Count -eq 1) "Overlay manifest should report the same-id source mismatch."

    $questBoardPreviewPath = Join-Path $projectRoot.Path "logs\quest_board_preview_report.json"
    Assert-True (Test-Path -LiteralPath $questBoardPreviewPath -PathType Leaf) "Quest board preview report was not written: $questBoardPreviewPath"
    $questBoardPreview = Get-Content -Raw -LiteralPath $questBoardPreviewPath | ConvertFrom-Json
    Assert-True ([int]$questBoardPreview.wouldApplyArtifactCount -eq 1) "Quest board preview should accept only the eligible fixed-board artifact."
    Assert-True ((@($questBoardPreview.artifacts | Where-Object { $_.status -eq "ineligible" })).Count -eq 2) "Quest board preview should mark both invalid-owner artifacts ineligible."
    Assert-True ((@($questBoardPreview.issues | Where-Object { $_.code -eq "managed-artifact-owner-inactive" })).Count -eq 1) "Quest board preview should report the inactive owner."
    Assert-True ((@($questBoardPreview.issues | Where-Object { $_.code -eq "managed-artifact-owner-source-mismatch" })).Count -eq 1) "Quest board preview should report the same-id source mismatch."

    $overlays = @($manifest.overlays)
    Assert-True ($overlays.Count -eq 3) "Expected exactly three overlay entries."
    $patchEntryOverlay = @($overlays | Where-Object { $_.kind -eq "trinket.patchEntry" })[0]
    Assert-True ($patchEntryOverlay.effect -eq "patchEntry") "Trinket patch overlay should record patch-entry intent."
    Assert-True ($patchEntryOverlay.target -eq "content.trinkets.entries") "Trinket patch overlay should target trinket entry content."
    Assert-True ([bool]$patchEntryOverlay.enabled) "Trinket patch overlay should be enabled."
    Assert-True (@($patchEntryOverlay.items).Count -eq 2) "Trinket patch overlay should keep the declared item list."
    Assert-True ($patchEntryOverlay.items[0].set.rarity -eq "comet") "Trinket patch overlay should preserve declared set fields."
    Assert-True (@($patchEntryOverlay.items[0].remove | Where-Object { $_ -eq "price" }).Count -eq 1) "Trinket patch overlay should preserve explicit remove fields."
    Assert-True ($patchEntryOverlay.items[1].where.rarity -eq "common") "Trinket patch overlay should preserve rarity selectors."
    Assert-True ([int]$patchEntryOverlay.items[1].set.price -eq 0) "Trinket patch overlay should preserve selector set fields."
    $questBoardOverlay = @($overlays | Where-Object { $_.kind -eq "questBoard.replaceWithFixedSet" })[0]
    Assert-True ($questBoardOverlay.effect -eq "replaceWithFixedSet") "Fixed-board overlay should record board replacement intent."
    Assert-True ($questBoardOverlay.target -eq "profile.quest_board") "Fixed-board overlay should target the profile quest board."
    Assert-True (@($questBoardOverlay.questIds).Count -eq 2) "Fixed-board overlay should keep the declared quest id list."
    Assert-True ((@($questBoardOverlay.questIds) -contains "plot_kill_necromancer_3") -and (@($questBoardOverlay.questIds) -contains "plot_kill_prophet_3")) "Fixed-board overlay should keep both declared boss quest ids."
    $townUnlockOverlay = @($overlays | Where-Object { $_.kind -eq "town.unlockAllBuildings" })[0]
    Assert-True ($townUnlockOverlay.effect -eq "suppressBuildingRequirements") "Town unlock overlay should record building requirement suppression."
    Assert-True ($townUnlockOverlay.target -eq "content.town.buildingRequirements") "Town unlock overlay should target building requirement content."
    Assert-True ($townUnlockOverlay.mode -eq "all_unlocked_and_maxed") "Town unlock overlay should preserve the requested mode."

    $virtualRules = @($manifest.virtualFileRules)
    Assert-True ($virtualRules.Count -eq (1 + $lockedTownBuildingFiles.Count + $trinketPatchEntryFiles.Count)) "Expected quest overlay plus town building and trinket content sourcePath overlays."
    $virtualRule = @($virtualRules | Where-Object { $_.effect -eq "forcePlotQuestAvailable" })[0]
    Assert-True ($virtualRule.target -eq "campaign/quest/quest.plot_quests.json") "Overlay virtual rule should target the base plot quest file."
    Assert-True ($virtualRule.effect -eq "forcePlotQuestAvailable") "Overlay virtual rule should force the selected plot quest available."
    $virtualReplacements = @($virtualRule.replacements)
    Assert-True ($virtualReplacements.Count -eq 2) "Expected exactly two overlay virtual replacements."
    $necroBoardReplacement = @($virtualReplacements | Where-Object { $_.sourceQuestId -eq "plot_kill_necromancer_3" })[0]
    Assert-True ($necroBoardReplacement.kind -eq "questBoard.replaceWithFixedSet") "Necromancer fixed-board replacement should keep the originating overlay kind."
    Assert-True ([int]$necroBoardReplacement.setDungeonLevel -eq 0) "Necromancer fixed-board replacement should force dungeon_level to 0."
    Assert-True ([bool]$necroBoardReplacement.setRepeatable) "Necromancer fixed-board replacement should force repeatable availability."
    Assert-True ([int]$necroBoardReplacement.findChars -gt 0) "Necromancer fixed-board replacement should contain non-empty find text."
    Assert-True ([int]$necroBoardReplacement.replaceChars -gt 0) "Necromancer fixed-board replacement should contain non-empty replacement text."
    $questBoardReplacement = @($virtualReplacements | Where-Object { $_.sourceQuestId -eq "plot_kill_prophet_3" })[0]
    Assert-True ($questBoardReplacement.kind -eq "questBoard.replaceWithFixedSet") "Fixed-board replacement should keep the originating overlay kind."
    Assert-True ([int]$questBoardReplacement.setDungeonLevel -eq 0) "Fixed-board replacement should force dungeon_level to 0."
    Assert-True ([bool]$questBoardReplacement.setRepeatable) "Fixed-board replacement should force repeatable availability."
    $trinketRules = @($virtualRules | Where-Object { $_.effect -eq "patchTrinketEntries" })
    Assert-True ($trinketRules.Count -eq $trinketPatchEntryFiles.Count) "Trinket patch selectors should generate one trinket entry sourcePath overlay per matching trinket entry file."
    $baseTrinketRule = @($trinketRules | Where-Object { $_.target -eq "trinkets/base.entries.trinkets.json" })[0]
    Assert-True ($null -ne $baseTrinketRule) "Expected a generated sourcePath overlay for base trinket entries."
    Assert-True ([int]$baseTrinketRule.affectedEntryCount -gt 0) "Base trinket overlay should affect at least one entry."
    Assert-True ([int]$baseTrinketRule.patchEntryAffectedEntryCount -gt 1) "Base trinket overlay should patch the declared id and rarity selector matches."
    Assert-True (@($baseTrinketRule.patchEntryItemIds | Where-Object { $_ -eq "focus_ring" }).Count -eq 1) "Base trinket overlay should report the patched trinket id."
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
    $previewQuest = @($preview.plot_quests | Where-Object { $_.id -eq "plot_kill_necromancer_3" })
    Assert-True ($previewQuest.Count -eq 1) "Managed overlay preview should contain the Necromancer fixed-board plot quest exactly once."
    Assert-True ([int]$previewQuest[0].dungeon_level -eq 0) "Managed overlay preview should force Necromancer dungeon_level to 0."
    Assert-True ([bool]$previewQuest[0].is_repeatable) "Managed overlay preview should force Necromancer is_repeatable to true."
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
    $pricedCommonPreviewEntries = @($baseTrinketPreview.entries | Where-Object {
        [string]$_.rarity -eq "common" -and $null -ne $_.price -and [int]$_.price -ne 0
    })
    Assert-True ($pricedCommonPreviewEntries.Count -eq 0) "Base trinket overlay preview should set common trinket prices to zero through the selector."
    $focusRingPreviewEntries = @($baseTrinketPreview.entries | Where-Object { $_.id -eq "focus_ring" })
    Assert-True ($focusRingPreviewEntries.Count -eq 1) "Base trinket overlay preview should keep exactly one focus_ring entry."
    Assert-True ($focusRingPreviewEntries[0].rarity -eq "comet") "Trinket patch should set the requested rarity."
    Assert-True ([int]$focusRingPreviewEntries[0].shard -eq 1) "Trinket patch should set the requested shard cost."
    Assert-True ([int]$focusRingPreviewEntries[0].limit -eq 1) "Trinket patch should set the requested ownership limit."
    Assert-True ($null -eq $focusRingPreviewEntries[0].price) "Trinket patch should remove ordinary price only because the test explicitly requested it."

    Write-Host "PASS: managed action artifacts compiled into quest, trinket entry, and town building unlock overlay manifest."
}
finally {
    Pop-Location
}
