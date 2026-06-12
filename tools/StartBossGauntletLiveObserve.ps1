param(
    [string]$ConfigPath = "config\challenge_save_event_bridge_observe_config.json",
    [string]$StateRoot = "",
    [switch]$NoBuild,
    [switch]$SkipPrepare,
    [switch]$PrepareOnly,
    [switch]$DryRun,
    [switch]$AllowExistingGameProcess,
    [switch]$ForceTown
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$pluginId = "validation.boss_gauntlet_campaign_contract"
$projectRoot = Get-DdrtProjectRoot
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $StateRoot = Join-Path $projectRoot "state\boss_gauntlet_live_observe\$sessionId"
}

function Read-BossGauntletState {
    param([string]$Root)

    $path = Join-Path $Root "$pluginId.json"
    Assert-DdrtTrue (Test-Path -LiteralPath $path -PathType Leaf) "Boss gauntlet sidecar state was not found: $path"
    $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    return Get-DdrtPathValue $document "state.bossGauntlet"
}

function New-ForceTownConfig {
    param(
        [string]$SourcePath,
        [string]$StateRootPath
    )

    $config = Get-Content -Raw -LiteralPath $SourcePath | ConvertFrom-Json
    $gameArguments = @(Get-DdrtObjectProperty $config "gameArguments")
    if (-not ($gameArguments -contains "-forcetown")) {
        $gameArguments += "-forcetown"
    }

    $config | Add-Member -NotePropertyName "gameArguments" -NotePropertyValue $gameArguments -Force
    $configDirectory = Join-Path $StateRootPath "launch_config"
    New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null

    $path = Join-Path $configDirectory "config.json"
    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
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

    $effectiveConfigFullPath = $configFullPath
    if ($ForceTown) {
        $effectiveConfigFullPath = New-ForceTownConfig -SourcePath $configFullPath -StateRootPath $StateRoot
    }

    $baseArgs = @(
        "--config", $effectiveConfigFullPath,
        "--allow-non-atomic-state-writes",
        "--mod-state-id", $pluginId,
        "--mod-state-dir", $StateRoot
    )

    if (-not $SkipPrepare) {
        Invoke-DdrtLoader -LoaderArgs ($baseArgs + @(
            "--no-inject",
            "--init-mod-state"
        ))

        Invoke-DdrtLoader -LoaderArgs ($baseArgs + @(
            "--no-inject",
            "--emit-event", "profile.initialization_requested"
        ))
    }

    $state = Read-BossGauntletState -Root $StateRoot
    Write-Host "Boss gauntlet live observe state: $StateRoot"
    Write-Host "Phase: $((Get-DdrtObjectProperty $state "phase")) initialized=$((Get-DdrtObjectProperty $state "initialized"))"
    Write-Host "Fixed bosses: $(@(Get-DdrtObjectProperty $state "fixedQuestIds") -join ', ')"

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
