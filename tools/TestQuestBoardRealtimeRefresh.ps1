param(
    [string]$SampleProfile = ".research\profile_0",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$pluginId = "validation.boss_gauntlet_campaign_contract"
$projectRoot = Get-DdrtProjectRoot
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot "logs\quest_board_realtime_refresh_test\$sessionId"
$stateRoot = Join-Path $projectRoot "state\quest_board_realtime_refresh_test\$sessionId"
$remoteRoot = Join-Path $stateRoot "remote"
$profileId = "profile_3"
$profileRoot = Join-Path $remoteRoot $profileId
$sourceQuestPath = Join-Path $profileRoot "persist.quest.json"
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
        pluginDirectories = @("./plugins/_validation")
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

    $startedAt = Get-Date
    $arguments = @(
        "run",
        "--project", "launcher/DDRuntimeLoader.csproj",
        "-c", "Release",
        "--no-build",
        "--",
        "--config", $configPath,
        "--watch-saves-for-ms", "8000",
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

    Write-OriginalQuestFixture
    (Get-Item -LiteralPath $sourceQuestPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(5)

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
    Assert-True ($questIds.Count -eq 8) "Realtime quest board refresh should write eight fixed boss quests."
    Assert-True ($questIds[0] -eq "plot_kill_necromancer_3") "Realtime quest board refresh should keep the first fixed quest."
    Assert-True ($questIds[1] -eq "plot_kill_prophet_3") "Realtime quest board refresh should keep the second fixed quest."

    $backupFiles = @(Get-ChildItem -LiteralPath (Join-Path $stateRoot "_live_save_backups\quest_board_refresh") -Filter "persist.quest.json" -Recurse -ErrorAction SilentlyContinue)
    Assert-True ($backupFiles.Count -ge 1) "Realtime quest board refresh should create a backup before writing."

    $sessionRoot = Join-Path $projectRoot "logs\save_sessions"
    $sessionReports = @(Get-ChildItem -LiteralPath $sessionRoot -Filter "*.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-1) } |
        Sort-Object LastWriteTime)
    Assert-True ($sessionReports.Count -ge 1) "Watch-save diagnostic did not write a session report."

    $sessionReport = Get-Content -Raw -LiteralPath $sessionReports[-1].FullName | ConvertFrom-Json
    $eventCounts = $sessionReport.eventCounts
    Assert-True ([int]$eventCounts.'save.quest_board_auto_refresh_requested' -ge 1) "Realtime watcher should request quest board auto refresh."
    Assert-True ([int]$eventCounts.'save.quest_board_auto_refresh_completed' -ge 1) "Realtime watcher should complete quest board auto refresh."

    Write-Host "PASS: realtime quest-board save change auto-refreshed the fixed boss board."
}
finally {
    if (Test-Path -LiteralPath $configPath) {
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    }

    Pop-Location
}
