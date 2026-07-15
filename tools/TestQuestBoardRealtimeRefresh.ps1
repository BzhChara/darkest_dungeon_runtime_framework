param(
    [string]$SampleProfile = ".research\profile_0",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$pluginId = "validation.boss_gauntlet_campaign_contract"
$projectRoot = Get-DdrtProjectRoot
$pluginManifestPath = (Resolve-Path -LiteralPath (Join-Path $projectRoot "plugins\_validation\boss_gauntlet_campaign_contract\patches.json")).Path
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot "logs\quest_board_realtime_refresh_test\$sessionId"
$stateRoot = Join-Path $projectRoot "state\quest_board_realtime_refresh_test\$sessionId"
$remoteRoot = Join-Path $stateRoot "remote"
$profileId = "profile_3"
$profileRoot = Join-Path $remoteRoot $profileId
$sourceQuestPath = Join-Path $profileRoot "persist.quest.json"
$sourceTownPath = Join-Path $profileRoot "persist.town.json"
$configPath = Join-Path $stateRoot "config.json"
$stdoutPath = Join-Path $testRoot "watch_stdout.txt"
$stderrPath = Join-Path $testRoot "watch_stderr.txt"

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

    return (Join-Path $projectRoot $Path)
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Write-OriginalQuestFixture {
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
'@ | Set-Content -LiteralPath $sourceQuestPath -Encoding UTF8
}

function Write-TownFixture {
    @'
{
  "base_root": {
    "buildings": {
      "stage_coach": {
        "store": {
          "0": {
            "generated": {
              "0": {
                "hero_id": 101
              }
            }
          }
        }
      },
      "nomad_wagon": {
        "store": {
          "0": {
            "generated": {
              "0": {
                "id": "stale_trinket"
              }
            },
            "inventory": {
              "items": {
                "0": {
                  "id": "stale_trinket",
                  "type": "trinket",
                  "amount": 1
                }
              }
            }
          }
        }
      },
      "blacksmith": {
        "store": {
          "0": {
            "generated": {
              "0": {
                "id": "foreign_profile_should_not_clear"
              }
            }
          }
        }
      }
    },
    "districts": {
      "buildings": {}
    }
  }
}
'@ | Set-Content -LiteralPath $sourceTownPath -Encoding UTF8
}

function Write-TownEventFixture {
    @'
{
  "base_root": {
    "current_result_event_id": 123,
    "has_unclaimed_interaction": true,
    "event_cost": {
      "gold": 1000
    },
    "bonus_hero_entries": {
      "0": {
        "hero_id": 101
      }
    },
    "dead_hero_entries": [
      201
    ],
    "free_upgrade_tags": {
      "0": "stale"
    }
  }
}
'@ | Set-Content -LiteralPath (Join-Path $profileRoot "persist.town_event.json") -Encoding UTF8
}

function Write-TestConfig {
    $config = [ordered]@{
        gameExecutablePath = "E:/Steam/steamapps/common/DarkestDungeon/_windows/win64/Darkest.exe"
        gameWorkingDirectory = "E:/Steam/steamapps/common/DarkestDungeon"
        runtimeDllPath = "./runtime/bin/x64/Release/RuntimeHook.dll"
        logDirectory = "./logs"
        modStateDirectory = $stateRoot
        allowNonAtomicStateWrites = $true
        enableInjection = $false
        killGameOnInjectionFailure = $false
        startSuspendedForInjection = $false
        fileIoObserveOnly = $true
        fileIoLogExtensions = @(".darkest", ".json", ".txt")
        fileIoMaxLogEntries = 100
        fileIoDeduplicate = $true
        eventProbeEnabled = $false
        eventProbeLogFileOpen = $false
        eventProbeLogFileWrite = $false
        eventProbeLogSaveFiles = $false
        eventProbeLogDataFiles = $false
        eventProbeLogAssetFiles = $false
        eventProbeMaxLogEntries = 100
        eventProbeMaxSaveLogEntries = 100
        eventProbeIgnorePathFragments = @("Steam/logs/", "gameoverlay_renderer.txt")
        saveWatchEnabled = $true
        saveWatchDirectories = @($remoteRoot)
        saveWatchAfterExitSeconds = 0
        saveEventBridgeEnabled = $true
        saveEventBridgeDebounceMilliseconds = 200
        questBoardAutoRefreshEnabled = $true
        questBoardAutoRefreshAllowRunningGameSaveWrite = $false
        continuousProfileActionAutoApplyEnabled = $true
        continuousProfileActionAutoApplyAllowRunningGameSaveWrite = $false
        questBoardPolicyAutoMaterializeEnabled = $true
        pluginDirectories = @("./plugins/_validation/boss_gauntlet_campaign_contract")
        pluginPatchManifestName = "patches.json"
        virtualFileEnabled = $true
        virtualFileTarget = ""
        virtualFileFind = ""
        virtualFileReplace = ""
        virtualFileRules = @()
    }
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8
}

function Read-QuestIds {
    $quest = Get-Content -Raw -LiteralPath $sourceQuestPath | ConvertFrom-Json
    return @($quest.base_root.quests.PSObject.Properties |
        Sort-Object { [int]$_.Name } |
        ForEach-Object { [string]$_.Value.id })
}

function Set-CompletedBossState {
    $statePath = Join-Path $stateRoot "$pluginId.json"
    Assert-True (Test-Path -LiteralPath $statePath -PathType Leaf) "Boss gauntlet state file was not created: $statePath"
    $stateDocument = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    $stateDocument.state.bossGauntlet.phase = "boss_gauntlet"
    $stateDocument.state.bossGauntlet.completedQuestIds = [object[]]@("plot_kill_necromancer_3")
    $stateDocument | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Write-StaleDd4PolicyArtifact {
    $artifactRoot = Join-Path $stateRoot "_managed_actions"
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $resolveReportPath = Join-Path $projectRoot "logs\quest_board_policy_resolve_report.json"
    $staleProfileRoot = Join-Path $profileRoot "stale_root_that_should_not_split_supersession"
    $artifact = [ordered]@{
        version = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        status = "materialized"
        eventId = "quest.board.policies.materialized"
        pluginId = "framework.quest_board_policy_materializer"
        sourceName = "Quest Board Policy Materializer"
        sourcePath = $resolveReportPath
        owners = @(
            [ordered]@{
                pluginId = $pluginId
                sourcePath = $pluginManifestPath
            }
        )
        profileScope = [ordered]@{
            kind = "profile"
            profileId = $profileId
            profileRoot = $staleProfileRoot
            source = "test.staleArtifact"
        }
        loadOrder = 2147483647
        ruleIndex = 0
        ruleId = "questBoardPolicies.materialized"
        actionIndex = 0
        action = [ordered]@{
            type = "questBoard.replaceWithFixedSet"
            capability = "quest_board.replace_with_fixed_set"
            risk = "managed"
            required = $false
        }
        payload = [ordered]@{
            source = "questBoardPolicies"
            selectedQuestCount = 1
        }
        issues = @()
        plan = [ordered]@{
            kind = "questBoard.replaceWithFixedSet"
            effect = "replaceWithFixedSet"
            target = "profile.quest_board"
            source = "questBoardPolicies"
            profileScope = [ordered]@{
                kind = "profile"
                profileId = $profileId
                profileRoot = $staleProfileRoot
                source = "test.staleArtifact"
            }
            arguments = [ordered]@{
                target = "profile.quest_board"
                questIds = @("plot_darkest_dungeon_4")
                removeCompleted = $false
                source = "questBoardPolicies"
                selectionMode = "policyModeAwareWeightedPools"
                seed = 0
                slotLimit = $null
                policies = @(
                    [ordered]@{
                        pluginId = $pluginId
                        sourcePath = $pluginManifestPath
                        policyId = "boss_gauntlet_darkest_finale_chain.linear_progression"
                        mode = "linearProgression"
                        status = "selected"
                        selectedQuestIds = @("plot_darkest_dungeon_4")
                    }
                )
            }
        }
    }

    $artifactPath = Join-Path $artifactRoot "manual_stale_dd4_questBoard.replaceWithFixedSet.json"
    $artifact | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
}

function Write-ForeignProfileContinuousArtifact {
    $artifactRoot = Join-Path $stateRoot "_managed_actions"
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $artifact = [ordered]@{
        version = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        status = "materialized"
        eventId = "test.foreign_profile"
        pluginId = "validation.foreign_profile_probe"
        sourceName = "Foreign Profile Probe"
        sourcePath = "tools/TestQuestBoardRealtimeRefresh.ps1"
        profileScope = [ordered]@{
            kind = "profile"
            profileId = "profile_0"
            profileRoot = (Join-Path $remoteRoot "profile_0")
            source = "test.foreignProfile"
        }
        loadOrder = 0
        ruleIndex = 0
        ruleId = "foreign_profile_store_probe"
        actionIndex = 0
        action = [ordered]@{
            type = "town.suppressStoreItems"
            capability = "town.suppress_store_items"
            risk = "managed"
            required = $true
        }
        plan = [ordered]@{
            kind = "town.suppressStoreItems"
            effect = "suppressStoreItems"
            target = "profile.town.stores"
            arguments = [ordered]@{
                mode = "empty"
                buildingIds = @("blacksmith")
                sections = @("generated")
            }
        }
    }

    $artifactPath = Join-Path $artifactRoot "manual_foreign_profile_town.suppressStoreItems.json"
    $artifact | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
}

Push-Location $projectRoot
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    $sampleProfilePath = Resolve-ProjectPath $SampleProfile
    Assert-True (Test-Path -LiteralPath $sampleProfilePath -PathType Container) "Sample profile directory was not found: $sampleProfilePath"

    New-Item -ItemType Directory -Force -Path $testRoot, $remoteRoot | Out-Null
    Copy-Item -LiteralPath $sampleProfilePath -Destination $profileRoot -Recurse -Force
    Write-OriginalQuestFixture
    Write-TownFixture
    Write-TownEventFixture
    Write-TestConfig

    $baseArgs = @(
        "--config", $configPath,
        "--allow-non-atomic-state-writes",
        "--mod-state-id", $pluginId,
        "--mod-state-dir", $stateRoot,
        "--no-inject"
    )

    Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "profile.initialization_requested"))
    Set-CompletedBossState
    Write-StaleDd4PolicyArtifact
    Write-ForeignProfileContinuousArtifact

    $startedAt = Get-Date
    $arguments = @(
        "run",
        "--project", "launcher/DDRuntimeLoader.csproj",
        "-c", "Release",
        "--no-build",
        "--",
        "--config", $configPath,
        "--watch-saves-for-ms", "12000",
        "--no-inject"
    )
    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList $arguments `
        -WorkingDirectory $projectRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru `
        -WindowStyle Hidden

    $ready = $false
    for ($i = 0; $i -lt 80; $i++) {
        if ((Test-Path -LiteralPath $stdoutPath) -and
            ((Get-Content -Raw -LiteralPath $stdoutPath) -match "Watch-save diagnostic running")) {
            $ready = $true
            break
        }

        Start-Sleep -Milliseconds 100
    }
    Assert-True $ready "Watch-save diagnostic did not report readiness before timeout."

    Assert-True (Test-Path -LiteralPath $sourceTownPath -PathType Leaf) "Sample profile should contain persist.town.json for non-quest save refresh trigger."
    (Get-Item -LiteralPath $sourceTownPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(5)

    if (-not $process.WaitForExit(20000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Watch-save diagnostic did not exit before timeout."
    }

    if ($process.ExitCode -ne 0) {
        $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -Raw -LiteralPath $stdoutPath } else { "" }
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { "" }
        throw "Watch-save diagnostic failed with exit code $($process.ExitCode). STDOUT: $stdout STDERR: $stderr"
    }

    $questIds = @(Read-QuestIds)
    Assert-True ($questIds.Count -eq 7) "Realtime quest board refresh should write seven remaining fixed boss quests after one completed boss."
    Assert-True (-not ($questIds -contains "plot_kill_necromancer_3")) "Realtime quest board refresh should remove the completed necromancer quest."
    Assert-True ($questIds[0] -eq "plot_kill_prophet_3") "Realtime quest board refresh should keep the next uncompleted fixed quest first."
    Assert-True (-not ($questIds -contains "plot_darkest_dungeon_4")) "Stale DD4 quest-board policy artifact must not override the current pre-finale fixed board."

    $town = Get-Content -Raw -LiteralPath $sourceTownPath | ConvertFrom-Json
    $stageCoachGenerated = @($town.base_root.buildings.stage_coach.store.'0'.generated.PSObject.Properties).Count
    $nomadGenerated = @($town.base_root.buildings.nomad_wagon.store.'0'.generated.PSObject.Properties).Count
    $nomadInventory = @($town.base_root.buildings.nomad_wagon.store.'0'.inventory.items.PSObject.Properties).Count
    $blacksmithGenerated = @($town.base_root.buildings.blacksmith.store.'0'.generated.PSObject.Properties).Count
    Assert-True ($stageCoachGenerated -eq 0) "Continuous profile apply should clear regenerated stagecoach recruits."
    Assert-True ($nomadGenerated -eq 0) "Continuous profile apply should clear generated nomad wagon stock."
    Assert-True ($nomadInventory -eq 0) "Continuous profile apply should clear nomad wagon inventory items."
    Assert-True ($blacksmithGenerated -eq 1) "Continuous profile apply must ignore artifacts scoped to another profile."

    $backupFiles = @(Get-ChildItem -LiteralPath (Join-Path $stateRoot "_live_save_backups\quest_board_refresh") -Filter "persist.quest.json" -Recurse -ErrorAction SilentlyContinue)
    Assert-True ($backupFiles.Count -ge 1) "Realtime quest board refresh should create a backup before writing."
    $continuousBackupFiles = @(Get-ChildItem -LiteralPath (Join-Path $stateRoot "_live_save_backups\continuous_profile_apply") -Filter "persist.town.json" -Recurse -ErrorAction SilentlyContinue)
    Assert-True ($continuousBackupFiles.Count -ge 1) "Continuous profile auto apply should create a backup before writing town state."

    $sessionRoot = Join-Path $projectRoot "logs\save_sessions"
    $sessionReports = @(Get-ChildItem -LiteralPath $sessionRoot -Filter "*.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-1) } |
        Sort-Object LastWriteTime)
    Assert-True ($sessionReports.Count -ge 1) "Watch-save diagnostic did not write a session report."

    $sessionReport = Get-Content -Raw -LiteralPath $sessionReports[-1].FullName | ConvertFrom-Json
    $eventCounts = $sessionReport.eventCounts
    Assert-True ([int]$eventCounts.'save.quest_board_auto_refresh_requested' -ge 1) "Realtime watcher should request quest board auto refresh."
    Assert-True ([int]$eventCounts.'save.quest_board_auto_refresh_completed' -ge 1) "Realtime watcher should complete quest board auto refresh."
    Assert-True ([int]$eventCounts.'save.continuous_profile_auto_apply_requested' -ge 1) "Realtime watcher should request continuous profile action auto apply."
    Assert-True ([int]$eventCounts.'save.continuous_profile_auto_apply_completed' -ge 1) "Realtime watcher should complete continuous profile action auto apply."

    Write-Host "PASS: realtime campaign save change auto-refreshed the fixed boss board and continuous profile actions."
}
finally {
    if (Test-Path -LiteralPath $configPath) {
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    }

    Pop-Location
}
