param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\runtime_event_executor_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\runtime_event_executor_test\$sessionId"
$payloadRoot = Join-Path $testRoot "payloads"

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

function Invoke-LoaderExpectFailure {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -eq 0) {
        throw "DDRuntimeLoader was expected to fail but returned exit code 0"
    }
}

function Write-JsonPayload {
    param(
        [string]$Name,
        [object]$Payload
    )

    $path = Join-Path $payloadRoot $Name
    $Payload | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Read-StateDocument {
    param([string]$PluginId)

    $path = Join-Path $stateRoot "$PluginId.json"
    Assert-True (Test-Path -LiteralPath $path) "Sidecar state was not created: $path"
    $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    return $document
}

function Read-ChallengeState {
    $document = Read-StateDocument "validation.challenge_run_contract"
    return $document.state.challengeRun
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
$resolvedLogsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot.Path "logs"))
Assert-True ($resolvedTestRoot.StartsWith($resolvedLogsRoot, [System.StringComparison]::OrdinalIgnoreCase)) "Refusing to clean outside logs: $resolvedTestRoot"

New-Item -ItemType Directory -Force -Path $testRoot, $payloadRoot | Out-Null

$baseArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", "validation.challenge_run_contract",
    "--mod-state-dir", $stateRoot
)
Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))

$draftArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", "validation.quest_draft_contract",
    "--mod-state-dir", $stateRoot
)
Invoke-Loader -LoaderArgs ($draftArgs + @("--init-mod-state"))
$draftPayloadPath = Write-JsonPayload "party_selection_confirmed.json" ([pscustomobject]@{
    selectedHeroIds = @("1", "2")
})
Invoke-Loader -LoaderArgs ($draftArgs + @("--emit-event", "party.selection_confirmed", "--event-payload-file", $draftPayloadPath))
$draftState = (Read-StateDocument "validation.quest_draft_contract").state
Assert-True ((Convert-ToArray $draftState.usedHeroIds).Count -eq 2) "Draft event should add selected heroes from event payload."
Assert-True ((Convert-ToArray $draftState.usedHeroIds) -contains "1") "Draft event should include hero 1."
Assert-True ((Convert-ToArray $draftState.usedHeroIds) -contains "2") "Draft event should include hero 2."

$missingDraftPayloadPath = Write-JsonPayload "party_selection_missing_selected_heroes.json" ([pscustomobject]@{})
Invoke-LoaderExpectFailure -LoaderArgs ($draftArgs + @("--emit-event", "party.selection_confirmed", "--event-payload-file", $missingDraftPayloadPath))
$runtimeEventReportPath = Join-Path $projectRoot.Path "logs\runtime_event_report.json"
$runtimeEventReport = Get-Content -Raw -LiteralPath $runtimeEventReportPath | ConvertFrom-Json
$strictArgIssue = @(Convert-ToArray $runtimeEventReport.issues | Where-Object {
    $_.code -eq "action-failed" -and
    [string]$_.message -like "*fromEvent*" -and
    [string]$_.message -like "*selectedHeroIds*"
})
Assert-True ($strictArgIssue.Count -gt 0) "Missing event payload field should fail the action instead of being treated as an empty no-op."

$selectionPayload = [pscustomobject]@{
    stageId = "stage_1_necromancer"
    selectedHeroIds = @("1", "2", "7", "8")
    selectedTrinketIds = @("berserk_mask", "immunity_mask", "fortunate_armlet", "sb_4", "sb_3", "sb_2", "sb_1", "bleeding_pendant")
}
$selectionPayloadPath = Write-JsonPayload "selection_confirmed.json" $selectionPayload

Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_selection_confirmed", "--event-payload-file", $selectionPayloadPath))
$state = Read-ChallengeState
Assert-True ($state.lockedStageSelection.stageId -eq "stage_1_necromancer") "Selection lock stage id was not recorded."
Assert-True ((Convert-ToArray $state.lockedStageSelection.heroIds).Count -eq 4) "Selection lock hero count was not recorded."
Assert-True ((Convert-ToArray $state.usedHeroIds).Count -eq 0) "Selection confirmation should not mark heroes used."

$stagePayloadPath = Write-JsonPayload "stage_result.json" ([pscustomobject]@{
    stageId = "stage_1_necromancer"
})

Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_failed", "--event-payload-file", $stagePayloadPath))
$state = Read-ChallengeState
Assert-True ($state.lockedStageSelection.stageId -eq "stage_1_necromancer") "Failure should keep locked selection."
Assert-True ((Convert-ToArray $state.stageAttempts).Count -eq 1) "Failure should record one attempt."
Assert-True ((Convert-ToArray $state.stageAttempts)[0].result -eq "failed") "Failure attempt result was not recorded."
Assert-True ((Convert-ToArray $state.usedHeroIds).Count -eq 0) "Failure should not mark heroes used."

Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_completed", "--event-payload-file", $stagePayloadPath))
$state = Read-ChallengeState
Assert-True ([int]$state.currentStageIndex -eq 1) "Completion should advance currentStageIndex to 1."
Assert-True ((Convert-ToArray $state.completedStageIds) -contains "stage_1_necromancer") "Completion should record completed stage id."
Assert-True ((Convert-ToArray $state.usedHeroIds).Count -eq 4) "Completion should mark selected heroes used."
Assert-True ((Convert-ToArray $state.usedTrinketIds).Count -eq 8) "Completion should mark selected trinkets used."
Assert-True ($null -eq $state.lockedStageSelection) "Completion should clear locked selection."
Assert-True ((Convert-ToArray $state.stageAttempts).Count -eq 2) "Completion should record a second attempt."
Assert-True ((Convert-ToArray $state.stageAttempts)[1].result -eq "completed") "Completion attempt result was not recorded."

Write-Host "PASS: runtime event executor state assertions passed."
