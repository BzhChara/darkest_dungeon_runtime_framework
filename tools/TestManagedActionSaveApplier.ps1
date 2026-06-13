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
$saveEditorJar = Join-Path $projectRoot.Path ".research\DDSaveEditor-v0.0.70\DDSaveEditor.jar"
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

function Invoke-DDSaveEditorEncodeProbe {
    param([string]$FileName)

    Assert-True (Test-Path -LiteralPath $saveEditorJar -PathType Leaf) "DDSaveEditor jar is missing: $saveEditorJar"
    $inputPath = Join-Path $saveRoot $FileName
    Assert-True (Test-Path -LiteralPath $inputPath -PathType Leaf) "Decoded file missing for encode probe: $inputPath"
    $outputPath = Join-Path $stateRoot ("encoded_" + $FileName + ".bin")
    & java -jar $saveEditorJar encode --output $outputPath $inputPath
    if ($LASTEXITCODE -ne 0) {
        throw "DDSaveEditor encode failed for $FileName with exit code $LASTEXITCODE"
    }

    Assert-True (Test-Path -LiteralPath $outputPath -PathType Leaf) "DDSaveEditor encode did not create output: $outputPath"
    Assert-True ((Get-Item -LiteralPath $outputPath).Length -gt 0) "DDSaveEditor encode output was empty: $outputPath"
}

function Invoke-DDSaveEditorEncodeFile {
    param(
        [string]$DecodedPath,
        [string]$OutputPath
    )

    Assert-True (Test-Path -LiteralPath $saveEditorJar -PathType Leaf) "DDSaveEditor jar is missing: $saveEditorJar"
    Assert-True (Test-Path -LiteralPath $DecodedPath -PathType Leaf) "Decoded file missing for encode: $DecodedPath"
    & java -jar $saveEditorJar encode --output $OutputPath $DecodedPath
    if ($LASTEXITCODE -ne 0) {
        throw "DDSaveEditor encode failed for $DecodedPath with exit code $LASTEXITCODE"
    }

    Assert-True (Test-Path -LiteralPath $OutputPath -PathType Leaf) "DDSaveEditor encode did not create output: $OutputPath"
    Assert-True ((Get-Item -LiteralPath $OutputPath).Length -gt 0) "DDSaveEditor encode output was empty: $OutputPath"
}

function Read-ApplyReport {
    $path = Join-Path $projectRoot.Path "logs\managed_action_apply_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Managed action apply report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedProfileInitializationReport {
    $path = Join-Path $projectRoot.Path "logs\decoded_profile_initialization_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded profile initialization report was not created: $path"
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

function Read-DecodedTown {
    $path = Join-Path $saveRoot "persist.town.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded town file was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
}

function Read-DecodedQuest {
    $path = Join-Path $saveRoot "persist.quest.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded quest file was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedProgression {
    $path = Join-Path $saveRoot "persist.progression.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded progression file was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedTownEvent {
    $path = Join-Path $saveRoot "persist.town_event.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded town event file was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedProfilePolicy {
    $path = Join-Path $saveRoot "_ddrt_profile_policy.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded profile policy file was not created: $path"
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
              "number_of_successful_darkest_dungeon_quests": 1,
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
                "name": "DDRF Highwayman 2",
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

function Write-DecodedTownFixture {
    $path = Join-Path $saveRoot "persist.town.json"
    @'
{
  "base_root": {
    "version": 513,
    "buildings": {
      "stage_coach": {
        "activities": {},
        "store": {
          "hero_recruit": {
            "generated": {
              "10": {
                "heroClass": "crusader",
                "actor": {
                  "name": "Recruit A",
                  "current_hp": 33.0
                }
              },
              "11": {
                "heroClass": "highwayman",
                "actor": {
                  "name": "Recruit B",
                  "current_hp": 23.0
                }
              }
            }
          },
          "bonus_recruit": {
            "generated": {}
          }
        }
      },
      "blacksmith": {},
      "guild": {},
      "tavern": {}
    },
    "districts": {
      "buildings": {
        "bank": {
          "built": false,
          "buffs": {
            "estate": {}
          }
        },
        "granary": {
          "built": false,
          "buffs": {
            "provision": {
              "count": 0
            }
          }
        },
        "library": {
          "built": true,
          "buffs": {
            "hero_buff0": {}
          }
        }
      }
    }
  }
}
'@ | Set-Content -LiteralPath $path -Encoding UTF8
}

function Write-DecodedTownEventFixture {
    $path = Join-Path $saveRoot "persist.town_event.json"
    @'
{
  "base_root": {
    "version": 516,
    "last_town_event_week": 3,
    "rng_seed": 123456,
    "current_result_event_id": 123456789,
    "has_unclaimed_interaction": true,
    "event_cost": {
      "0": {
        "amount": 500,
        "type": "gold"
      }
    },
    "bonus_hero_entries": {},
    "dead_hero_entries": [42],
    "free_upgrade_tags": {
      "0": {
        "tag": "blacksmith.weapon"
      }
    },
    "non_rolled_additional_chances": {},
    "result_event_history": [123456789]
  }
}
'@ | Set-Content -LiteralPath $path -Encoding UTF8
}

function Write-DecodedQuestFixture {
    $path = Join-Path $saveRoot "persist.quest.json"
    @'
{
  "base_root": {
    "version": 41,
    "quests": {
      "0": {
        "id": "plot_tutorial_crypts",
        "map_name": "tutorial_crypts",
        "torch_setting": "",
        "raid_rules_override": "",
        "is_plot_quest": true,
        "type": "explore",
        "dungeon": "crypts",
        "difficulty": 1,
        "length": 1,
        "counted_in_generation": true,
        "goal_ids": [
          "explore_all_rooms"
        ],
        "progression_goal_ids": 0,
        "use_default_progression_goals": true,
        "completion_reward": {
          "resolve_xp": 2,
          "resolve_xp_per_wave_kill": 0,
          "items_definition": {
            "items": {
              "0": {
                "id": "",
                "type": "gold",
                "amount": 3000
              }
            }
          },
          "additional_threshold_trinket_rewards": {},
          "trinket_retention_ids": [],
          "max_times_dungeon_xp_awarded": 0
        },
        "threshold_rewards": {},
        "completion_threshold": 0,
        "is_from_town_event": false
      }
    },
    "trinket_retention_ids": [],
    "plot_quest_total": 44
  }
}
'@ | Set-Content -LiteralPath $path -Encoding UTF8
}

function Write-DecodedProgressionFixture {
    $path = Join-Path $saveRoot "persist.progression.json"
    $tutorialHash = Get-DsonHashSigned "plot_tutorial_crypts"
    $dd1Hash = Get-DsonHashSigned "plot_darkest_dungeon_1"
    $dd2Hash = Get-DsonHashSigned "plot_darkest_dungeon_2"
    $dd3Hash = Get-DsonHashSigned "plot_darkest_dungeon_3"
    @"
{
  "base_root": {
    "version": 2,
    "dungeon": {
      "crypts": {
        "xp": 4
      }
    },
    "completed_plot_quests_data": {
      "0": {
        "plot_quest_id": $tutorialHash,
        "heroes": {
          "0": {
            "guid": 1,
            "survived": true,
            "last_blow": false
          }
        }
      },
      "1": {
        "plot_quest_id": $dd1Hash,
        "heroes": {
          "0": {
            "guid": 1,
            "survived": true,
            "last_blow": true
          }
        }
      },
      "2": {
        "plot_quest_id": $dd2Hash,
        "heroes": {
          "0": {
            "guid": 2,
            "survived": true,
            "last_blow": false
          }
        }
      }
    },
    "total_recruited_stage_coach_heroes": 2,
    "total_quests_finished": 3,
    "total_successful_quests_finished": 3,
    "last_quest_played_successfully": true,
    "last_quest_played_id": $dd2Hash,
    "last_quest_played_xp": 8,
    "last_raid_success": true,
    "last_raid_was_a_plot_quest": true,
    "last_raid_quest_id": $dd3Hash,
    "achievements": {
      "plot_tutorial_crypts": {
        "rtti": 196099018,
        "id": "plot_tutorial_crypts",
        "completed": true,
        "awarded": false
      },
      "plot_darkest_dungeon_1": {
        "rtti": 196099018,
        "id": "plot_darkest_dungeon_1",
        "completed": true,
        "awarded": true
      },
      "plot_darkest_dungeon_2": {
        "rtti": 196099018,
        "id": "plot_darkest_dungeon_2",
        "completed": true,
        "awarded": false
      },
      "plot_darkest_dungeon_3": {
        "rtti": 196099018,
        "id": "plot_darkest_dungeon_3",
        "completed": true,
        "awarded": false
      }
    },
    "real_achievements": {},
    "flashback_completion_counts": {}
  }
}
"@ | Set-Content -LiteralPath $path -Encoding UTF8
}

function Write-CompletedQuestStateFixture {
    param([string[]]$QuestIds)

    $path = Join-Path $stateRoot "$pluginId.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Plugin state file was not created: $path"
    $json = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    $json.state.bossGauntlet.completedQuestIds = @($QuestIds)
    $json | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding UTF8
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

function Get-TrinketCopies {
    param(
        [object]$Estate,
        [string]$Id
    )

    $items = $Estate.base_root.trinkets.items
    if ($null -eq $items) {
        return 0
    }

    $entries = @($items.PSObject.Properties | ForEach-Object { $_.Value })
    $matches = @($entries | Where-Object { $_.type -eq "trinket" -and $_.id -eq $Id })
    foreach ($entry in $matches) {
        Assert-True ([int]$entry.amount -eq 1) "Non-stackable trinket '$Id' should be represented as independent amount=1 entries."
    }

    return $matches.Count
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

function Get-CompletedPlotQuestHashes {
    param([object]$Progression)

    return @($Progression.base_root.completed_plot_quests_data.PSObject.Properties | ForEach-Object {
        [int64]$_.Value.plot_quest_id
    })
}

function Get-FirstHeroRootByClass {
    param(
        [object]$Roster,
        [string]$ClassId
    )

    return @((Get-HeroRoots -Roster $Roster) | Where-Object { $_.heroClass -eq $ClassId }) | Select-Object -First 1
}

function Get-HeroNamePool {
    param([string]$Language)

    $config = Get-Content -Raw -LiteralPath (Resolve-ProjectPath $ConfigPath) | ConvertFrom-Json
    $path = Join-Path ([string]$config.gameWorkingDirectory) "localization\names.string_table.xml"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Hero name string table was not found: $path"
    [xml]$document = Get-Content -Raw -LiteralPath $path
    $languageNode = @($document.root.language | Where-Object { $_.id -eq $Language }) | Select-Object -First 1
    Assert-True ($null -ne $languageNode) "Hero name string table language was not found: $Language"
    return @($languageNode.entry | Where-Object { $_.id -like "hero_name_*" } | ForEach-Object { [string]$_.InnerText })
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

function Get-StagecoachGeneratedRecruitCount {
    param([hashtable]$Town)

    $stagecoach = $Town["base_root"]["buildings"]["stage_coach"]
    $count = 0
    foreach ($store in $stagecoach["store"].Values) {
        if ($store.ContainsKey("generated")) {
            $count += $store["generated"].Count
        }
    }

    return $count
}

function Get-DistrictBuilt {
    param(
        [hashtable]$Town,
        [string]$DistrictId
    )

    return [bool]$Town["base_root"]["districts"]["buildings"][$DistrictId]["built"]
}

function Get-QuestIds {
    param([object]$Quest)

    return @($Quest.base_root.quests.PSObject.Properties |
        Sort-Object { [int]$_.Name } |
        ForEach-Object { [string]$_.Value.id })
}

function Get-QuestById {
    param(
        [object]$Quest,
        [string]$Id
    )

    $entry = @($Quest.base_root.quests.PSObject.Properties |
        ForEach-Object { $_.Value } |
        Where-Object { $_.id -eq $Id }) | Select-Object -First 1
    Assert-True ($null -ne $entry) "Expected quest board entry: $Id"
    return $entry
}

function Test-QuestRewardItem {
    param(
        [object]$QuestEntry,
        [string]$Type,
        [string]$Id,
        [int]$Amount
    )

    $items = @($QuestEntry.completion_reward.items_definition.items.PSObject.Properties | ForEach-Object { $_.Value })
    return @($items | Where-Object {
        [string]$_.type -eq $Type -and
        [string]$_.id -eq $Id -and
        [int]$_.amount -eq $Amount
    }).Count -gt 0
}

Assert-True (Test-Path -LiteralPath (Join-Path $sourceSaveRoot "persist.estate.json") -PathType Leaf) "Decoded current save fixture is missing persist.estate.json."
New-Item -ItemType Directory -Force -Path $testRoot, $saveRoot | Out-Null
Get-ChildItem -LiteralPath $sourceSaveRoot -Filter "*.json" |
    Copy-Item -Destination $saveRoot -Force
Write-DecodedRosterFixture
Write-DecodedUpgradesFixture
Write-DecodedTownFixture
Write-DecodedTownEventFixture
Write-DecodedQuestFixture
Write-DecodedProgressionFixture

$baseArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $stateRoot
)

$estate = Read-DecodedEstate
$startingGold = Get-WalletAmount -Estate $estate -Currency "gold"
Assert-True ($startingGold -ne 20000) "Fixture should start with a non-normalized gold amount so the write assertion is meaningful."
Assert-True ((Get-TrinketCopies -Estate $estate -Id "focus_ring") -eq 0) "Fixture should start without focus_ring so the trinket write assertion is meaningful."
$roster = Read-DecodedRoster
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "crusader") -eq 1) "Fixture should start with one crusader."
$initialCrusader = Get-FirstHeroRootByClass -Roster $roster -ClassId "crusader"
$initialHighwayman = Get-FirstHeroRootByClass -Roster $roster -ClassId "highwayman"
Assert-True ([string]$initialHighwayman.actor.name -eq "DDRF Highwayman 2") "Fixture should start with one old generated placeholder hero name."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "arbalest") -eq 0) "Fixture should start without arbalest so roster write assertions are meaningful."
$upgrades = Read-DecodedUpgrades
Assert-True (-not (Test-UpgradePurchase -Upgrades $upgrades -TreeName "blacksmith.weapon" -RequirementCode "d" -InstanceNumber 0)) "Fixture should start without max blacksmith weapon upgrade."
$town = Read-DecodedTown
Assert-True ((Get-StagecoachGeneratedRecruitCount -Town $town) -eq 2) "Fixture should start with two generated stagecoach recruits."
Assert-True (-not (Get-DistrictBuilt -Town $town -DistrictId "bank")) "Fixture should start with bank district unbuilt."
Assert-True (-not (Get-DistrictBuilt -Town $town -DistrictId "granary")) "Fixture should start with granary district unbuilt."
$quest = Read-DecodedQuest
Assert-True ((Get-QuestIds -Quest $quest).Count -eq 1) "Fixture should start with one quest board entry."
Assert-True ((Get-QuestIds -Quest $quest) -contains "plot_tutorial_crypts") "Fixture should start with tutorial quest."
Assert-True (-not ((Get-QuestIds -Quest $quest) -contains "plot_kill_necromancer_3")) "Fixture should start without fixed boss quests."
$progression = Read-DecodedProgression
$completedPlotQuestHashes = @(Get-CompletedPlotQuestHashes -Progression $progression)
Assert-True ($completedPlotQuestHashes -contains (Get-DsonHashSigned "plot_tutorial_crypts")) "Fixture should start with tutorial completed plot data."
Assert-True ($completedPlotQuestHashes -contains (Get-DsonHashSigned "plot_darkest_dungeon_1")) "Fixture should start with DD1 completed plot data."
Assert-True ($completedPlotQuestHashes -contains (Get-DsonHashSigned "plot_darkest_dungeon_2")) "Fixture should start with DD2 completed plot data."
Assert-True ([bool]$progression.base_root.achievements.plot_darkest_dungeon_1.completed) "Fixture should start with DD1 achievement completed."
Assert-True ([int]$initialCrusader.number_of_successful_darkest_dungeon_quests -eq 1) "Fixture should start with one old hero marked as a DD survivor."
$townEvent = Read-DecodedTownEvent
Assert-True ([int]$townEvent.base_root.current_result_event_id -ne 0) "Fixture should start with a current town event id."
Assert-True ([bool]$townEvent.base_root.has_unclaimed_interaction) "Fixture should start with an unclaimed town event interaction."
Assert-True (-not (Test-Path -LiteralPath (Join-Path $saveRoot "_ddrt_profile_policy.json") -PathType Leaf)) "Fixture should start without a generated profile policy file."

Invoke-Loader -LoaderArgs ($baseArgs + @("--initialize-decoded-profile", "--managed-action-save-dir", $saveRoot))
$dryRunInitializationReport = Read-DecodedProfileInitializationReport
Assert-True ([bool]$dryRunInitializationReport.succeeded) "Decoded profile dry-run initialization should succeed."
Assert-True ([bool]$dryRunInitializationReport.dryRun) "Decoded profile dry-run initialization should record dryRun=true."
Assert-True ([bool]$dryRunInitializationReport.stateSucceeded) "Decoded profile dry-run initialization should initialize sidecar state."
Assert-True ([bool]$dryRunInitializationReport.eventSucceeded) "Decoded profile dry-run initialization should run the initialization event."
Assert-True ([int]$dryRunInitializationReport.materializedActionCount -eq 13) "Decoded profile dry-run initialization should materialize thirteen actions."
Assert-True ([bool]$dryRunInitializationReport.questBoardPreviewSucceeded) "Decoded profile dry-run initialization should preview the quest board."
Assert-True ([int]$dryRunInitializationReport.questBoardCandidateCount -eq 8) "Decoded profile dry-run initialization should preview eight fixed boss quests."
Assert-True (-not [bool]$dryRunInitializationReport.applySkipped) "Decoded profile dry-run initialization should run managed action apply."
Assert-True ([bool]$dryRunInitializationReport.applySucceeded) "Decoded profile dry-run initialization apply should succeed."
Assert-True ([int]$dryRunInitializationReport.applySupportedActionCount -eq 12) "Decoded profile dry-run initialization should recognize twelve supported decoded-save/policy actions."
Assert-True ([int]$dryRunInitializationReport.applyDryRunActionCount -eq 12) "Decoded profile dry-run initialization should dry-run twelve supported actions."
Assert-True ([int]$dryRunInitializationReport.applyChangedFileCount -eq 8) "Decoded profile dry-run initialization should report eight would-change decoded save or policy files."
Assert-True (@(Convert-ToArray $dryRunInitializationReport.applyActions | Where-Object { $_.actionType -eq "roster.setProgression" -and $_.status -eq "dry-run" }).Count -eq 1) "Decoded profile initialization report should include roster.setProgression dry-run action details."
$dryRunReport = Read-ApplyReport
Assert-True ([bool]$dryRunReport.dryRun) "First apply pass should be dry-run by default."
Assert-True ([int]$dryRunReport.artifactCount -eq 13) "Dry-run should inspect thirteen boss gauntlet initialization artifacts."
Assert-True ([int]$dryRunReport.supportedActionCount -eq 12) "Dry-run should recognize twelve currently supported decoded-save/policy actions."
Assert-True ([int]$dryRunReport.dryRunActionCount -eq 12) "Dry-run should report twelve dry-run actions."
Assert-True ([int]$dryRunReport.appliedActionCount -eq 0) "Dry-run should not report written actions."
Assert-True ([int]$dryRunReport.unsupportedActionCount -eq 1) "Dry-run should report the remaining profile-normalization action as unsupported."
Assert-True ([int]$dryRunReport.failedActionCount -eq 0) "Dry-run should not fail on unsupported future actions."
Assert-True ([int]$dryRunReport.changedFileCount -eq 8) "Dry-run should report eight would-change decoded save or policy files."

$estate = Read-DecodedEstate
Assert-True ((Get-WalletAmount -Estate $estate -Currency "gold") -eq $startingGold) "Dry-run must not modify decoded save JSON."
Assert-True ((Get-TrinketCopies -Estate $estate -Id "focus_ring") -eq 0) "Dry-run must not add trinkets to decoded save JSON."
$roster = Read-DecodedRoster
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "crusader") -eq 1) "Dry-run must not add roster heroes."
$dryRunHighwayman = Get-FirstHeroRootByClass -Roster $roster -ClassId "highwayman"
Assert-True ([string]$dryRunHighwayman.actor.name -eq "DDRF Highwayman 2") "Dry-run must not rename old generated placeholder hero names."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "arbalest") -eq 0) "Dry-run must not add missing roster classes."
$crusader = Get-FirstHeroRootByClass -Roster $roster -ClassId "crusader"
Assert-True ((Get-ObjectPropertyCount -Value $crusader.skills.selected_combat_skills) -eq 0) "Dry-run must not fill existing hero combat skills."
Assert-True ((Get-ObjectPropertyCount -Value $crusader.skills.selected_camping_skills) -eq 0) "Dry-run must not fill existing hero camping skills."
$upgrades = Read-DecodedUpgrades
Assert-True (-not (Test-UpgradePurchase -Upgrades $upgrades -TreeName "blacksmith.weapon" -RequirementCode "d" -InstanceNumber 0)) "Dry-run must not add upgrade purchases."
$town = Read-DecodedTown
Assert-True ((Get-StagecoachGeneratedRecruitCount -Town $town) -eq 2) "Dry-run must not remove generated stagecoach recruits."
Assert-True (-not (Get-DistrictBuilt -Town $town -DistrictId "bank")) "Dry-run must not mark bank district built."
Assert-True (-not (Get-DistrictBuilt -Town $town -DistrictId "granary")) "Dry-run must not mark granary district built."
$quest = Read-DecodedQuest
Assert-True ((Get-QuestIds -Quest $quest).Count -eq 1) "Dry-run must not replace quest board entries."
Assert-True ((Get-QuestIds -Quest $quest) -contains "plot_tutorial_crypts") "Dry-run must keep tutorial quest."
Assert-True (-not ((Get-QuestIds -Quest $quest) -contains "plot_kill_necromancer_3")) "Dry-run must not add fixed boss quests."
$progression = Read-DecodedProgression
$completedPlotQuestHashes = @(Get-CompletedPlotQuestHashes -Progression $progression)
Assert-True ($completedPlotQuestHashes -contains (Get-DsonHashSigned "plot_darkest_dungeon_1")) "Dry-run must not remove DD1 completed plot data."
Assert-True ([bool]$progression.base_root.achievements.plot_darkest_dungeon_1.completed) "Dry-run must not reset DD1 achievement state."
$townEvent = Read-DecodedTownEvent
Assert-True ([int]$townEvent.base_root.current_result_event_id -eq 123456789) "Dry-run must not suppress the current town event."
Assert-True ([bool]$townEvent.base_root.has_unclaimed_interaction) "Dry-run must not clear town event interaction state."
Assert-True (-not (Test-Path -LiteralPath (Join-Path $saveRoot "_ddrt_profile_policy.json") -PathType Leaf)) "Dry-run must not write the profile policy file."

Invoke-Loader -LoaderArgs ($baseArgs + @("--initialize-decoded-profile", "--write-managed-actions", "--managed-action-save-dir", $saveRoot))
$writeInitializationReport = Read-DecodedProfileInitializationReport
Assert-True ([bool]$writeInitializationReport.succeeded) "Decoded profile write initialization should succeed."
Assert-True (-not [bool]$writeInitializationReport.dryRun) "Decoded profile write initialization should record dryRun=false."
Assert-True ([bool]$writeInitializationReport.stateSucceeded) "Decoded profile write initialization should keep sidecar state valid."
Assert-True ([bool]$writeInitializationReport.eventSucceeded) "Decoded profile write initialization event should succeed even after initialized=true."
Assert-True (-not [bool]$writeInitializationReport.applySkipped) "Decoded profile write initialization should run managed action apply."
Assert-True ([bool]$writeInitializationReport.applySucceeded) "Decoded profile write initialization apply should succeed."
Assert-True ([int]$writeInitializationReport.applyAppliedActionCount -eq 12) "Decoded profile write initialization should apply twelve supported decoded-save/policy actions."
Assert-True ([int]$writeInitializationReport.applyChangedFileCount -eq 8) "Decoded profile write initialization should write eight decoded save or policy files."
Assert-True (@(Convert-ToArray $writeInitializationReport.applyActions | Where-Object { $_.actionType -eq "roster.setProgression" -and $_.status -eq "applied" }).Count -eq 1) "Decoded profile initialization report should include roster.setProgression applied action details."
$writeReport = Read-ApplyReport
Assert-True (-not [bool]$writeReport.dryRun) "Write pass should record dryRun=false."
Assert-True ([int]$writeReport.supportedActionCount -eq 12) "Write pass should recognize twelve currently supported decoded-save/policy actions."
Assert-True ([int]$writeReport.dryRunActionCount -eq 0) "Write pass should not report dry-run actions."
Assert-True ([int]$writeReport.appliedActionCount -eq 12) "Write pass should apply twelve currently supported decoded-save/policy actions."
Assert-True ([int]$writeReport.unsupportedActionCount -eq 1) "Write pass should leave only one future profile-normalization action unsupported."
Assert-True ([int]$writeReport.changedFileCount -eq 8) "Write pass should change eight decoded save or policy files."
Assert-True (@(Convert-ToArray $writeReport.files | Where-Object { $_.written -eq $true }).Count -eq 8) "Write pass should mark eight files as written."

$estate = Read-DecodedEstate
Assert-True ((Get-WalletAmount -Estate $estate -Currency "gold") -eq 20000) "Write pass should set starting gold to 20000."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "bust") -eq 0) "Write pass should set starting busts to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "portrait") -eq 0) "Write pass should set starting portraits to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "deed") -eq 0) "Write pass should set starting deeds to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "crest") -eq 0) "Write pass should set starting crests to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "shard") -eq 0) "Write pass should set starting shards to 0."
Assert-True ((Get-TrinketCopies -Estate $estate -Id "focus_ring") -eq 2) "Write pass should add two independent copies of focus_ring."
Assert-True ((Get-TrinketCopies -Estate $estate -Id "berserk_mask") -eq 2) "Write pass should add two independent copies of berserk_mask."
$roster = Read-DecodedRoster
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "crusader") -eq 2) "Write pass should ensure two crusaders."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "highwayman") -eq 2) "Write pass should ensure two highwaymen."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "arbalest") -eq 2) "Write pass should ensure two arbalests."
Assert-True ((Get-HeroClassCount -Roster $roster -ClassId "vestal") -eq 2) "Write pass should ensure two vestals."
$crusader = Get-FirstHeroRootByClass -Roster $roster -ClassId "crusader"
$highwayman = Get-FirstHeroRootByClass -Roster $roster -ClassId "highwayman"
$arbalest = Get-FirstHeroRootByClass -Roster $roster -ClassId "arbalest"
Assert-True ([int]$crusader.resolveXp -eq 46) "Progression action should set existing heroes to max resolve XP."
Assert-True ([int]$crusader.weapon_rank -eq 4) "Progression action should set existing heroes to max weapon rank."
Assert-True ([int]$crusader.armour_rank -eq 4) "Progression action should set existing heroes to max armour rank."
Assert-True ([double]$crusader.actor.current_hp -gt 33.0) "Progression action should heal existing heroes to class max HP when maxing equipment."
Assert-True ([int]$arbalest.resolveXp -eq 46) "Generated max-level heroes should use max resolve XP."
Assert-True ([int]$arbalest.weapon_rank -eq 4) "Generated max-level heroes should use max weapon rank."
Assert-True ([int]$arbalest.armour_rank -eq 4) "Generated max-level heroes should use max armour rank."
Assert-True ([int]$crusader.number_of_successful_darkest_dungeon_quests -eq 0) "Campaign progress reset should clear old hero DD survivor counts."
Assert-True ($null -eq $arbalest.template_only_marker) "Generated heroes must be built from a clean blueprint, not copied from an existing roster template."
Assert-True (-not ([string]$arbalest.actor.name -like "DDRF *")) "Generated heroes should use a configured content name pool instead of DDRF placeholder names."
$heroNamePool = Get-HeroNamePool -Language "schinese"
Assert-True ($heroNamePool -contains [string]$arbalest.actor.name) "Generated hero name should come from the configured Schinese hero name pool."
Assert-True ([string]$crusader.actor.name -eq "Existing Crusader") "Roster name normalization should not rename non-placeholder existing hero names."
Assert-True (-not ([string]$highwayman.actor.name -like "DDRF *")) "Roster name normalization should rename old DDRF placeholder hero names."
Assert-True ($heroNamePool -contains [string]$highwayman.actor.name) "Renamed old placeholder hero name should come from the configured Schinese hero name pool."
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
$town = Read-DecodedTown
Assert-True ((Get-StagecoachGeneratedRecruitCount -Town $town) -eq 0) "Write pass should remove generated stagecoach recruits."
Assert-True (Get-DistrictBuilt -Town $town -DistrictId "bank") "Write pass should mark bank district built."
Assert-True (Get-DistrictBuilt -Town $town -DistrictId "granary") "Write pass should mark granary district built."
Assert-True (Get-DistrictBuilt -Town $town -DistrictId "library") "Write pass should keep already-built district built."
$quest = Read-DecodedQuest
$questIds = @(Get-QuestIds -Quest $quest)
Assert-True ($questIds.Count -eq 8) "Write pass should replace the quest board with eight fixed boss quests."
Assert-True ($questIds[0] -eq "plot_kill_necromancer_3") "Write pass should keep fixed quest order from the action plan."
Assert-True ($questIds[1] -eq "plot_kill_prophet_3") "Write pass should keep fixed quest order from the action plan."
$progression = Read-DecodedProgression
$completedPlotQuestHashes = @(Get-CompletedPlotQuestHashes -Progression $progression)
Assert-True ($completedPlotQuestHashes -contains (Get-DsonHashSigned "plot_tutorial_crypts")) "Campaign progress reset should keep unrelated completed plot data."
Assert-True (-not ($completedPlotQuestHashes -contains (Get-DsonHashSigned "plot_darkest_dungeon_1"))) "Campaign progress reset should remove DD1 completed plot data."
Assert-True (-not ($completedPlotQuestHashes -contains (Get-DsonHashSigned "plot_darkest_dungeon_2"))) "Campaign progress reset should remove DD2 completed plot data."
Assert-True (-not [bool]$progression.base_root.achievements.plot_darkest_dungeon_1.completed) "Campaign progress reset should clear DD1 achievement completion."
Assert-True (-not [bool]$progression.base_root.achievements.plot_darkest_dungeon_1.awarded) "Campaign progress reset should clear DD1 achievement awarded state."
Assert-True ([bool]$progression.base_root.achievements.plot_tutorial_crypts.completed) "Campaign progress reset should keep unrelated achievement completion."
Assert-True ([int]$progression.base_root.last_quest_played_id -eq 0) "Campaign progress reset should clear stale last quest references when they point to a reset plot quest."
Assert-True ([int]$progression.base_root.last_raid_quest_id -eq 0) "Campaign progress reset should clear stale last raid references when they point to a reset plot quest."
$townEvent = Read-DecodedTownEvent
Assert-True ([int]$townEvent.base_root.current_result_event_id -eq 0) "Write pass should suppress the current town event id."
Assert-True (-not [bool]$townEvent.base_root.has_unclaimed_interaction) "Write pass should clear unclaimed town event interaction state."
Assert-True ((Get-ObjectPropertyCount -Value $townEvent.base_root.event_cost) -eq 0) "Write pass should clear current town event costs."
Assert-True ((Convert-ToArray $townEvent.base_root.dead_hero_entries).Count -eq 0) "Write pass should clear current town event dead hero entries."
$profilePolicy = Read-DecodedProfilePolicy
Assert-True ([bool]$profilePolicy.profilePolicies.inventory.saleDisabled.trinket) "Write pass should record trinket sale disable policy."
Assert-True ([string]$profilePolicy.profilePolicies.townEvent.message -eq "Enjoy the inferno") "Write pass should record the requested town event message policy."
$necroQuest = Get-QuestById -Quest $quest -Id "plot_kill_necromancer_3"
Assert-True ([string]$necroQuest.dungeon -eq "crypts") "Fixed quest should preserve content-defined dungeon."
Assert-True ([int]$necroQuest.difficulty -eq 5) "Fixed quest should preserve content-defined difficulty."
Assert-True (@($necroQuest.goal_ids)[0] -eq "kill_necromancer_C") "Fixed quest should preserve content-defined goal id."
Assert-True (Test-QuestRewardItem -QuestEntry $necroQuest -Type "trinket" -Id "boss_necromancer" -Amount 1) "Fixed quest should preserve concrete boss trinket reward."
Assert-True ($null -eq $necroQuest.completion_reward.items_definition.system_config_type) "Decoded quest writer should remove static-only system_config_type from save items_definition."
Write-CompletedQuestStateFixture -QuestIds @("plot_kill_necromancer_3")
Write-DecodedQuestFixture
Invoke-Loader -LoaderArgs ($baseArgs + @("--apply-managed-actions", "--write-managed-actions", "--managed-action-save-dir", $saveRoot))
$quest = Read-DecodedQuest
$questIds = @(Get-QuestIds -Quest $quest)
Assert-True ($questIds.Count -eq 7) "Completed fixed quests should be filtered out when removeCompleted is enabled."
Assert-True ($questIds[0] -eq "plot_kill_prophet_3") "Quest board should keep only uncompleted fixed quests after sidecar completion state changes."
Invoke-DDSaveEditorEncodeProbe -FileName "persist.roster.json"
Invoke-DDSaveEditorEncodeProbe -FileName "persist.upgrades.json"
Invoke-DDSaveEditorEncodeProbe -FileName "persist.town.json"
Invoke-DDSaveEditorEncodeProbe -FileName "persist.town_event.json"
Invoke-DDSaveEditorEncodeProbe -FileName "persist.quest.json"

$prepareSourceProfile = Join-Path $stateRoot "prepare_source_profile"
$prepareOutputRoot = Join-Path $stateRoot "prepared_workspaces"
$prepareSessionId = "encoded_profile_roundtrip"
New-Item -ItemType Directory -Force -Path $prepareSourceProfile | Out-Null
$persistDecodedFiles = @(Get-ChildItem -LiteralPath $saveRoot -Filter "persist*.json" -File | Sort-Object Name)
Assert-True ($persistDecodedFiles.Count -ge 5) "Roundtrip source profile should have the required persist files."
foreach ($persistFile in $persistDecodedFiles) {
    Invoke-DDSaveEditorEncodeFile `
        -DecodedPath $persistFile.FullName `
        -OutputPath (Join-Path $prepareSourceProfile $persistFile.Name)
}

& (Join-Path $projectRoot.Path "tools\PrepareDecodedProfileWorkspace.ps1") `
    -SourceProfileDirectory $prepareSourceProfile `
    -OutputRoot $prepareOutputRoot `
    -SessionId $prepareSessionId `
    -SaveEditorJar $saveEditorJar `
    -ConfigPath (Resolve-ProjectPath $ConfigPath) `
    -ModStateId $pluginId `
    -Initialize `
    -WriteManagedActions `
    -EncodeInitializedProfile `
    -NoBuild
if ($LASTEXITCODE -ne 0) {
    throw "PrepareDecodedProfileWorkspace failed with exit code $LASTEXITCODE"
}

$workspaceReportPath = Join-Path $prepareOutputRoot (Join-Path $prepareSessionId "decoded_profile_workspace_report.json")
Assert-True (Test-Path -LiteralPath $workspaceReportPath -PathType Leaf) "Decoded profile workspace report was not written: $workspaceReportPath"
$workspaceReport = Get-Content -Raw -LiteralPath $workspaceReportPath | ConvertFrom-Json
Assert-True ([bool]$workspaceReport.initializeRequested) "Workspace roundtrip should request initialization."
Assert-True ([bool]$workspaceReport.writeManagedActions) "Workspace roundtrip should write managed actions into the decoded sandbox."
Assert-True ([bool]$workspaceReport.encodeInitializedProfileRequested) "Workspace roundtrip should request initialized profile encoding."
Assert-True ([bool]$workspaceReport.initialization.succeeded) "Workspace initialization should succeed before encoding."
Assert-True ([string]$workspaceReport.encoding.status -eq "completed") "Workspace encoding should complete."
Assert-True ([int]$workspaceReport.encoding.encodedFileCount -eq $persistDecodedFiles.Count) "Workspace encoding should encode every decoded persist file."
Assert-True ([int]$workspaceReport.encoding.failedFileCount -eq 0) "Workspace encoding should not fail any persist file."
Assert-True ([int]$workspaceReport.encoding.roundTripValidatedFileCount -eq $persistDecodedFiles.Count) "Workspace encoding should roundtrip-validate every encoded persist file."
Assert-True (Test-Path -LiteralPath (Join-Path ([string]$workspaceReport.encoding.encodedProfileDirectory) "persist.roster.json") -PathType Leaf) "Encoded sandbox profile should include persist.roster.json."
Assert-True (Test-Path -LiteralPath (Join-Path ([string]$workspaceReport.encoding.roundTripDecodedDirectory) "persist.roster.json") -PathType Leaf) "Roundtrip decoded sandbox should include persist.roster.json."

$roundTripRoster = Get-Content -Raw -LiteralPath (Join-Path ([string]$workspaceReport.encoding.roundTripDecodedDirectory) "persist.roster.json") | ConvertFrom-Json
Assert-True ((Get-HeroClassCount -Roster $roundTripRoster -ClassId "crusader") -eq 2) "Roundtrip decoded roster should preserve initialized crusader count."
Assert-True ((Get-HeroClassCount -Roster $roundTripRoster -ClassId "arbalest") -eq 2) "Roundtrip decoded roster should preserve initialized arbalest count."
$roundTripCrusader = Get-FirstHeroRootByClass -Roster $roundTripRoster -ClassId "crusader"
Assert-True ([int]$roundTripCrusader.resolveXp -eq 46) "Roundtrip decoded roster should preserve initialized resolve XP."

$roundTripQuest = Get-Content -Raw -LiteralPath (Join-Path ([string]$workspaceReport.encoding.roundTripDecodedDirectory) "persist.quest.json") | ConvertFrom-Json
$roundTripQuestIds = @(Get-QuestIds -Quest $roundTripQuest)
Assert-True ($roundTripQuestIds.Count -eq 8) "Roundtrip decoded quest board should preserve initialized fixed quest count."
Assert-True ($roundTripQuestIds[0] -eq "plot_kill_necromancer_3") "Roundtrip decoded quest board should preserve initialized fixed quest order."

Write-Host "PASS: managed action save applier dry-run and decoded wallet/trinket/roster/skill/upgrade/town/town-event/quest/policy write assertions passed."
