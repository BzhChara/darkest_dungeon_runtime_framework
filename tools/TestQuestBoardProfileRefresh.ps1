param(
    [string]$ConfigPath = "config\quest_board_profile_refresh_config.json",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$pluginId = "validation.boss_gauntlet_campaign_contract"
$projectRoot = Get-DdrtProjectRoot
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot "state\quest_board_profile_refresh_test\$sessionId"
$remoteRoot = Join-Path $stateRoot "remote"
$profileId = "profile_3"
$profileRoot = Join-Path $remoteRoot $profileId
$sourceQuestPath = Join-Path $profileRoot "persist.quest.json"
$configOutputPath = Join-Path $stateRoot "config.json"

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

function Write-QuestFixture {
    New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
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

function Write-TestConfig {
    param([string]$SourceConfigPath)

    $config = Get-Content -Raw -LiteralPath $SourceConfigPath | ConvertFrom-Json
    $config.saveWatchDirectories = @($remoteRoot)
    $config.modStateDirectory = $stateRoot
    $config.enableInjection = $false
    $config.saveWatchEnabled = $false
    $config.saveEventBridgeEnabled = $false
    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $configOutputPath -Encoding UTF8
}

function Read-QuestIds {
    $quest = Get-Content -Raw -LiteralPath $sourceQuestPath | ConvertFrom-Json
    return @($quest.base_root.quests.PSObject.Properties |
        Sort-Object { [int]$_.Name } |
        ForEach-Object { [string]$_.Value.id })
}

function Read-RefreshReport {
    $path = Join-Path $projectRoot "logs\quest_board_profile_refresh_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Quest board profile refresh report was not written: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

Push-Location $projectRoot
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    Write-QuestFixture
    Write-TestConfig -SourceConfigPath (Resolve-ProjectPath $ConfigPath)

    $baseArgs = @(
        "--config", $configOutputPath,
        "--allow-non-atomic-state-writes",
        "--mod-state-id", $pluginId,
        "--mod-state-dir", $stateRoot
    )

    Invoke-Loader -LoaderArgs ($baseArgs + @("--no-inject", "--init-mod-state"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--no-inject", "--emit-event", "profile.initialization_requested"))

    $beforeText = Get-Content -Raw -LiteralPath $sourceQuestPath
    Invoke-Loader -LoaderArgs ($baseArgs + @("--dry-run", "--refresh-quest-board-profile", $profileId))
    $dryRunReport = Read-RefreshReport
    Assert-True ([bool]$dryRunReport.dryRun) "Dry-run refresh should record dryRun=true."
    Assert-True (-not [bool]$dryRunReport.written) "Dry-run refresh must not write the profile save."
    Assert-True ([string]$dryRunReport.status -eq "dry-run-would-write") "Dry-run refresh should report dry-run-would-write."
    Assert-True ((Get-Content -Raw -LiteralPath $sourceQuestPath) -eq $beforeText) "Dry-run refresh modified persist.quest.json."

    Invoke-Loader -LoaderArgs ($baseArgs + @("--refresh-quest-board-profile", $profileId))
    $writeReport = Read-RefreshReport
    Assert-True (-not [bool]$writeReport.dryRun) "Write refresh should record dryRun=false."
    Assert-True ([bool]$writeReport.written) "Write refresh should write the profile save."
    Assert-True ([string]$writeReport.status -eq "written") "Write refresh should report written."
    Assert-True ([string]$writeReport.writeMode -eq "direct-overwrite-after-backup") "Write refresh should report the explicit non-atomic save write mode."
    Assert-True (Test-Path -LiteralPath ([string]$writeReport.backupPath) -PathType Leaf) "Write refresh should create a backup."

    $questIds = @(Read-QuestIds)
    Assert-True ($questIds.Count -eq 8) "Quest board refresh should write eight fixed boss quests."
    Assert-True ($questIds[0] -eq "plot_kill_necromancer_3") "Quest board refresh should keep the first fixed quest."
    Assert-True ($questIds[1] -eq "plot_kill_prophet_3") "Quest board refresh should keep the second fixed quest."

    Invoke-Loader -LoaderArgs ($baseArgs + @("--refresh-quest-board-profile", $profileId))
    $unchangedReport = Read-RefreshReport
    Assert-True (-not [bool]$unchangedReport.changed) "Second refresh should detect the profile is already current."
    Assert-True (-not [bool]$unchangedReport.written) "Second refresh should not rewrite an unchanged quest board."
    Assert-True ([string]$unchangedReport.status -eq "unchanged") "Second refresh should report unchanged."

    Write-Host "PASS: quest board profile refresh dry-run, backup, write, and unchanged paths passed."
}
finally {
    Pop-Location
}
