param(
    [string]$SampleProfile = ".research\profile_0",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\realtime_save_bridge_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\realtime_save_bridge_test\$sessionId"
$remoteRoot = Join-Path $stateRoot "remote"
$profileRoot = Join-Path $remoteRoot "profile_0"
$auxiliaryProfileRoot = Join-Path $remoteRoot "profile_9"
$configPath = Join-Path $projectRoot.Path "config\_realtime_save_bridge_test_$sessionId.json"
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

    return (Join-Path $projectRoot.Path $Path)
}

Push-Location $projectRoot.Path
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
        pluginDirectories = @("./plugins/_validation")
        pluginPatchManifestName = "patches.json"
        virtualFileEnabled = $true
        virtualFileTarget = ""
        virtualFileFind = ""
        virtualFileReplace = ""
        virtualFileRules = @()
    }
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8

    $startedAt = Get-Date
    $arguments = @(
        "run",
        "--project", "launcher/DDRuntimeLoader.csproj",
        "-c", "Release",
        "--no-build",
        "--",
        "--config", $configPath,
        "--watch-saves-for-ms", "5000",
        "--no-inject"
    )
    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList $arguments `
        -WorkingDirectory $projectRoot.Path `
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

    New-Item -ItemType Directory -Force -Path $auxiliaryProfileRoot | Out-Null
    $auxiliaryStartedAt = Get-Date
    $auxiliaryFile = Join-Path $auxiliaryProfileRoot "persist.circus_estate.json"
    Set-Content -LiteralPath $auxiliaryFile -Value "{}" -Encoding UTF8
    Start-Sleep -Milliseconds 900

    $saveStateRoot = Join-Path $projectRoot.Path "logs\save_states"
    $auxiliaryRealtimeReports = @(Get-ChildItem -LiteralPath $saveStateRoot -Filter "*_realtime_*.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $auxiliaryStartedAt.AddMilliseconds(-100) } |
        Sort-Object LastWriteTime)
    Assert-True ($auxiliaryRealtimeReports.Count -eq 0) "Auxiliary-only save changes should not write realtime save state reports."

    $changedFile = Join-Path $profileRoot "persist.game.json"
    Assert-True (Test-Path -LiteralPath $changedFile -PathType Leaf) "Copied sample profile is missing persist.game.json."
    (Get-Item -LiteralPath $changedFile).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(5)

    if (-not $process.WaitForExit(15000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Watch-save diagnostic did not exit before timeout."
    }

    if ($process.ExitCode -ne 0) {
        $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -Raw -LiteralPath $stdoutPath } else { "" }
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { "" }
        throw "Watch-save diagnostic failed with exit code $($process.ExitCode). STDOUT: $stdout STDERR: $stderr"
    }

    $realtimeReports = @(Get-ChildItem -LiteralPath $saveStateRoot -Filter "*_realtime_*.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-1) } |
        Sort-Object LastWriteTime)
    Assert-True ($realtimeReports.Count -ge 1) "Realtime save bridge did not write a realtime save state report."

    $latestRealtimeReport = Get-Content -Raw -LiteralPath $realtimeReports[-1].FullName | ConvertFrom-Json
    Assert-True ($latestRealtimeReport.activeProfile.profile -eq "profile_0") "Realtime save state report should target copied profile_0."
    Assert-True ($latestRealtimeReport.sessionId -like "*_realtime_*") "Realtime save state report should use a realtime session id."

    $sessionRoot = Join-Path $projectRoot.Path "logs\save_sessions"
    $sessionReports = @(Get-ChildItem -LiteralPath $sessionRoot -Filter "*.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-1) } |
        Sort-Object LastWriteTime)
    Assert-True ($sessionReports.Count -ge 1) "Watch-save diagnostic did not write a session report."

    $sessionReport = Get-Content -Raw -LiteralPath $sessionReports[-1].FullName | ConvertFrom-Json
    $eventCounts = $sessionReport.eventCounts
    Assert-True ([int]$eventCounts.'save.event_bridge_realtime_ignored_auxiliary' -ge 1) "Realtime watcher should record ignored auxiliary-only save changes."
    Assert-True ([int]$eventCounts.'save.state_report_realtime_written' -ge 1) "Realtime watcher should record a realtime save state report write."
    Assert-True ([int]$eventCounts.'save.event_bridge_realtime_completed' -ge 1) "Realtime watcher should record a completed save event bridge pass."

    Write-Host "PASS: realtime save watcher ignored auxiliary-only changes, then generated a campaign save state report and executed SaveEventBridge."
}
finally {
    if (Test-Path -LiteralPath $configPath) {
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    }

    Pop-Location
}
