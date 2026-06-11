param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot.Path "state\managed_action_overlay_test\$sessionId"

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
        "--mod-state-id", "validation.challenge_run_contract",
        "--mod-state-dir", $stateRoot
    )

    Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.run_started"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_selection_started"))
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_selection_started"))

    $artifactRoot = Join-Path $stateRoot "_managed_actions"
    $artifacts = @(Get-ChildItem -LiteralPath $artifactRoot -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
    Assert-True ($artifacts.Count -eq 6) "Expected six materialized managed action artifacts after two selection-start events, found $($artifacts.Count)."

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

    Assert-True ([int]$manifest.artifactCount -eq 6) "Overlay manifest should count all six artifacts."
    Assert-True ([int]$manifest.overlayCount -eq 1) "First overlay compiler slice should expose only the latest quest.injectFixedStage overlay."
    Assert-True ([int]$manifest.ignoredArtifactCount -eq 4) "Overlay manifest should ignore hero/trinket filter artifacts for now."
    Assert-True ([int]$manifest.supersededOverlayCount -eq 1) "Overlay manifest should supersede the older quest injection artifact."
    Assert-True ([int]$manifest.virtualFileRuleCount -eq 1) "Overlay manifest should compile one quest plot virtual file rule."
    Assert-True ([int]$manifest.virtualFileReplacementCount -eq 1) "Overlay manifest should compile one quest plot replacement."
    Assert-True ((@($manifest.issues)).Count -eq 0) "Overlay manifest should not contain issues."

    $overlays = @($manifest.overlays)
    Assert-True ($overlays.Count -eq 1) "Expected exactly one overlay entry."
    $overlay = $overlays[0]
    Assert-True ($overlay.kind -eq "quest.injectFixedStage") "Overlay kind should be quest.injectFixedStage."
    Assert-True ($overlay.stageId -eq "stage_1_necromancer") "Overlay should target the first challenge stage."
    Assert-True ($overlay.sourceQuestId -eq "plot_kill_necromancer_1") "Overlay should carry the source quest id."
    Assert-True (Test-Path -LiteralPath ([string]$overlay.artifactPath) -PathType Leaf) "Overlay artifact path should point to an existing artifact."

    $virtualRules = @($manifest.virtualFileRules)
    Assert-True ($virtualRules.Count -eq 1) "Expected exactly one overlay virtual file rule."
    $virtualRule = $virtualRules[0]
    Assert-True ($virtualRule.target -eq "campaign/quest/quest.plot_quests.json") "Overlay virtual rule should target the base plot quest file."
    Assert-True ($virtualRule.effect -eq "forcePlotQuestAvailable") "Overlay virtual rule should force the selected plot quest available."
    $virtualReplacements = @($virtualRule.replacements)
    Assert-True ($virtualReplacements.Count -eq 1) "Expected exactly one overlay virtual replacement."
    $virtualReplacement = $virtualReplacements[0]
    Assert-True ($virtualReplacement.sourceQuestId -eq "plot_kill_necromancer_1") "Overlay virtual replacement should use the current stage source quest."
    Assert-True ($virtualReplacement.stageId -eq "stage_1_necromancer") "Overlay virtual replacement should carry the current stage id."
    Assert-True ([int]$virtualReplacement.setDungeonLevel -eq 0) "Overlay virtual replacement should force dungeon_level to 0."
    Assert-True ([bool]$virtualReplacement.setRepeatable) "Overlay virtual replacement should force the quest to repeatable."
    Assert-True ([int]$virtualReplacement.findChars -gt 0) "Overlay virtual replacement should contain non-empty find text."
    Assert-True ([int]$virtualReplacement.replaceChars -gt 0) "Overlay virtual replacement should contain non-empty replacement text."

    Write-Host "PASS: managed action artifacts compiled into an overlay manifest."
}
finally {
    Pop-Location
}
