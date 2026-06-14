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
    param([string]$Root = $stateRoot)

    $path = Join-Path $Root "$pluginId.json"
    Assert-True (Test-Path -LiteralPath $path) "Sidecar state was not created: $path"
    $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    return $document.state.bossGauntlet
}

function Read-RuntimeEventReport {
    $path = Join-Path $projectRoot.Path "logs\runtime_event_report.json"
    Assert-True (Test-Path -LiteralPath $path) "Runtime event report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-QuestBoardPreviewReport {
    $path = Join-Path $projectRoot.Path "logs\quest_board_preview_report.json"
    Assert-True (Test-Path -LiteralPath $path) "Quest board preview report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-QuestBoardPolicyMaterializeReport {
    $path = Join-Path $projectRoot.Path "logs\quest_board_policy_materialize_report.json"
    Assert-True (Test-Path -LiteralPath $path) "Quest board policy materialize report was not created: $path"
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
Assert-True ((Convert-ToArray $state.fixedQuestIds).Count -eq 8) "Initialization should load the fixed boss quest ids from the definition."
Assert-True ((Convert-ToArray $state.fixedQuestIds) -contains "plot_kill_necromancer_3") "Fixed quest ids should include the Necromancer fixture quest."
Assert-True ($state.finaleQuestChainId -eq "boss_gauntlet_darkest_finale_chain") "Initialization should load the finale quest chain id from the definition."
Assert-True ([bool]$state.finaleDoesNotReviveDeadHeroes) "Definition should record that finale unlock does not revive dead heroes."

$generatedPolicyReportPath = Join-Path $stateRoot "_quest_board_policies\validation.boss_gauntlet_campaign_contract\001_boss_gauntlet_darkest_finale_chain.generated.linear_progression_policy.json"
Assert-True (Test-Path -LiteralPath $generatedPolicyReportPath -PathType Leaf) "Finale quest chain should generate a quest board policy report: $generatedPolicyReportPath"
$generatedPolicyReport = Get-Content -Raw -LiteralPath $generatedPolicyReportPath | ConvertFrom-Json
Assert-True ([bool]$generatedPolicyReport.succeeded) "Finale quest chain generated policy should validate successfully."
Assert-True ([int]$generatedPolicyReport.entryCount -eq 4) "Finale quest chain generated policy should contain DD1-DD4."
Assert-True (@($generatedPolicyReport.refreshTriggers) -contains "immediateOnQuestComplete") "Finale generated policy should refresh immediately on quest completion."
Assert-True (@($generatedPolicyReport.refreshTriggers) -contains "onWeekAdvance") "Finale generated policy should also refresh after week advance."
$dd1PolicyEntry = @($generatedPolicyReport.entries)[0]
$dd2PolicyEntry = @($generatedPolicyReport.entries)[1]
Assert-True ($dd1PolicyEntry.effectiveQuestId -eq "plot_darkest_dungeon_1") "Finale generated policy should start at DD1."
Assert-True ($dd1PolicyEntry.availableWhen.stateKey -eq "bossGauntlet.phase") "DD1 should be gated by the chain unlock state key."
Assert-True ($dd1PolicyEntry.availableWhen.stateEquals -eq "darkest_finale") "DD1 should require darkest_finale phase."
Assert-True (@($dd1PolicyEntry.availableWhen.notCompletedQuests) -contains "plot_darkest_dungeon_1") "DD1 should disappear after completion."
Assert-True (@($dd2PolicyEntry.availableWhen.completedQuests) -contains "plot_darkest_dungeon_1") "DD2 should require DD1 completion."
Assert-True (@($dd2PolicyEntry.availableWhen.notCompletedQuests) -contains "plot_darkest_dungeon_2") "DD2 should disappear after completion."

$initializationReport = Read-RuntimeEventReport
Assert-True ([int]$initializationReport.materializedActionCount -eq 13) "Initialization should materialize thirteen managed profile-normalization actions."

$rosterAction = Get-ActionReport -Report $initializationReport -Type "roster.ensureClassInstances"
$rosterArtifact = Read-ManagedActionArtifact -Action $rosterAction -ExpectedType "roster.ensureClassInstances"
Assert-True ([int]$rosterArtifact.plan.arguments.copiesPerClass -eq 2) "Roster normalization should request two heroes per class."
Assert-True ($rosterArtifact.plan.arguments.level -eq "max") "Roster normalization should request max-level heroes."
Assert-True ($rosterArtifact.plan.arguments.positiveQuirks -eq "full_random") "Roster normalization should request full random positive quirks."
Assert-True ($rosterArtifact.plan.arguments.negativeQuirks -eq "one_random") "Roster normalization should request one random negative quirk."
Assert-True ($rosterArtifact.plan.arguments.nameSource -eq "content.hero_names.enabled") "Roster normalization should use the generic content hero name source."
Assert-True ($rosterArtifact.plan.arguments.nameLanguage -eq "schinese") "Roster normalization should request the configured hero name language."
Assert-True ($rosterArtifact.plan.arguments.nameSeed -eq "validation.fixed_resource_boss_gauntlet") "Roster normalization should request a stable name seed."
Assert-True ($rosterArtifact.plan.arguments.nameRenamePolicy -eq "generated_placeholders") "Roster normalization should only rename generated placeholder hero names."

$upgradeAction = Get-ActionReport -Report $initializationReport -Type "upgrade.ensurePurchases"
$upgradeArtifact = Read-ManagedActionArtifact -Action $upgradeAction -ExpectedType "upgrade.ensurePurchases"
Assert-True ($upgradeArtifact.plan.arguments.source -eq "content.upgrades.enabled") "Upgrade normalization should use the generic content upgrade source."
Assert-True ($upgradeArtifact.plan.arguments.mode -eq "all_requirements") "Upgrade normalization should request all requirements."
Assert-True ((Convert-ToArray $upgradeArtifact.plan.arguments.categories) -contains "combat_skill") "Upgrade normalization should include combat skill purchases."
Assert-True ((Convert-ToArray $upgradeArtifact.plan.arguments.categories) -contains "building") "Upgrade normalization should include building purchases."
Assert-True ($upgradeArtifact.plan.arguments.instanceSource -eq "profile.roster.heroes") "Upgrade normalization should derive instanced purchases from roster heroes."

$walletAction = Get-ActionReport -Report $initializationReport -Type "wallet.setCurrencyAmounts"
$walletArtifact = Read-ManagedActionArtifact -Action $walletAction -ExpectedType "wallet.setCurrencyAmounts"
Assert-True ([int]$walletArtifact.plan.arguments.amounts.gold -eq 20000) "Wallet normalization should request 20000 starting gold."
Assert-True ([int]$walletArtifact.plan.arguments.amounts.bust -eq 0) "Wallet normalization should include an explicit bust heirloom amount."
Assert-True ([int]$walletArtifact.plan.arguments.amounts.portrait -eq 0) "Wallet normalization should include an explicit portrait heirloom amount."
Assert-True ([int]$walletArtifact.plan.arguments.amounts.deed -eq 0) "Wallet normalization should include an explicit deed heirloom amount."
Assert-True ([int]$walletArtifact.plan.arguments.amounts.crest -eq 0) "Wallet normalization should include an explicit crest heirloom amount."
Assert-True ([int]$walletArtifact.plan.arguments.amounts.shard -eq 36) "Wallet normalization should include the configured shard amount."

$inventoryAction = Get-ActionReport -Report $initializationReport -Type "estate.ensureInventoryCounts"
$inventoryArtifact = Read-ManagedActionArtifact -Action $inventoryAction -ExpectedType "estate.ensureInventoryCounts"
Assert-True ($inventoryArtifact.plan.arguments.source -eq "content.trinkets.enabled") "Inventory normalization should use the generic content trinket source."
Assert-True ([int]$inventoryArtifact.plan.arguments.count -eq 2) "Inventory normalization should request two trinket copies."
Assert-True ((Convert-ToArray $inventoryArtifact.plan.arguments.excludeRarities) -contains "darkest_dungeon") "Inventory normalization should exclude Darkest Dungeon reward trinkets."
Assert-True ((Convert-ToArray $inventoryArtifact.plan.arguments.excludeRarities) -contains "trophy") "Inventory normalization should exclude boss trophy trinkets."

$campaignProgressAction = Get-ActionReport -Report $initializationReport -Type "campaign.resetPlotProgress"
$campaignProgressArtifact = Read-ManagedActionArtifact -Action $campaignProgressAction -ExpectedType "campaign.resetPlotProgress"
Assert-True ((Convert-ToArray $campaignProgressArtifact.plan.arguments.plotQuestIds) -contains "plot_darkest_dungeon_1") "Campaign progress reset should include DD1."
Assert-True ((Convert-ToArray $campaignProgressArtifact.plan.arguments.plotQuestIds) -contains "plot_darkest_dungeon_4") "Campaign progress reset should include DD4."
Assert-True ([bool]$campaignProgressArtifact.plan.arguments.clearHeroDarkestDungeonProgress) "Campaign progress reset should clear hero DD participation flags."

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

$remainingBossQuestIdsBeforeProphetFinale = @(
    "plot_kill_formless_flesh_3",
    "plot_kill_swine_prince_3",
    "plot_kill_brigand_cannon_3",
    "plot_kill_hag_3",
    "plot_kill_drowned_crew_3",
    "plot_kill_siren_3"
)

$autoIndex = 0
foreach ($questId in $remainingBossQuestIdsBeforeProphetFinale) {
    $autoIndex++
    $selectionPayloadPath = Write-JsonPayload "selection_auto_success_$autoIndex.json" ([pscustomobject]@{
        questId = $questId
        selectedHeroIds = @(
            "hero_auto_$($autoIndex)_1",
            "hero_auto_$($autoIndex)_2",
            "hero_auto_$($autoIndex)_3",
            "hero_auto_$($autoIndex)_4"
        )
        selectedTrinketIds = @(
            "trinket_auto_$($autoIndex)_1",
            "trinket_auto_$($autoIndex)_2"
        )
    })
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $selectionPayloadPath))

    $successPayloadPath = Write-JsonPayload "attempt_auto_success_$autoIndex.json" ([pscustomobject]@{
        questId = $questId
        success = $true
        attemptId = "attempt_auto_success_$autoIndex"
    })
    Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.attempt_resolved", "--event-payload-file", $successPayloadPath))
}

$state = Read-BossGauntletState
Assert-True ($state.phase -eq "boss_gauntlet") "Completing all but one fixed boss should stay in boss_gauntlet phase."
Assert-True ((Convert-ToArray $state.completedQuestIds).Count -eq 7) "Seven fixed boss quests should be completed before the final prerequisite boss."

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
Assert-True ((Convert-ToArray $state.completedQuestIds).Count -eq 8) "All fixed boss quests should be completed."
Assert-True ((Convert-ToArray $state.attempts).Count -eq 9) "The final successful attempt should be recorded."
Assert-True ([int]$state.wallet.gold -eq 100000) "Every successful prerequisite boss should pay the configured victory reward once."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 0) "Finale unlock should clear sidecar hero reuse restrictions."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 0) "Finale unlock should clear sidecar trinket reuse restrictions."
Assert-True ((Convert-ToArray $state.observedDeadHeroIds) -contains "dead_hero_1") "Finale unlock should not erase observed dead hero state."
Assert-True ($null -eq $state.activeSelection) "Finale unlock should leave active selection cleared."

$finaleNoCompletedFactsPath = Write-JsonPayload "finale_chain_no_dd_completed.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_contract_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            completedQuestIds = @()
        }
    }
})

Invoke-Loader -LoaderArgs ($baseArgs + @("--materialize-quest-board-policies", "--save-state-report", $finaleNoCompletedFactsPath, "--quest-board-policy-slots", "1"))
$finalePolicyMaterialize = Read-QuestBoardPolicyMaterializeReport
Assert-True ([bool]$finalePolicyMaterialize.succeeded) "Finale quest board policy materialization should succeed."
Assert-True ($finalePolicyMaterialize.status -eq "materialized") "Finale quest board policy should materialize DD1."
Assert-True ([int]$finalePolicyMaterialize.selectedQuestCount -eq 1) "Finale policy should select one current DD quest."
Assert-True (@($finalePolicyMaterialize.selectedQuestIds) -contains "plot_darkest_dungeon_1") "Finale policy should unlock DD1 after all fixed bosses are completed."
$finaleQuestBoardArtifact = Get-Content -Raw -LiteralPath $finalePolicyMaterialize.artifactPath | ConvertFrom-Json
Assert-True ((Convert-ToArray $finaleQuestBoardArtifact.plan.arguments.questIds).Count -eq 1) "Finale quest board should contain exactly one entry in the first slice."
Assert-True ((Convert-ToArray $finaleQuestBoardArtifact.plan.arguments.questIds) -contains "plot_darkest_dungeon_1") "Finale quest board should unlock DD1 after all fixed bosses are completed."
Assert-True (-not [bool]$finaleQuestBoardArtifact.plan.arguments.removeCompleted) "Generated finale policy should pre-filter completed quests instead of using pre-finale completed boss state."

Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-quest-board"))
$questBoardPreview = Read-QuestBoardPreviewReport
$activePreviewQuests = Convert-ToArray $questBoardPreview.finalActiveQuests
Assert-True ([int]$questBoardPreview.finalActiveQuestCount -eq 1) "Quest board preview should resolve the latest artifact to one finale quest."
Assert-True ($activePreviewQuests[0].questId -eq "plot_darkest_dungeon_1") "Quest board preview should make DD1 the active finale quest."

$dd1CompletedFactsPath = Write-JsonPayload "finale_chain_dd1_completed.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_contract_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            completedQuestIds = @("plot_darkest_dungeon_1")
            lastRaidQuest = [pscustomobject]@{
                names = @("plot_darkest_dungeon_1")
            }
            lastRaidSuccess = $true
        }
        campaignLog = [pscustomobject]@{
            latestCompletedPartyRaidRecord = [pscustomobject]@{
                questId = [pscustomobject]@{
                    names = @("plot_darkest_dungeon_1")
                }
                start = $false
                success = $true
            }
        }
    }
})

Invoke-Loader -LoaderArgs ($baseArgs + @("--materialize-quest-board-policies", "--save-state-report", $dd1CompletedFactsPath, "--quest-board-policy-slots", "1"))
$dd2PolicyMaterialize = Read-QuestBoardPolicyMaterializeReport
Assert-True ([bool]$dd2PolicyMaterialize.succeeded) "Finale policy materialization should succeed after DD1 completion."
Assert-True ($dd2PolicyMaterialize.status -eq "materialized") "Finale policy should materialize DD2 after DD1 completion."
Assert-True (@($dd2PolicyMaterialize.selectedQuestIds) -contains "plot_darkest_dungeon_2") "Finale policy should advance to DD2 after DD1 completion."
Assert-True (-not (@($dd2PolicyMaterialize.selectedQuestIds) -contains "plot_darkest_dungeon_1")) "Finale policy should not keep DD1 after DD1 completion."

Invoke-Loader -LoaderArgs ($baseArgs + @("--preview-quest-board"))
$questBoardPreview = Read-QuestBoardPreviewReport
$activePreviewQuests = Convert-ToArray $questBoardPreview.finalActiveQuests
Assert-True ([int]$questBoardPreview.finalActiveQuestCount -eq 1) "Quest board preview should resolve the latest artifact to one advanced finale quest."
Assert-True ($activePreviewQuests[0].questId -eq "plot_darkest_dungeon_2") "Quest board preview should advance the finale board to DD2."

$postFinaleSelectionPayloadPath = Write-JsonPayload "selection_after_finale.json" ([pscustomobject]@{
    questId = "plot_kill_necromancer_3"
    selectedHeroIds = @("hero_1", "hero_2", "hero_3", "hero_4")
    selectedTrinketIds = @("trinket_1", "trinket_2")
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "quest.selection_confirmed", "--event-payload-file", $postFinaleSelectionPayloadPath))
$state = Read-BossGauntletState
Assert-True ($null -eq $state.activeSelection) "Boss-gauntlet selection lock should not run after darkest_finale unlocks."

$bridgeStateRoot = Join-Path $stateRoot "save_event_bridge"
$bridgeArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $bridgeStateRoot
)

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--init-mod-state"))
Invoke-Loader -LoaderArgs ($bridgeArgs + @("--emit-event", "profile.initialization_requested"))

$necroActiveRaidReportPath = Write-JsonPayload "boss_bridge_necromancer_active_raid.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        raid = [pscustomobject]@{
            instance = [pscustomobject]@{
                id = "plot_kill_necromancer_3"
            }
            party = [pscustomobject]@{
                heroCount = 4
                heroGuids = @(101, 102, 103, 104)
            }
        }
        heroes = @(
            [pscustomobject]@{ id = "101"; trinketIds = @("trinket_necro_1", "trinket_necro_2") },
            [pscustomobject]@{ id = "102"; trinketIds = @("trinket_necro_3", "trinket_necro_4") },
            [pscustomobject]@{ id = "103"; trinketIds = @("trinket_necro_5", "trinket_necro_6") },
            [pscustomobject]@{ id = "104"; trinketIds = @("trinket_necro_7", "trinket_necro_8") }
        )
    }
})

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--infer-save-events", "--save-state-report", $necroActiveRaidReportPath))
$bridgeState = Read-BossGauntletState -Root $bridgeStateRoot
Assert-True ($bridgeState.activeSelection.questId -eq "plot_kill_necromancer_3") "Save bridge should lock the active fixed boss quest."
Assert-True ((Convert-ToArray $bridgeState.activeSelection.heroIds).Count -eq 4) "Save bridge should lock four active-raid heroes."
Assert-True ((Convert-ToArray $bridgeState.activeSelection.trinketIds).Count -eq 8) "Save bridge should lock active-raid trinkets."

$necroSuccessReportPath = Write-JsonPayload "boss_bridge_necromancer_success.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            lastRaidQuestId = 1001
            lastRaidQuest = [pscustomobject]@{
                value = 1001
                isResolved = $true
                isAmbiguous = $false
                names = @("plot_kill_necromancer_3")
            }
            lastRaidSuccess = $true
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 1
        }
    }
})

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--infer-save-events", "--save-state-report", $necroSuccessReportPath))
$bridgeState = Read-BossGauntletState -Root $bridgeStateRoot
Assert-True ((Convert-ToArray $bridgeState.attempts).Count -eq 1) "Save bridge should record the successful boss attempt."
Assert-True ((Convert-ToArray $bridgeState.completedQuestIds) -contains "plot_kill_necromancer_3") "Save bridge should mark the successful fixed boss as completed."
Assert-True ((Convert-ToArray $bridgeState.consumedHeroIds).Count -eq 4) "Save bridge should consume selected heroes after success."
Assert-True ((Convert-ToArray $bridgeState.consumedTrinketIds).Count -eq 8) "Save bridge should consume selected trinkets after success."
Assert-True ([int]$bridgeState.wallet.gold -eq 30000) "Save bridge success should pay the configured victory gold once."
Assert-True ($bridgeState.lastResolvedAttemptId -eq "1") "Save bridge should remember the resolved party raid record id."
Assert-True ($null -eq $bridgeState.activeSelection) "Save bridge success should clear active selection."

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--infer-save-events", "--save-state-report", $necroSuccessReportPath))
$bridgeState = Read-BossGauntletState -Root $bridgeStateRoot
Assert-True ((Convert-ToArray $bridgeState.attempts).Count -eq 1) "Duplicate save bridge success should not record another attempt."
Assert-True ([int]$bridgeState.wallet.gold -eq 30000) "Duplicate save bridge success should not pay again."

$prophetActiveRaidReportPath = Write-JsonPayload "boss_bridge_prophet_active_raid.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        raid = [pscustomobject]@{
            instance = [pscustomobject]@{
                id = "plot_kill_prophet_3"
            }
            party = [pscustomobject]@{
                heroCount = 4
                heroGuids = @(201, 202, 203, 204)
            }
        }
        heroes = @(
            [pscustomobject]@{ id = "201"; trinketIds = @("trinket_prophet_1") },
            [pscustomobject]@{ id = "202"; trinketIds = @("trinket_prophet_2") },
            [pscustomobject]@{ id = "203"; trinketIds = @("trinket_prophet_3") },
            [pscustomobject]@{ id = "204"; trinketIds = @("trinket_prophet_4") }
        )
    }
})

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--infer-save-events", "--save-state-report", $prophetActiveRaidReportPath))
$bridgeState = Read-BossGauntletState -Root $bridgeStateRoot
Assert-True ($bridgeState.activeSelection.questId -eq "plot_kill_prophet_3") "Save bridge should lock the second fixed boss selection."

$prophetFailedReportPath = Write-JsonPayload "boss_bridge_prophet_failed.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            lastRaidQuestId = 1002
            lastRaidQuest = [pscustomobject]@{
                value = 1002
                isResolved = $true
                isAmbiguous = $false
                names = @("plot_kill_prophet_3")
            }
            lastRaidSuccess = $false
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 2
        }
    }
})

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--infer-save-events", "--save-state-report", $prophetFailedReportPath))
$bridgeState = Read-BossGauntletState -Root $bridgeStateRoot
Assert-True ((Convert-ToArray $bridgeState.attempts).Count -eq 2) "Save bridge should record the failed boss attempt."
Assert-True ((Convert-ToArray $bridgeState.consumedHeroIds).Count -eq 8) "Save bridge failure should consume selected heroes."
Assert-True ((Convert-ToArray $bridgeState.consumedTrinketIds).Count -eq 12) "Save bridge failure should consume selected trinkets."
Assert-True (-not ((Convert-ToArray $bridgeState.completedQuestIds) -contains "plot_kill_prophet_3")) "Save bridge failure should not complete the boss quest."
Assert-True ($bridgeState.lastResolvedAttemptId -eq "2") "Save bridge should remember the failed party raid record id."
Assert-True ($null -eq $bridgeState.activeSelection) "Save bridge failure should clear active selection."

$prophetStaleRetryReportPath = Write-JsonPayload "boss_bridge_prophet_retry_with_stale_result.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        raid = [pscustomobject]@{
            instance = [pscustomobject]@{
                id = "plot_kill_prophet_3"
            }
            party = [pscustomobject]@{
                heroCount = 4
                heroGuids = @(301, 302, 303, 304)
            }
        }
        progression = [pscustomobject]@{
            lastRaidQuestId = 1002
            lastRaidQuest = [pscustomobject]@{
                value = 1002
                isResolved = $true
                isAmbiguous = $false
                names = @("plot_kill_prophet_3")
            }
            lastRaidSuccess = $false
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 2
        }
        heroes = @(
            [pscustomobject]@{ id = "301"; trinketIds = @("trinket_retry_1") },
            [pscustomobject]@{ id = "302"; trinketIds = @("trinket_retry_2") },
            [pscustomobject]@{ id = "303"; trinketIds = @("trinket_retry_3") },
            [pscustomobject]@{ id = "304"; trinketIds = @("trinket_retry_4") }
        )
    }
})

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--infer-save-events", "--save-state-report", $prophetStaleRetryReportPath))
$bridgeState = Read-BossGauntletState -Root $bridgeStateRoot
Assert-True ($bridgeState.activeSelection.questId -eq "plot_kill_prophet_3") "Retry selection should stay locked when the save report still contains the previous attempt result."
Assert-True ((Convert-ToArray $bridgeState.activeSelection.heroIds) -contains "301") "Retry selection should keep the newly selected heroes."
Assert-True ((Convert-ToArray $bridgeState.attempts).Count -eq 2) "Stale previous result should not record a duplicate attempt for the retry selection."
Assert-True ((Convert-ToArray $bridgeState.consumedHeroIds).Count -eq 8) "Stale previous result should not consume the retry heroes."

$prophetSuccessReportPath = Write-JsonPayload "boss_bridge_prophet_success.json" ([pscustomobject]@{
    version = 1
    sessionId = "boss_gauntlet_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            lastRaidQuestId = 1002
            lastRaidQuest = [pscustomobject]@{
                value = 1002
                isResolved = $true
                isAmbiguous = $false
                names = @("plot_kill_prophet_3")
            }
            lastRaidSuccess = $true
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 3
        }
    }
})

Invoke-Loader -LoaderArgs ($bridgeArgs + @("--infer-save-events", "--save-state-report", $prophetSuccessReportPath))
$bridgeState = Read-BossGauntletState -Root $bridgeStateRoot
Assert-True ((Convert-ToArray $bridgeState.attempts).Count -eq 3) "Save bridge should record the retry success as a new attempt."
Assert-True ((Convert-ToArray $bridgeState.completedQuestIds) -contains "plot_kill_prophet_3") "Save bridge should complete the second boss after retry success."
Assert-True ($bridgeState.phase -eq "boss_gauntlet") "Save bridge should not unlock darkest_finale until all fixed bosses are completed."
Assert-True ((Convert-ToArray $bridgeState.consumedHeroIds).Count -eq 12) "Pre-finale hero restrictions should remain until all fixed bosses are completed."
Assert-True ((Convert-ToArray $bridgeState.consumedTrinketIds).Count -eq 16) "Pre-finale trinket restrictions should remain until all fixed bosses are completed."
Assert-True ($bridgeState.lastResolvedAttemptId -eq "3") "Save bridge should update the last resolved attempt id after retry success."

Write-Host "PASS: boss gauntlet contract state assertions passed."
