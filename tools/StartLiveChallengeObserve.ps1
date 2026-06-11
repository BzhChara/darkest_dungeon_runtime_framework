param(
    [string]$ConfigPath = "config\challenge_save_event_bridge_observe_config.json",
    [string]$StateRoot = "",
    [switch]$NoBuild,
    [switch]$SkipPrepare,
    [switch]$PrepareOnly,
    [switch]$DryRun,
    [switch]$AllowExistingGameProcess
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
    $StateRoot = Join-Path $projectRoot "state\live_challenge_observe\$sessionId"
}

Push-Location $projectRoot
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    if (-not $PrepareOnly -and -not $DryRun -and -not $AllowExistingGameProcess) {
        $existingGame = Get-Process -Name Darkest -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq "E:\Steam\steamapps\common\DarkestDungeon\_windows\win64\Darkest.exe" } |
            Select-Object -First 1
        Assert-DdrtTrue ($null -eq $existingGame) "Darkest.exe is already running. Exit the game first, or pass -AllowExistingGameProcess if this is intentional."
    }

    $configFullPath = Get-DdrtResolvedPath `
        -Path $ConfigPath `
        -Leaf `
        -MissingMessage "Config file was not found: $ConfigPath"

    $baseArgs = @(
        "--config", $configFullPath,
        "--allow-non-atomic-state-writes",
        "--mod-state-dir", $StateRoot
    )

    if (-not $SkipPrepare) {
        Invoke-DdrtLoader -LoaderArgs ($baseArgs + @(
            "--no-inject",
            "--mod-state-id", "validation.challenge_run_contract",
            "--init-mod-state"
        ))

        Invoke-DdrtLoader -LoaderArgs ($baseArgs + @(
            "--no-inject",
            "--mod-state-id", "validation.challenge_run_contract",
            "--emit-event", "challenge.run_started"
        ))

        Invoke-DdrtLoader -LoaderArgs ($baseArgs + @(
            "--no-inject",
            "--mod-state-id", "validation.challenge_run_contract",
            "--emit-event", "challenge.stage_selection_started"
        ))
    }

    $challengeState = Read-DdrtChallengeState -Root $StateRoot
    $currentStage = Get-DdrtObjectProperty $challengeState "currentStage"
    Write-Host "Live challenge observe state: $StateRoot"
    if ($null -ne $currentStage) {
        Write-Host "Current stage: $((Get-DdrtObjectProperty $currentStage "id")) sourceQuestId=$((Get-DdrtObjectProperty $currentStage "sourceQuestId"))"
    }

    if ($PrepareOnly) {
        Write-Host "PrepareOnly requested. Game was not started."
        return
    }

    $launchArgs = $baseArgs
    if ($DryRun) {
        $launchArgs += "--dry-run"
    }

    Invoke-DdrtLoader -LoaderArgs $launchArgs
}
finally {
    Pop-Location
}
