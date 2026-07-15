param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\runtime_event_executor_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\runtime_event_executor_test\$sessionId"
$payloadRoot = Join-Path $testRoot "payloads"
$bossPluginId = "validation.boss_gauntlet_campaign_contract"

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

function Read-BossGauntletState {
    $document = Read-StateDocument $bossPluginId
    return $document.state.bossGauntlet
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Read-RuntimeEventReport {
    $path = Join-Path $projectRoot.Path "logs\runtime_event_report.json"
    Assert-True (Test-Path -LiteralPath $path) "Runtime event report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Get-ActionReport {
    param(
        [object]$Report,
        [string]$Type
    )

    $actions = @(Convert-ToArray $Report.rules | ForEach-Object {
        Convert-ToArray $_.actions
    } | Where-Object {
        $_.type -eq $Type
    })

    Assert-True ($actions.Count -eq 1) "Expected exactly one action report for '$Type', found $($actions.Count)."
    return $actions[0]
}

function Read-ManagedActionArtifact {
    param(
        [object]$Action,
        [string]$ExpectedType
    )

    Assert-True ($Action.status -eq "materialized") "Managed action '$ExpectedType' should be materialized, not only planned."
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$Action.artifactPath)) "Managed action '$ExpectedType' should include an artifact path."

    $artifactPath = [System.IO.Path]::GetFullPath([string]$Action.artifactPath)
    $resolvedStateRoot = [System.IO.Path]::GetFullPath($stateRoot)
    Assert-True ($artifactPath.StartsWith($resolvedStateRoot, [System.StringComparison]::OrdinalIgnoreCase)) "Managed action artifact should stay inside this test state root: $artifactPath"
    Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Managed action artifact was not written: $artifactPath"

    $artifact = Get-Content -Raw -LiteralPath $artifactPath | ConvertFrom-Json
    Assert-True ($artifact.status -eq "materialized") "Managed action artifact status should be materialized."
    Assert-True ($artifact.action.type -eq $ExpectedType) "Managed action artifact type mismatch."
    Assert-True ($artifact.plan.kind -eq $ExpectedType) "Managed action artifact plan kind mismatch."
    return $artifact
}

$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
$resolvedLogsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot.Path "logs"))
Assert-True ($resolvedTestRoot.StartsWith($resolvedLogsRoot, [System.StringComparison]::OrdinalIgnoreCase)) "Refusing to clean outside logs: $resolvedTestRoot"

New-Item -ItemType Directory -Force -Path $testRoot, $payloadRoot | Out-Null

$bossArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $bossPluginId,
    "--mod-state-dir", $stateRoot
)
Invoke-Loader -LoaderArgs ($bossArgs + @("--init-mod-state"))

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
$runtimeEventReport = Read-RuntimeEventReport
$strictArgIssue = @(Convert-ToArray $runtimeEventReport.issues | Where-Object {
    $_.code -eq "action-failed" -and
    [string]$_.message -like "*fromEvent*" -and
    [string]$_.message -like "*selectedHeroIds*"
})
Assert-True ($strictArgIssue.Count -gt 0) "Missing event payload field should fail the action instead of being treated as an empty no-op."

Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "profile.initialization_requested"))
$state = Read-BossGauntletState
Assert-True ([bool]$state.initialized) "Boss gauntlet initialization should mark the profile initialized."
Assert-True ($state.phase -eq "boss_gauntlet") "Boss gauntlet initialization should enter boss_gauntlet phase."
Assert-True ((Convert-ToArray $state.fixedQuestIds).Count -eq 8) "Boss gauntlet initialization should load fixed boss quest ids from the definition."
Assert-True ((Convert-ToArray $state.fixedQuestIds) -contains "plot_kill_necromancer_3") "Fixed boss quest ids should include the Necromancer fixture quest."
Assert-True ([int]$state.wallet.gold -eq 20000) "Boss gauntlet initialization should set the starting gold."

$runtimeEventReport = Read-RuntimeEventReport
Assert-True ([int]$runtimeEventReport.materializedActionCount -eq 13) "Boss gauntlet initialization should materialize profile-normalization actions."
$rosterAction = Get-ActionReport -Report $runtimeEventReport -Type "roster.ensureClassInstances"
$rosterArtifact = Read-ManagedActionArtifact -Action $rosterAction -ExpectedType "roster.ensureClassInstances"
Assert-True ([int]$rosterArtifact.plan.arguments.copiesPerClass -eq 2) "Roster normalization artifact should request two heroes per class."
Assert-True ($rosterArtifact.plan.arguments.level -eq "max") "Roster normalization artifact should request max-level heroes."
$questBoardAction = Get-ActionReport -Report $runtimeEventReport -Type "questBoard.replaceWithFixedSet"
$questBoardArtifact = Read-ManagedActionArtifact -Action $questBoardAction -ExpectedType "questBoard.replaceWithFixedSet"
Assert-True ((Convert-ToArray $questBoardArtifact.plan.arguments.questIds) -contains "plot_kill_necromancer_3") "Quest board artifact should include the Necromancer fixture quest."

$firstSelectionPayloadPath = Write-JsonPayload "boss_selection_necromancer.json" ([pscustomobject]@{
    questId = "plot_kill_necromancer_3"
    selectedHeroIds = @("hero_1", "hero_2", "hero_3", "hero_4")
    selectedTrinketIds = @("trinket_1", "trinket_2")
})

Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $firstSelectionPayloadPath))
$state = Read-BossGauntletState
Assert-True ($state.activeSelection.questId -eq "plot_kill_necromancer_3") "Selection lock should record the active boss quest."
Assert-True ((Convert-ToArray $state.activeSelection.heroIds).Count -eq 4) "Selection lock should record four selected heroes."
Assert-True ((Convert-ToArray $state.activeSelection.trinketIds).Count -eq 2) "Selection lock should record selected trinkets."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 0) "Selection confirmation should not consume heroes."

$firstSuccessPayloadPath = Write-JsonPayload "boss_attempt_necromancer_success.json" ([pscustomobject]@{
    questId = "plot_kill_necromancer_3"
    success = $true
    attemptId = "attempt_necromancer_success_001"
})

Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $firstSuccessPayloadPath))
$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 1) "Successful attempt should be recorded once."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 4) "Successful attempt should consume selected heroes."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 2) "Successful attempt should consume selected trinkets."
Assert-True ((Convert-ToArray $state.completedQuestIds) -contains "plot_kill_necromancer_3") "Successful attempt should mark the boss quest completed."
Assert-True ([int]$state.wallet.gold -eq 30000) "Successful attempt should add the configured victory reward."
Assert-True ($state.lastResolvedAttemptId -eq "attempt_necromancer_success_001") "Successful attempt should persist the resolved attempt id."
Assert-True ($null -eq $state.activeSelection) "Resolved attempt should clear active selection."

Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $firstSuccessPayloadPath))
$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 1) "Duplicate successful attempt without an active selection should not record again."
Assert-True ([int]$state.wallet.gold -eq 30000) "Duplicate successful attempt should not pay the reward again."

Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "profile.initialization_requested"))
$state = Read-BossGauntletState
Assert-True ([int]$state.wallet.gold -eq 30000) "Repeated initialization must not reset changed wallet state."
Assert-True ((Convert-ToArray $state.completedQuestIds) -contains "plot_kill_necromancer_3") "Repeated initialization must not clear completed boss quests."
$runtimeEventReport = Read-RuntimeEventReport
Assert-True ([int]$runtimeEventReport.materializedActionCount -eq 0) "Repeated initialization should not materialize profile-normalization actions."
Assert-True ([int]$runtimeEventReport.executedActionCount -eq 0) "Repeated initialization should not execute state initialization actions."

$failedSelectionPayloadPath = Write-JsonPayload "boss_selection_prophet_failed.json" ([pscustomobject]@{
    questId = "plot_kill_prophet_3"
    selectedHeroIds = @("hero_5", "hero_6", "hero_7", "hero_8")
    selectedTrinketIds = @("trinket_3", "trinket_4")
})
Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $failedSelectionPayloadPath))

$failedAttemptPayloadPath = Write-JsonPayload "boss_attempt_prophet_failed.json" ([pscustomobject]@{
    questId = "plot_kill_prophet_3"
    success = $false
    attemptId = "attempt_prophet_failed_001"
})
Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $failedAttemptPayloadPath))
$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 2) "Failed attempt should be recorded."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 8) "Failed attempt should also consume selected heroes."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 4) "Failed attempt should also consume selected trinkets."
Assert-True (-not ((Convert-ToArray $state.completedQuestIds) -contains "plot_kill_prophet_3")) "Failed attempt should not complete the boss quest."
Assert-True ([int]$state.wallet.gold -eq 30000) "Failed attempt should not pay the victory reward."
Assert-True ($null -eq $state.activeSelection) "Failed attempt should clear active selection."

$bossStatePath = Join-Path $stateRoot "$bossPluginId.json"
$bossStateDocument = Get-Content -Raw -LiteralPath $bossStatePath | ConvertFrom-Json
$bossStateDocument.state.bossGauntlet.victoryGold = "invalid-transaction-probe"
$bossStateDocument | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $bossStatePath -Encoding UTF8

$rollbackSelectionPayloadPath = Write-JsonPayload "boss_selection_hag_atomic_rollback.json" ([pscustomobject]@{
    questId = "plot_kill_hag_3"
    selectedHeroIds = @("hero_9", "hero_10", "hero_11", "hero_12")
    selectedTrinketIds = @("trinket_5", "trinket_6")
})
Invoke-Loader -LoaderArgs ($bossArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $rollbackSelectionPayloadPath))

$rollbackAttemptId = "attempt_hag_atomic_rollback_001"
$rollbackAttemptPayloadPath = Write-JsonPayload "boss_attempt_hag_atomic_rollback.json" ([pscustomobject]@{
    questId = "plot_kill_hag_3"
    success = $true
    attemptId = $rollbackAttemptId
})
Invoke-LoaderExpectFailure -LoaderArgs ($bossArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $rollbackAttemptPayloadPath))
$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 3) "Actions completed before the failed reward should still be committed."
Assert-True ([int]$state.wallet.gold -eq 30000) "A failed reward action must not change sidecar wallet state."
Assert-True (-not ((Convert-ToArray $state.rewardedAttemptFingerprints) -contains $rollbackAttemptId)) "A failed reward action must discard its partially written reward fingerprint."
$runtimeEventReport = Read-RuntimeEventReport
$failedRewardAction = Get-ActionReport -Report $runtimeEventReport -Type "wallet.addCurrencyOnEvent"
Assert-True ($failedRewardAction.status -eq "failed") "The invalid reward action should remain visible as failed."

Write-Host "PASS: runtime event executor state assertions passed."
