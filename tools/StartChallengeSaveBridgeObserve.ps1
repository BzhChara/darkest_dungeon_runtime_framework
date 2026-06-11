param(
    [string]$ConfigPath = "config\challenge_save_event_bridge_observe_config.json",
    [string]$StateRoot = "",
    [switch]$NoBuild,
    [switch]$SkipPrepare
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
    $StateRoot = Join-Path $projectRoot.Path "state\challenge_save_event_bridge_observe\$sessionId"
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
        "--mod-state-dir", $StateRoot
    )

    if (-not $SkipPrepare) {
        Invoke-Loader -LoaderArgs ($baseArgs + @(
            "--mod-state-id", "validation.challenge_run_contract",
            "--init-mod-state"
        ))

        Invoke-Loader -LoaderArgs ($baseArgs + @(
            "--mod-state-id", "validation.challenge_run_contract",
            "--emit-event", "challenge.run_started"
        ))
    }

    Write-Host "Challenge observe state: $StateRoot"
    Invoke-Loader -LoaderArgs $baseArgs
}
finally {
    Pop-Location
}
