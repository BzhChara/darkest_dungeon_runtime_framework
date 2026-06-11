param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\boss_gauntlet_contract_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\boss_gauntlet_contract_test\$sessionId"
$payloadRoot = Join-Path $testRoot "payloads"
$pluginId = "validation.boss_gauntlet_campaign_contract"

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

function Write-JsonPayload {
    param(
        [string]$Name,
        [object]$Payload
    )

    $path = Join-Path $payloadRoot $Name
    $Payload | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Read-BossGauntletState {
    $path = Join-Path $stateRoot "$pluginId.json"
    Assert-True (Test-Path -LiteralPath $path) "Sidecar state was not created: $path"
    $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    return $document.state.bossGauntlet
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

    Assert-True ($Action.status -eq "materialized") "Managed action '$ExpectedType' should be materialized."
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

New-Item -ItemType Directory -Force -Path $testRoot, $payloadRoot | Out-Null

$baseArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $stateRoot
)

Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
$state = Read-BossGauntletState
Assert-True (-not [bool]$state.initialized) "Boss gauntlet state should start uninitialized."
Assert-True ($state.phase -eq "uninitialized") "Boss gauntlet phase should start as uninitialized."

Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "profile.initialization_requested"))
$state = Read-BossGauntletState
Assert-True ([bool]$state.initialized) "Initialization should mark the profile initialized."
Assert-True ($state.phase -eq "boss_gauntlet") "Initialization should enter boss_gauntlet phase."
Assert-True ([int]$state.wallet.gold -eq 20000) "Initialization should set gold to 20000."
Assert-True ([bool]$state.trinketSaleDisabled) "Initialization should disable trinket selling in sidecar state."
Assert-True ((Convert-ToArray $state.fixedQuestIds).Count -eq 2) "Initialization should load the fixed boss quest ids from the definition."
Assert-True ((Convert-ToArray $state.fixedQuestIds) -contains "plot_kill_necromancer_3") "Fixed quest ids should include the Necromancer fixture quest."
Assert-True ([bool]$state.finaleDoesNotReviveDeadHeroes) "Definition should record that finale unlock does not revive dead heroes."

$initializationReport = Read-RuntimeEventReport
Assert-True ([int]$initializationReport.materializedActionCount -eq 11) "Initialization should materialize eleven managed profile-normalization actions."

$rosterAction = Get-ActionReport -Report $initializationReport -Type "roster.ensureClassInstances"
$rosterArtifact = Read-ManagedActionArtifact -Action $rosterAction -ExpectedType "roster.ensureClassInstances"
Assert-True ([int]$rosterArtifact.plan.arguments.copiesPerClass -eq 2) "Roster normalization should request two heroes per class."
Assert-True ($rosterArtifact.plan.arguments.level -eq "max") "Roster normalization should request max-level heroes."
Assert-True ($rosterArtifact.plan.arguments.positiveQuirks -eq "full_random") "Roster normalization should request full random positive quirks."
Assert-True ($rosterArtifact.plan.arguments.negativeQuirks -eq "one_random") "Roster normalization should request one random negative quirk."

$walletAction = Get-ActionReport -Report $initializationReport -Type "wallet.setCurrencyAmount"
$walletArtifact = Read-ManagedActionArtifact -Action $walletAction -ExpectedType "wallet.setCurrencyAmount"
Assert-True ([int]$walletArtifact.plan.arguments.amount -eq 20000) "Wallet normalization should request 20000 starting gold."

$questBoardAction = Get-ActionReport -Report $initializationReport -Type "questBoard.replaceWithFixedSet"
$questBoardArtifact = Read-ManagedActionArtifact -Action $questBoardAction -ExpectedType "questBoard.replaceWithFixedSet"
Assert-True ((Convert-ToArray $questBoardArtifact.plan.arguments.questIds) -contains "plot_kill_necromancer_3") "Quest board normalization should include the Necromancer fixture quest."
Assert-True ([bool]$questBoardArtifact.plan.arguments.removeCompleted) "Quest board normalization should request completed fixed quests to be removed."

$townEventAction = Get-ActionReport -Report $initializationReport -Type "townEvent.overrideCurrent"
$townEventArtifact = Read-ManagedActionArtifact -Action $townEventAction -ExpectedType "townEvent.overrideCurrent"
Assert-True ($townEventArtifact.plan.arguments.event.message -eq "Enjoy the inferno") "Town event normalization should request the fixed event message."

$deadHeroPayloadPath = Write-JsonPayload "dead_hero_observed.json" ([pscustomobject]@{
    heroIds = @("dead_hero_1")
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "profile.dead_hero_observed", "--event-payload-file", $deadHeroPayloadPath))

$firstSelectionPayloadPath = Write-JsonPayload "selection_necromancer.json" ([pscustomobject]@{
    questId = "plot_kill_necromancer_3"
    selectedHeroIds = @("hero_1", "hero_2", "hero_3", "hero_4")
    selectedTrinketIds = @("trinket_1", "trinket_2")
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $firstSelectionPayloadPath))
$state = Read-BossGauntletState
Assert-True ($state.activeSelection.questId -eq "plot_kill_necromancer_3") "Selection confirmation should lock the active boss quest."
Assert-True ((Convert-ToArray $state.activeSelection.heroIds).Count -eq 4) "Selection confirmation should lock four selected heroes."

$firstSuccessPayloadPath = Write-JsonPayload "attempt_necromancer_success.json" ([pscustomobject]@{
    questId = "plot_kill_necromancer_3"
    success = $true
    attemptId = "attempt_necromancer_success_001"
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $firstSuccessPayloadPath))
$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 1) "Successful attempt should be recorded once."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 4) "Successful attempt should consume selected heroes."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 2) "Successful attempt should consume selected trinkets."
Assert-True ((Convert-ToArray $state.completedQuestIds) -contains "plot_kill_necromancer_3") "Successful attempt should complete the boss quest."
Assert-True ([int]$state.wallet.gold -eq 30000) "Successful attempt should add the 10000 gold victory reward."
Assert-True ($state.phase -eq "boss_gauntlet") "Completing only one fixed boss should stay in boss_gauntlet phase."
Assert-True ($null -eq $state.activeSelection) "Resolved attempt should clear active selection."

Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $firstSuccessPayloadPath))
$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 1) "Duplicate successful attempt should not record again after active selection clears."
Assert-True ([int]$state.wallet.gold -eq 30000) "Duplicate successful attempt should not pay the victory reward again."

Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "profile.initialization_requested"))
$state = Read-BossGauntletState
Assert-True ([int]$state.wallet.gold -eq 30000) "Initialization must be idempotent and must not reset gold after the run has changed."
Assert-True ((Convert-ToArray $state.completedQuestIds) -contains "plot_kill_necromancer_3") "Initialization must not clear completed boss quests."
$reinitializationReport = Read-RuntimeEventReport
Assert-True ([int]$reinitializationReport.materializedActionCount -eq 0) "Repeated initialization should not materialize profile-normalization actions after initialized=true."
Assert-True ([int]$reinitializationReport.executedActionCount -eq 0) "Repeated initialization should not execute state initialization actions after initialized=true."

$failedSelectionPayloadPath = Write-JsonPayload "selection_prophet_failed.json" ([pscustomobject]@{
    questId = "plot_kill_prophet_3"
    selectedHeroIds = @("hero_5", "hero_6", "hero_7", "hero_8")
    selectedTrinketIds = @("trinket_3", "trinket_4")
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $failedSelectionPayloadPath))

$failedAttemptPayloadPath = Write-JsonPayload "attempt_prophet_failed.json" ([pscustomobject]@{
    questId = "plot_kill_prophet_3"
    success = $false
    attemptId = "attempt_prophet_failed_001"
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $failedAttemptPayloadPath))
$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 2) "Failed attempt should be recorded."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 8) "Failed attempt should also consume selected heroes."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 4) "Failed attempt should also consume selected trinkets."
Assert-True (-not ((Convert-ToArray $state.completedQuestIds) -contains "plot_kill_prophet_3")) "Failed attempt should not complete the boss quest."
Assert-True ([int]$state.wallet.gold -eq 30000) "Failed attempt should not pay the victory reward."
Assert-True ($state.phase -eq "boss_gauntlet") "Failed attempt should keep the boss gauntlet phase."
Assert-True ($null -eq $state.activeSelection) "Failed attempt should clear active selection."

$secondSuccessSelectionPayloadPath = Write-JsonPayload "selection_prophet_success.json" ([pscustomobject]@{
    questId = "plot_kill_prophet_3"
    selectedHeroIds = @("hero_9", "hero_10", "hero_11", "hero_12")
    selectedTrinketIds = @("trinket_5", "trinket_6")
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $secondSuccessSelectionPayloadPath))

$secondSuccessPayloadPath = Write-JsonPayload "attempt_prophet_success.json" ([pscustomobject]@{
    questId = "plot_kill_prophet_3"
    success = $true
    attemptId = "attempt_prophet_success_001"
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $secondSuccessPayloadPath))
$state = Read-BossGauntletState
Assert-True ($state.phase -eq "darkest_finale") "Completing all fixed bosses should unlock darkest_finale."
Assert-True ((Convert-ToArray $state.completedQuestIds).Count -eq 2) "Both fixed boss quests should be completed."
Assert-True ((Convert-ToArray $state.attempts).Count -eq 3) "The final successful attempt should be recorded."
Assert-True ([int]$state.wallet.gold -eq 40000) "The final successful attempt should pay exactly one more victory reward."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 0) "Finale unlock should clear sidecar hero reuse restrictions."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 0) "Finale unlock should clear sidecar trinket reuse restrictions."
Assert-True ((Convert-ToArray $state.observedDeadHeroIds) -contains "dead_hero_1") "Finale unlock should not erase observed dead hero state."
Assert-True ($null -eq $state.activeSelection) "Finale unlock should leave active selection cleared."

$postFinaleSelectionPayloadPath = Write-JsonPayload "selection_after_finale.json" ([pscustomobject]@{
    questId = "plot_kill_necromancer_3"
    selectedHeroIds = @("hero_1", "hero_2", "hero_3", "hero_4")
    selectedTrinketIds = @("trinket_1", "trinket_2")
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $postFinaleSelectionPayloadPath))
$state = Read-BossGauntletState
Assert-True ($null -eq $state.activeSelection) "Boss-gauntlet selection lock should not run after darkest_finale unlocks."

Write-Host "PASS: boss gauntlet contract state assertions passed."
