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
