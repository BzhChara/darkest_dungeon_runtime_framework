param(
    [string]$LiveProfileDirectory = "E:\Steam\userdata\1097809614\262060\remote\profile_3",
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon",
    [string]$ExportScriptPath = "tools\ExportSaveSampleFacts.ps1",
    [string]$ModStateDirectory = "",
    [ValidateSet("Auto", "None", "SelectionConfirmed", "StageCompleted", "StageFailed")]
    [string]$ExpectedOutcome = "Auto",
    [switch]$UseExistingModState,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"

function Get-LiveOutcome {
    param(
        [object]$Facts,
        [object]$ChallengeState
    )

    $stage = Get-DdrtObjectProperty $ChallengeState "currentStage"
    if ($null -eq $stage) {
        return "None"
    }

    $sourceQuestId = [string](Get-DdrtObjectProperty $stage "sourceQuestId")
    $partySize = [int](Get-DdrtObjectProperty $ChallengeState "partySize")
    $lockedSelection = Get-DdrtObjectProperty $ChallengeState "lockedStageSelection"

    $raidId = [string](Get-DdrtPathValue $Facts "raid.instance.id")
    $raidHeroCountValue = Get-DdrtPathValue $Facts "raid.party.heroCount"
    if (-not [string]::IsNullOrWhiteSpace($raidId) -and
        $raidId -eq $sourceQuestId -and
        $null -ne $raidHeroCountValue -and
        [int]$raidHeroCountValue -eq $partySize) {
        return "SelectionConfirmed"
    }

    $latestCompletedQuestNames = Get-DdrtPathValue $Facts "campaignLog.latestCompletedPartyRaidRecord.questId.names"
    $latestCompletedSuccess = Get-DdrtPathValue $Facts "campaignLog.latestCompletedPartyRaidRecord.success"
    if ((Test-DdrtContainsValue $latestCompletedQuestNames $sourceQuestId) -and $latestCompletedSuccess -eq $true) {
        return "StageCompleted"
    }

    $lastRaidQuestNames = Get-DdrtPathValue $Facts "progression.lastRaidQuest.names"
    $lastRaidSuccess = Get-DdrtPathValue $Facts "progression.lastRaidSuccess"
    if ($null -ne $lockedSelection -and (Test-DdrtContainsValue $lastRaidQuestNames $sourceQuestId)) {
        if ($lastRaidSuccess -eq $true) {
            return "StageCompleted"
        }

        if ($lastRaidSuccess -eq $false) {
            return "StageFailed"
        }
    }

    return "None"
}

Push-Location $projectRoot
try {
    $configFullPath = Get-DdrtResolvedPath `
        -Path $ConfigPath `
        -Leaf `
        -MissingMessage "Config file was not found: $ConfigPath"

    $assemblyFullPath = Resolve-Path -LiteralPath (Resolve-DdrtProjectPath $AssemblyPath) -ErrorAction SilentlyContinue
    if ($null -eq $assemblyFullPath -and -not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    $stateRoot = if ([string]::IsNullOrWhiteSpace($ModStateDirectory)) {
        Join-Path $projectRoot "state\live_profile_challenge_event_test\$sessionId"
    } else {
        Resolve-DdrtProjectPath $ModStateDirectory
    }

    if (-not $UseExistingModState) {
        New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    } else {
        Assert-DdrtTrue (Test-Path -LiteralPath $stateRoot -PathType Container) "Existing mod state directory was not found: $stateRoot"
    }

    $factsResult = Export-DdrtLiveSaveFacts `
        -LiveProfileDirectory $LiveProfileDirectory `
        -AssemblyPath $AssemblyPath `
        -GameDirectory $GameDirectory `
        -ExportScriptPath $ExportScriptPath `
        -SessionPrefix "live_profile_challenge_events"
    $facts = $factsResult.facts

    $baseArgs = @(
        "--config", $configFullPath,
        "--no-inject",
        "--allow-non-atomic-state-writes",
        "--mod-state-id", "validation.challenge_run_contract",
        "--mod-state-dir", $stateRoot
    )

    if (-not $UseExistingModState) {
        Invoke-DdrtLoader -LoaderArgs ($baseArgs + @("--init-mod-state"))
        Invoke-DdrtLoader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.run_started"))
    }

    $preBridgeState = Read-DdrtChallengeState -Root $stateRoot
    $actualExpectedOutcome = if ($ExpectedOutcome -eq "Auto") {
        Get-LiveOutcome $facts $preBridgeState
    } else {
        $ExpectedOutcome
    }

    Invoke-DdrtLoader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $factsResult.reportPath))

    $bridgeReportResult = Read-DdrtSaveEventBridgeReport
    $bridgeReport = $bridgeReportResult.report
    $postBridgeState = Read-DdrtChallengeState -Root $stateRoot
    $executedEventIds = Get-DdrtExecutedEventIds $bridgeReport
    $issues = ConvertTo-DdrtArray (Get-DdrtObjectProperty $bridgeReport "issues")
    $errorIssues = @($issues | Where-Object { (Get-DdrtObjectProperty $_ "severity") -eq "error" })
    Assert-DdrtTrue ($errorIssues.Count -eq 0) "Save event bridge reported error issues."

    $currentStage = Get-DdrtObjectProperty $preBridgeState "currentStage"
    $stageId = [string](Get-DdrtObjectProperty $currentStage "id")

    switch ($actualExpectedOutcome) {
        "None" {
            Assert-DdrtTrue ([int](Get-DdrtObjectProperty $bridgeReport "inferredEventCount") -eq 0) "Expected no inferred challenge events from the live profile."
            Assert-DdrtTrue ($executedEventIds.Count -eq 0) "Expected no executed challenge events from the live profile."
            Assert-DdrtTrue ([int](Get-DdrtObjectProperty $postBridgeState "currentStageIndex") -eq [int](Get-DdrtObjectProperty $preBridgeState "currentStageIndex")) "No-event bridge pass should not advance the challenge stage."
        }
        "SelectionConfirmed" {
            Assert-DdrtTrue ($executedEventIds.Count -eq 1) "Expected exactly one executed event for active live raid selection."
            Assert-DdrtTrue ($executedEventIds[0] -eq "challenge.stage_selection_confirmed") "Expected challenge.stage_selection_confirmed, found '$($executedEventIds -join ",")'."
            $lockedSelection = Get-DdrtObjectProperty $postBridgeState "lockedStageSelection"
            Assert-DdrtTrue ($null -ne $lockedSelection) "Selection confirmation should lock the live challenge selection."
            Assert-DdrtTrue ((Get-DdrtObjectProperty $lockedSelection "stageId") -eq $stageId) "Locked selection should target stage '$stageId'."
            Assert-DdrtTrue ((ConvertTo-DdrtArray (Get-DdrtObjectProperty $lockedSelection "heroIds")).Count -eq [int](Get-DdrtObjectProperty $preBridgeState "partySize")) "Locked selection should contain the configured party size."
        }
        "StageCompleted" {
            Assert-DdrtTrue ((Test-DdrtContainsValue $executedEventIds "challenge.stage_completed")) "Expected challenge.stage_completed to execute."
            Assert-DdrtTrue ([int](Get-DdrtObjectProperty $postBridgeState "currentStageIndex") -eq ([int](Get-DdrtObjectProperty $preBridgeState "currentStageIndex") + 1)) "Stage completion should advance currentStageIndex by one."
            Assert-DdrtTrue ((Test-DdrtContainsValue (Get-DdrtObjectProperty $postBridgeState "completedStageIds") $stageId)) "Stage completion should record completed stage '$stageId'."
            Assert-DdrtTrue ($null -eq (Get-DdrtObjectProperty $postBridgeState "lockedStageSelection")) "Stage completion should clear the locked selection."
            Assert-DdrtTrue ((ConvertTo-DdrtArray (Get-DdrtObjectProperty $postBridgeState "usedHeroIds")).Count -gt 0) "Stage completion should consume selected heroes."
        }
        "StageFailed" {
            Assert-DdrtTrue ($executedEventIds.Count -eq 1) "Expected exactly one executed event for failed live stage."
            Assert-DdrtTrue ($executedEventIds[0] -eq "challenge.stage_failed") "Expected challenge.stage_failed, found '$($executedEventIds -join ",")'."
            Assert-DdrtTrue ($null -ne (Get-DdrtObjectProperty $postBridgeState "lockedStageSelection")) "Stage failure should keep the locked selection for retry."
            $attempts = ConvertTo-DdrtArray (Get-DdrtObjectProperty $postBridgeState "stageAttempts")
            Assert-DdrtTrue (($attempts | Where-Object { (Get-DdrtObjectProperty $_ "stageId") -eq $stageId }).Count -gt 0) "Stage failure should record a failed attempt for '$stageId'."
        }
    }

    Write-Host "PASS: live profile challenge event bridge outcome '$actualExpectedOutcome' validated."
    Write-Host "Save state report: $($factsResult.reportPath)"
    Write-Host "Bridge report: $($bridgeReportResult.path)"
    Write-Host "State directory: $stateRoot"
    Write-Host "Executed events: $(if ($executedEventIds.Count -eq 0) { '<none>' } else { $executedEventIds -join ', ' })"
}
finally {
    Pop-Location
}
