param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\managed_action_save_applier_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\managed_action_save_applier_test\$sessionId"
$saveRoot = Join-Path $stateRoot "decoded_save"
$sourceSaveRoot = Join-Path $projectRoot.Path ".research\DDSaveEditor-v0.0.70\decoded_current"
$pluginId = "validation.boss_gauntlet_campaign_contract"

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

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Read-ApplyReport {
    $path = Join-Path $projectRoot.Path "logs\managed_action_apply_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Managed action apply report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedEstate {
    $path = Join-Path $saveRoot "persist.estate.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded estate file was not copied: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedRoster {
    $path = Join-Path $saveRoot "persist.roster.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded roster file was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedUpgrades {
    $path = Join-Path $saveRoot "persist.upgrades.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded upgrades file was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Write-DecodedRosterFixture {
    $path = Join-Path $saveRoot "persist.roster.json"
    @'
{
  "base_root": {
    "version": 513,
    "nextGuid": 3,
    "dismissed_hero_count": 0,
    "heroes": {
      "1": {
        "hero_file_data": {
          "raw_data": {
            "base_root": {
              "roster.status": 0,
              "roster.before_on_start_town_visit_status": 0,
              "roster.missing_duration": 0,
              "roster.story_variation": 0,
              "roster.missing_from": 0,
              "roster.building_name": "",
              "roster.timestamp": 0,
              "actor": {
                "name": "Existing Crusader",
                "current_hp": 33.0,
                "stunned": 0,
                "combat_ready": false,
                "damage_source_data": 0,
                "damage_source_type": 0,
                "damage_type": 0,
                "colour_variation": 0,
                "enemy_rank_targets": 0,
                "friendly_rank_targets": 0,
                "performing_turn": 0,
                "controlling_actor_guid": 0,
                "controlling_duration": 0,
                "current_mode_id": 0,
                "rounds_in_ranks": 0,
                "check_round_ranks": 0,
                "health_damage_blocks": 0,
                "buff_group_next_guid": 0,
                "buff_group": {},
                "actor_dot": {}
              },
              "heroClass": "crusader",
              "template_only_marker": "must_not_copy_to_generated_heroes",
              "resolveXp": 0,
              "m_Stress": 0.0,
              "is_death_heart_attack_completed": false,
              "visited_deaths_door": false,
              "deaths_door_enter_effect_round_cooldown": 0,
              "has_had_heart_attack": false,
              "backer_hero": false,
              "steps_taken": 0,
              "enemies_killed": 0,
              "weapon_rank": 0,
              "armour_rank": 0,
              "dd_test_survived": 0,
              "affliction_type_id": "",
              "affliction_severity": 0,
              "virtue_type_id": "",
              "provisions_consumed": 0,
              "quirks": {},
              "skills": {
                "selected_combat_skills": {},
                "selected_camping_skills": {}
              },
              "trinkets": {
                "items": {}
              },
              "has_item_Tracking": true,
              "item_tracking": {
                "supply": {}
              },
              "number_of_successful_darkest_dungeon_quests": 0,
              "is_from_town_event": false,
              "dungeon_history": []
            }
          }
        }
      },
      "2": {
        "hero_file_data": {
          "raw_data": {
            "base_root": {
              "roster.status": 0,
              "roster.before_on_start_town_visit_status": 0,
              "roster.missing_duration": 0,
              "roster.story_variation": 0,
              "roster.missing_from": 0,
              "roster.building_name": "",
              "roster.timestamp": 0,
              "actor": {
                "name": "Existing Highwayman",
                "current_hp": 23.0,
                "stunned": 0,
                "combat_ready": false,
                "damage_source_data": 0,
                "damage_source_type": 0,
                "damage_type": 0,
                "colour_variation": 0,
                "enemy_rank_targets": 0,
                "friendly_rank_targets": 0,
                "performing_turn": 0,
                "controlling_actor_guid": 0,
                "controlling_duration": 0,
                "current_mode_id": 0,
                "rounds_in_ranks": 0,
                "check_round_ranks": 0,
                "health_damage_blocks": 0,
                "buff_group_next_guid": 0,
                "buff_group": {},
                "actor_dot": {}
              },
              "heroClass": "highwayman",
              "resolveXp": 0,
              "m_Stress": 0.0,
              "is_death_heart_attack_completed": false,
              "visited_deaths_door": false,
              "deaths_door_enter_effect_round_cooldown": 0,
              "has_had_heart_attack": false,
              "backer_hero": false,
              "steps_taken": 0,
              "enemies_killed": 0,
              "weapon_rank": 0,
              "armour_rank": 0,
              "dd_test_survived": 0,
              "affliction_type_id": "",
              "affliction_severity": 0,
              "virtue_type_id": "",
              "provisions_consumed": 0,
              "quirks": {},
              "skills": {
                "selected_combat_skills": {},
                "selected_camping_skills": {}
              },
              "trinkets": {
                "items": {}
              },
              "has_item_Tracking": true,
              "item_tracking": {
                "supply": {}
              },
              "number_of_successful_darkest_dungeon_quests": 0,
              "is_from_town_event": false,
              "dungeon_history": []
            }
          }
        }
      }
    },
    "last_party": {
      "last_party_guids": []
    }
  }
}
'@ | Set-Content -LiteralPath $path -Encoding UTF8
}

function Get-DsonHash {
    param([string]$Value)

    [uint64]$hash = 0
    foreach ($byte in [System.Text.Encoding]::UTF8.GetBytes($Value)) {
        $hash = ([uint64]$hash * [uint64]53) + [uint64]$byte
        $hash = $hash % [uint64]4294967296
    }

    return [uint64]$hash
}

function Get-DsonHashSigned {
    param([string]$Value)

    $hash = Get-DsonHash $Value
    if ($hash -gt 2147483647) {
        return [int64]$hash - 4294967296
    }

    return [int64]$hash
}

function Convert-TreeIdToUInt64 {
    param([object]$Value)

    $number = [int64]$Value
    if ($number -lt 0) {
        return [uint64]($number + 4294967296)
    }

    return [uint64]$number
}

function Write-DecodedUpgradesFixture {
    $path = Join-Path $saveRoot "persist.upgrades.json"
    $blacksmithWeaponTreeId = Get-DsonHashSigned "blacksmith.weapon"
    @"
{
  "base_root": {
    "version": 1,
    "purchases": {
      "0": {
        "instance_number": 0,
        "tree_id": $blacksmithWeaponTreeId,
        "requirement_code": "a",
        "is_purchased": false
      }
    }
  }
}
"@ | Set-Content -LiteralPath $path -Encoding UTF8
}

function Get-WalletAmount {
    param(
        [object]$Estate,
        [string]$Currency
    )

    $entries = @($Estate.base_root.wallet.PSObject.Properties | ForEach-Object { $_.Value })
    $entry = @($entries | Where-Object { $_.type -eq $Currency }) | Select-Object -First 1
    Assert-True ($null -ne $entry) "Wallet currency was not found: $Currency"
    return [int]$entry.amount
}

function Get-TrinketAmount {
    param(
        [object]$Estate,
        [string]$Id
    )

    $items = $Estate.base_root.trinkets.items
    if ($null -eq $items) {
        return $null
    }

    $entries = @($items.PSObject.Properties | ForEach-Object { $_.Value })
    $entry = @($entries | Where-Object { $_.type -eq "trinket" -and $_.id -eq $Id }) | Select-Object -First 1
    if ($null -eq $entry) {
        return $null
    }

    return [int]$entry.amount
}

function Get-HeroRoots {
    param([object]$Roster)

    return @($Roster.base_root.heroes.PSObject.Properties | ForEach-Object { $_.Value.hero_file_data.raw_data.base_root })
}

function Get-FirstHeroIdByClass {
    param(
        [object]$Roster,
        [string]$ClassId
    )

    $entry = @($Roster.base_root.heroes.PSObject.Properties | Where-Object {
        $_.Value.hero_file_data.raw_data.base_root.heroClass -eq $ClassId
    }) | Select-Object -First 1
    Assert-True ($null -ne $entry) "Expected at least one hero for class: $ClassId"
    return [int]$entry.Name
}

function Get-HeroClassCount {
    param(
        [object]$Roster,
        [string]$ClassId
    )

    return @((Get-HeroRoots -Roster $Roster) | Where-Object { $_.heroClass -eq $ClassId }).Count
}

function Get-FirstHeroRootByClass {
    param(
        [object]$Roster,
        [string]$ClassId
    )

    return @((Get-HeroRoots -Roster $Roster) | Where-Object { $_.heroClass -eq $ClassId }) | Select-Object -First 1
}

function Get-ObjectPropertyCount {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value.PSObject.Properties).Count
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Test-UpgradePurchase {
    param(
        [object]$Upgrades,
        [string]$TreeName,
        [string]$RequirementCode,
        [int]$InstanceNumber
    )

    $treeId = Get-DsonHash $TreeName
    $entries = @($Upgrades.base_root.purchases.PSObject.Properties | ForEach-Object { $_.Value })
    return @($entries | Where-Object {
        (Convert-TreeIdToUInt64 $_.tree_id) -eq $treeId -and
        [string]$_.requirement_code -eq $RequirementCode -and
        [int]$_.instance_number -eq $InstanceNumber -and
        [bool]$_.is_purchased
    }).Count -gt 0
}

Assert-True (Test-Path -LiteralPath (Join-Path $sourceSaveRoot "persist.estate.json") -PathType Leaf) "Decoded current save fixture is missing persist.estate.json."
New-Item -ItemType Directory -Force -Path $testRoot, $saveRoot | Out-Null
Get-ChildItem -LiteralPath $sourceSaveRoot -Filter "*.json" |
    Copy-Item -Destination $saveRoot -Force
Write-DecodedRosterFixture
Write-DecodedUpgradesFixture

$baseArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $stateRoot
)

Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "profile.initialization_requested"))

$estate = Read-DecodedEstate
$startingGold = Get-WalletAmount -Estate $estate -Currency "gold"
Assert-True ($startingGold -ne 20000) "Fixture should start with a non-normalized gold amount so the write assertion is meaningful."
Assert-True ($null -eq (Get-TrinketAmount -Estate $estate -Id "focus_ring")) "Fixture should start without focus_ring so the trinket write assertion is meaningful."
$roster = Read-DecodedRoster
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "crusader") -eq 1) "Fixture should start with one crusader."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "arbalest") -eq 0) "Fixture should start without arbalest so roster write assertions are meaningful."
$upgrades = Read-DecodedUpgrades
Assert-True (-not (Test-UpgradePurchase -Upgrades $upgrades -TreeName "blacksmith.weapon" -RequirementCode "d" -InstanceNumber 0)) "Fixture should start without max blacksmith weapon upgrade."

Invoke-Loader -LoaderArgs ($baseArgs + @("--apply-managed-actions", "--managed-action-save-dir", $saveRoot))
$dryRunReport = Read-ApplyReport
Assert-True ([bool]$dryRunReport.dryRun) "First apply pass should be dry-run by default."
Assert-True ([int]$dryRunReport.artifactCount -eq 12) "Dry-run should inspect twelve boss gauntlet initialization artifacts."
Assert-True ([int]$dryRunReport.supportedActionCount -eq 5) "Dry-run should recognize five currently supported decoded-save actions."
Assert-True ([int]$dryRunReport.dryRunActionCount -eq 5) "Dry-run should report five dry-run actions."
Assert-True ([int]$dryRunReport.appliedActionCount -eq 0) "Dry-run should not report written actions."
Assert-True ([int]$dryRunReport.unsupportedActionCount -eq 7) "Dry-run should report the remaining profile-normalization actions as unsupported."
Assert-True ([int]$dryRunReport.failedActionCount -eq 0) "Dry-run should not fail on unsupported future actions."
Assert-True ([int]$dryRunReport.changedFileCount -eq 3) "Dry-run should report three would-change decoded save files."

$estate = Read-DecodedEstate
Assert-True ((Get-WalletAmount -Estate $estate -Currency "gold") -eq $startingGold) "Dry-run must not modify decoded save JSON."
Assert-True ($null -eq (Get-TrinketAmount -Estate $estate -Id "focus_ring")) "Dry-run must not add trinkets to decoded save JSON."
$roster = Read-DecodedRoster
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "crusader") -eq 1) "Dry-run must not add roster heroes."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "arbalest") -eq 0) "Dry-run must not add missing roster classes."
$crusader = Get-FirstHeroRootByClass -Roster $roster -ClassId "crusader"
Assert-True ((Get-ObjectPropertyCount -Value $crusader.skills.selected_combat_skills) -eq 0) "Dry-run must not fill existing hero combat skills."
Assert-True ((Get-ObjectPropertyCount -Value $crusader.skills.selected_camping_skills) -eq 0) "Dry-run must not fill existing hero camping skills."
$upgrades = Read-DecodedUpgrades
Assert-True (-not (Test-UpgradePurchase -Upgrades $upgrades -TreeName "blacksmith.weapon" -RequirementCode "d" -InstanceNumber 0)) "Dry-run must not add upgrade purchases."

Invoke-Loader -LoaderArgs ($baseArgs + @("--apply-managed-actions", "--write-managed-actions", "--managed-action-save-dir", $saveRoot))
$writeReport = Read-ApplyReport
Assert-True (-not [bool]$writeReport.dryRun) "Write pass should record dryRun=false."
Assert-True ([int]$writeReport.supportedActionCount -eq 5) "Write pass should recognize five currently supported decoded-save actions."
Assert-True ([int]$writeReport.dryRunActionCount -eq 0) "Write pass should not report dry-run actions."
Assert-True ([int]$writeReport.appliedActionCount -eq 5) "Write pass should apply five currently supported decoded-save actions."
Assert-True ([int]$writeReport.changedFileCount -eq 3) "Write pass should change three decoded save files."
Assert-True (@(Convert-ToArray $writeReport.files | Where-Object { $_.written -eq $true }).Count -eq 3) "Write pass should mark three files as written."

$estate = Read-DecodedEstate
Assert-True ((Get-WalletAmount -Estate $estate -Currency "gold") -eq 20000) "Write pass should set starting gold to 20000."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "bust") -eq 0) "Write pass should set starting busts to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "portrait") -eq 0) "Write pass should set starting portraits to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "deed") -eq 0) "Write pass should set starting deeds to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "crest") -eq 0) "Write pass should set starting crests to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "shard") -eq 0) "Write pass should set starting shards to 0."
Assert-True ((Get-TrinketAmount -Estate $estate -Id "focus_ring") -eq 2) "Write pass should add two copies of focus_ring."
Assert-True ((Get-TrinketAmount -Estate $estate -Id "berserk_mask") -eq 2) "Write pass should add two copies of berserk_mask."
$roster = Read-DecodedRoster
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "crusader") -eq 2) "Write pass should ensure two crusaders."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "highwayman") -eq 2) "Write pass should ensure two highwaymen."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "arbalest") -eq 2) "Write pass should ensure two arbalests."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "vestal") -eq 2) "Write pass should ensure two vestals."
$crusader = Get-FirstHeroRootByClass -Roster $roster -ClassId "crusader"
$arbalest = Get-FirstHeroRootByClass -Roster $roster -ClassId "arbalest"
Assert-True ([int]$arbalest.resolveXp -eq 46) "Generated max-level heroes should use max resolve XP."
Assert-True ([int]$arbalest.weapon_rank -eq 4) "Generated max-level heroes should use max weapon rank."
Assert-True ([int]$arbalest.armour_rank -eq 4) "Generated max-level heroes should use max armour rank."
Assert-True ($null -eq $arbalest.template_only_marker) "Generated heroes must be built from a clean blueprint, not copied from an existing roster template."
Assert-True (@($arbalest.quirks.PSObject.Properties).Count -eq 6) "Generated heroes should have five positive quirks and one negative quirk."
Assert-True ((Get-ObjectPropertyCount -Value $crusader.skills.selected_combat_skills) -gt 4) "Skill unlock action should fill all known crusader combat skills."
Assert-True ((Get-ObjectPropertyCount -Value $crusader.skills.selected_camping_skills) -gt 4) "Skill unlock action should fill all known crusader camping skills."
Assert-True ((Get-ObjectPropertyCount -Value $arbalest.skills.selected_combat_skills) -gt 4) "Generated heroes should receive all known combat skills from content definitions."
Assert-True ((Get-ObjectPropertyCount -Value $arbalest.skills.selected_camping_skills) -gt 4) "Generated heroes should receive all known camping skills from content definitions."
$rosterText = Get-Content -Raw -LiteralPath (Join-Path $saveRoot "persist.roster.json")
Assert-True ($rosterText -match '"current_hp": 47\.0') "Generated DSON-decoded roster should preserve float token shape for current_hp."
Assert-True ($rosterText -match '"m_Stress": 0\.0') "Generated DSON-decoded roster should preserve float token shape for m_Stress."
$upgrades = Read-DecodedUpgrades
$crusaderId = Get-FirstHeroIdByClass -Roster $roster -ClassId "crusader"
$arbalestId = Get-FirstHeroIdByClass -Roster $roster -ClassId "arbalest"
Assert-True (Test-UpgradePurchase -Upgrades $upgrades -TreeName "blacksmith.weapon" -RequirementCode "a" -InstanceNumber 0) "Write pass should update existing false building purchase to purchased."
Assert-True (Test-UpgradePurchase -Upgrades $upgrades -TreeName "blacksmith.weapon" -RequirementCode "d" -InstanceNumber 0) "Write pass should add max building upgrade purchase."
Assert-True (Test-UpgradePurchase -Upgrades $upgrades -TreeName "crusader.smite" -RequirementCode "4" -InstanceNumber $crusaderId) "Write pass should max an existing hero combat skill upgrade."
Assert-True (Test-UpgradePurchase -Upgrades $upgrades -TreeName "arbalest.sniper_shot" -RequirementCode "4" -InstanceNumber $arbalestId) "Write pass should max a generated hero combat skill upgrade."

Write-Host "PASS: managed action save applier dry-run and decoded wallet/trinket/roster/skill/upgrade write assertions passed."
