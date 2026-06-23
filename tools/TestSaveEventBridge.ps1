param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\save_event_bridge_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\save_event_bridge_test\$sessionId"
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

function Invoke-LoaderExpectExit {
    param(
        [string[]]$LoaderArgs,
        [int]$ExpectedExitCode
    )

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        throw "DDRuntimeLoader exit code $LASTEXITCODE did not match expected $ExpectedExitCode"
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

function Get-DsonHashSigned {
    param([string]$Value)

    [Int64]$hash = 0
    foreach ($byte in [System.Text.Encoding]::UTF8.GetBytes($Value)) {
        $hash = (($hash * 53) + $byte) % 4294967296
    }

    if ($hash -ge 2147483648) {
        return [int]($hash - 4294967296)
    }

    return [int]$hash
}

function Read-BossGauntletState {
    param([string]$Root = $stateRoot)

    $path = Join-Path $Root "$pluginId.json"
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
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "profile.initialization_requested"))

$questId = "plot_kill_necromancer_3"
$questHash = Get-DsonHashSigned $questId
$raidStartedReportPath = Write-JsonPayload "save_state_report_necromancer_raid_started.json" ([pscustomobject]@{
    version = 1
    sessionId = "save_event_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        raid = [pscustomobject]@{
            instance = [pscustomobject]@{
                id = $questId
            }
            party = [pscustomobject]@{
                heroCount = 4
                heroGuids = @(101, 102, 103, 104)
            }
        }
        heroes = @(
            [pscustomobject]@{
                id = "101"
                trinketIds = @("trinket_necro_1", "trinket_necro_2")
            },
            [pscustomobject]@{
                id = "102"
                trinketIds = @("trinket_necro_3", "trinket_necro_4")
            },
            [pscustomobject]@{
                id = "103"
                trinketIds = @("trinket_necro_5", "trinket_necro_6")
            },
            [pscustomobject]@{
                id = "104"
                trinketIds = @("trinket_necro_7", "trinket_necro_8")
            }
        )
    }
})

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $raidStartedReportPath))

$state = Read-BossGauntletState
Assert-True ($null -ne $state.activeSelection) "Save event bridge should lock inferred active boss selection."
$lockedSelection = $state.activeSelection
Assert-True ($lockedSelection.questId -eq $questId) "Save event bridge should infer selected boss quest id."
$lockedHeroIds = Convert-ToArray $lockedSelection.heroIds
$lockedTrinketIds = Convert-ToArray $lockedSelection.trinketIds
Assert-True ($lockedHeroIds.Count -eq 4) "Save event bridge should infer four selected heroes."
Assert-True ($lockedTrinketIds.Count -eq 8) "Save event bridge should infer eight selected trinkets."
Assert-True ($lockedHeroIds -contains "101") "Save event bridge should coerce selected hero ids to strings."
Assert-True ($lockedTrinketIds -contains "trinket_necro_1") "Save event bridge should infer selected trinket ids."

$bridgeReportPath = Join-Path $projectRoot.Path "logs\save_event_bridge_report.json"
$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
Assert-True ([int]$bridgeReport.inferredEventCount -eq 1) "Save event bridge should infer exactly one selection event."
$executed = @(Convert-ToArray $bridgeReport.plugins | Where-Object { $_.status -eq "event-executed" })
Assert-True ($executed.Count -eq 1) "Save event bridge should execute one matching selection event."
Assert-True ($executed[0].eventId -eq "quest.selection_confirmed") "Save event bridge should emit quest.selection_confirmed."

$saveReportPath = Write-JsonPayload "save_state_report_necromancer_completed.json" ([pscustomobject]@{
    version = 1
    sessionId = "save_event_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            lastRaidQuestId = $questHash
            lastRaidQuest = [pscustomobject]@{
                value = $questHash
                isResolved = $true
                isAmbiguous = $false
                names = @($questId)
            }
            lastRaidSuccess = $true
            lastRaidWasPlotQuest = $true
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 1
        }
    }
})

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $saveReportPath))

$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 1) "Save event bridge should record one successful boss attempt."
Assert-True ((Convert-ToArray $state.completedQuestIds) -contains $questId) "Save event bridge should record completed boss quest id."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 4) "Save event bridge should consume locked heroes."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 8) "Save event bridge should consume locked trinkets."
Assert-True ([int]$state.wallet.gold -eq 30000) "Save event bridge success should pay the configured victory gold once."
Assert-True ($state.lastResolvedAttemptId -eq "1") "Save event bridge should remember the resolved party raid record id."
Assert-True ($null -eq $state.activeSelection) "Save event bridge completion should clear locked selection."

$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
Assert-True ([int]$bridgeReport.inferredEventCount -eq 1) "Save event bridge should infer exactly one attempt event."
$executed = @(Convert-ToArray $bridgeReport.plugins | Where-Object { $_.status -eq "event-executed" })
Assert-True ($executed.Count -eq 1) "Save event bridge should execute one matching attempt event."
Assert-True ($executed[0].eventId -eq "quest.attempt_resolved") "Save event bridge should emit quest.attempt_resolved."

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $saveReportPath))

$stateAfterNoMatch = Read-BossGauntletState
Assert-True ((Convert-ToArray $stateAfterNoMatch.attempts).Count -eq 1) "No-match save event bridge pass should leave attempts unchanged."
Assert-True ([int]$stateAfterNoMatch.wallet.gold -eq 30000) "No-match save event bridge pass should not pay a duplicate reward."
$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
Assert-True ([int]$bridgeReport.inferredEventCount -eq 0) "No-match save event bridge pass should not infer an event."
$notMatchedRules = @(Convert-ToArray $bridgeReport.plugins | Where-Object {
    $_.pluginId -eq $pluginId -and $_.status -eq "predicate-not-matched"
})
Assert-True ($notMatchedRules.Count -eq 3) "No-match save event bridge pass should leave all boss gauntlet fact-event rules unmatched."

$failedQuestId = "plot_kill_prophet_3"
$failedQuestHash = Get-DsonHashSigned $failedQuestId
$failedRaidStartedReportPath = Write-JsonPayload "save_state_report_prophet_raid_started.json" ([pscustomobject]@{
    version = 1
    sessionId = "save_event_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        raid = [pscustomobject]@{
            instance = [pscustomobject]@{
                id = $failedQuestId
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

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $failedRaidStartedReportPath))

$state = Read-BossGauntletState
Assert-True ($state.activeSelection.questId -eq $failedQuestId) "Save event bridge should lock the failed boss selection before resolution."

$failedReportPath = Write-JsonPayload "save_state_report_prophet_failed.json" ([pscustomobject]@{
    version = 1
    sessionId = "save_event_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            lastRaidQuestId = $failedQuestHash
            lastRaidQuest = [pscustomobject]@{
                value = $failedQuestHash
                isResolved = $true
                isAmbiguous = $false
                names = @($failedQuestId)
            }
            lastRaidSuccess = $false
            lastRaidWasPlotQuest = $true
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 2
        }
    }
})

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $failedReportPath))

$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 2) "Save event bridge failure should record one failed boss attempt."
Assert-True (-not ((Convert-ToArray $state.completedQuestIds) -contains $failedQuestId)) "Save event bridge failure should not complete the boss quest."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 8) "Save event bridge failure should consume locked heroes."
Assert-True ((Convert-ToArray $state.consumedTrinketIds).Count -eq 12) "Save event bridge failure should consume locked trinkets."
Assert-True ([int]$state.wallet.gold -eq 30000) "Save event bridge failure should not pay victory gold."
Assert-True ($state.lastResolvedAttemptId -eq "2") "Save event bridge failure should remember the failed party raid record id."
Assert-True ($null -eq $state.activeSelection) "Save event bridge failure should clear locked selection."

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $failedReportPath))

$state = Read-BossGauntletState
Assert-True ((Convert-ToArray $state.attempts).Count -eq 2) "Duplicate save bridge failure should not record a duplicate attempt."
Assert-True ((Convert-ToArray $state.consumedHeroIds).Count -eq 8) "Duplicate save bridge failure should not consume heroes again."

$postTaskStateRoot = Join-Path $stateRoot "post_task_campaign_log"
New-Item -ItemType Directory -Force -Path $postTaskStateRoot | Out-Null
$postTaskArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $postTaskStateRoot
)

Invoke-Loader -LoaderArgs ($postTaskArgs + @("--init-mod-state"))
Invoke-Loader -LoaderArgs ($postTaskArgs + @("--emit-event", "profile.initialization_requested"))

$campaignLogCompletedReportPath = Write-JsonPayload "save_state_report_necromancer_campaign_log_completed.json" ([pscustomobject]@{
    version = 1
    sessionId = "save_event_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            lastRaidQuestId = $questHash
            lastRaidQuest = [pscustomobject]@{
                value = $questHash
                isResolved = $true
                isAmbiguous = $false
                names = @($questId)
            }
            lastRaidSuccess = $true
            lastRaidWasPlotQuest = $true
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 1
            completedPartyRaidRecordCount = 1
            latestCompletedPartyRaidRecord = [pscustomobject]@{
                chapterSlotId = "2"
                chapterIndex = 2
                entrySlotId = "0"
                questIdHash = $questHash
                questId = [pscustomobject]@{
                    value = $questHash
                    isResolved = $true
                    isAmbiguous = $false
                    names = @($questId)
                }
                start = $false
                success = $true
                heroCount = 4
                heroGuids = @(1, 2, 7, 8)
                heroes = @(
                    [pscustomobject]@{ slotId = "0"; guid = 1; name = "Reynauld" },
                    [pscustomobject]@{ slotId = "1"; guid = 2; name = "Dismas" },
                    [pscustomobject]@{ slotId = "2"; guid = 7; name = "Junia" },
                    [pscustomobject]@{ slotId = "3"; guid = 8; name = "Paracelsus" }
                )
            }
            partyRaidRecords = @(
                [pscustomobject]@{
                    chapterSlotId = "2"
                    chapterIndex = 2
                    entrySlotId = "0"
                    questIdHash = $questHash
                    questId = [pscustomobject]@{
                        value = $questHash
                        isResolved = $true
                        isAmbiguous = $false
                        names = @($questId)
                    }
                    start = $false
                    success = $true
                    heroCount = 4
                    heroGuids = @(1, 2, 7, 8)
                    heroes = @(
                        [pscustomobject]@{ slotId = "0"; guid = 1; name = "Reynauld" },
                        [pscustomobject]@{ slotId = "1"; guid = 2; name = "Dismas" },
                        [pscustomobject]@{ slotId = "2"; guid = 7; name = "Junia" },
                        [pscustomobject]@{ slotId = "3"; guid = 8; name = "Paracelsus" }
                    )
                }
            )
        }
        heroes = @(
            [pscustomobject]@{
                id = "1"
                trinketIds = @("berserk_mask", "immunity_mask")
            },
            [pscustomobject]@{
                id = "2"
                trinketIds = @("fortunate_armlet", "sb_4")
            },
            [pscustomobject]@{
                id = "7"
                trinketIds = @("sb_3", "sb_2")
            },
            [pscustomobject]@{
                id = "8"
                trinketIds = @("sb_1", "bleeding_pendant")
            }
        )
    }
})

Invoke-Loader -LoaderArgs ($postTaskArgs + @("--infer-save-events", "--save-state-report", $campaignLogCompletedReportPath))

$postTaskState = Read-BossGauntletState -Root $postTaskStateRoot
Assert-True ((Convert-ToArray $postTaskState.attempts).Count -eq 1) "Post-task campaign log bridge should record one resolved attempt in one pass."
Assert-True ((Convert-ToArray $postTaskState.completedQuestIds) -contains $questId) "Post-task campaign log bridge should complete the inferred boss quest."
Assert-True ((Convert-ToArray $postTaskState.consumedHeroIds).Count -eq 4) "Post-task campaign log bridge should consume inferred heroes."
Assert-True ((Convert-ToArray $postTaskState.consumedTrinketIds).Count -eq 8) "Post-task campaign log bridge should consume inferred trinkets."
Assert-True ([int]$postTaskState.wallet.gold -eq 30000) "Post-task campaign log bridge should pay victory gold once."
Assert-True ($null -eq $postTaskState.activeSelection) "Post-task campaign log completion should clear inferred locked selection."

$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
Assert-True ([int]$bridgeReport.inferredEventCount -eq 2) "Post-task campaign log bridge should infer selection and attempt resolution in one pass."
$executed = @(Convert-ToArray $bridgeReport.plugins | Where-Object { $_.status -eq "event-executed" })
Assert-True ($executed.Count -eq 2) "Post-task campaign log bridge should execute two matching plugin events."
Assert-True (($executed | Where-Object { $_.eventId -eq "quest.selection_confirmed" }).Count -eq 1) "Post-task campaign log bridge should emit quest.selection_confirmed."
Assert-True (($executed | Where-Object { $_.eventId -eq "quest.attempt_resolved" }).Count -eq 1) "Post-task campaign log bridge should emit quest.attempt_resolved."

$postTaskNoTrinketStateRoot = Join-Path $stateRoot "post_task_campaign_log_without_trinkets"
New-Item -ItemType Directory -Force -Path $postTaskNoTrinketStateRoot | Out-Null
$postTaskNoTrinketArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $postTaskNoTrinketStateRoot
)

Invoke-Loader -LoaderArgs ($postTaskNoTrinketArgs + @("--init-mod-state"))
Invoke-Loader -LoaderArgs ($postTaskNoTrinketArgs + @("--emit-event", "profile.initialization_requested"))

$campaignLogCompletedNoTrinketsReportPath = Write-JsonPayload "save_state_report_necromancer_campaign_log_completed_without_trinkets.json" ([pscustomobject]@{
    version = 1
    sessionId = "save_event_bridge_test"
    generatedAt = [DateTimeOffset]::Now
    parseStatus = "fixture"
    facts = [pscustomobject]@{
        progression = [pscustomobject]@{
            lastRaidQuestId = $questHash
            lastRaidQuest = [pscustomobject]@{
                value = $questHash
                isResolved = $true
                isAmbiguous = $false
                names = @($questId)
            }
            lastRaidSuccess = $true
            lastRaidWasPlotQuest = $true
        }
        campaignLog = [pscustomobject]@{
            partyRaidRecordCount = 1
            completedPartyRaidRecordCount = 1
            latestCompletedPartyRaidRecord = [pscustomobject]@{
                questId = [pscustomobject]@{
                    value = $questHash
                    isResolved = $true
                    isAmbiguous = $false
                    names = @($questId)
                }
                start = $false
                success = $true
                heroCount = 4
                heroGuids = @(1, 2, 7, 8)
            }
            partyRaidRecords = @(
                [pscustomobject]@{
                    questId = [pscustomobject]@{
                        value = $questHash
                        isResolved = $true
                        isAmbiguous = $false
                        names = @($questId)
                    }
                    start = $false
                    success = $true
                    heroCount = 4
                    heroGuids = @(1, 2, 7, 8)
                }
            )
        }
        heroes = @(
            [pscustomobject]@{ id = "1" },
            [pscustomobject]@{ id = "2" },
            [pscustomobject]@{ id = "7" },
            [pscustomobject]@{ id = "8" }
        )
    }
})

Invoke-Loader -LoaderArgs ($postTaskNoTrinketArgs + @("--infer-save-events", "--save-state-report", $campaignLogCompletedNoTrinketsReportPath))

$postTaskNoTrinketState = Read-BossGauntletState -Root $postTaskNoTrinketStateRoot
Assert-True ((Convert-ToArray $postTaskNoTrinketState.completedQuestIds) -contains $questId) "Post-task campaign log bridge should complete even when selected heroes have no trinketIds."
Assert-True ((Convert-ToArray $postTaskNoTrinketState.consumedHeroIds).Count -eq 4) "Post-task campaign log bridge should still consume inferred heroes without trinkets."
Assert-True ((Convert-ToArray $postTaskNoTrinketState.consumedTrinketIds).Count -eq 0) "Post-task campaign log bridge should treat missing trinketIds as an empty optional selection."

$uninitializedStateRoot = Join-Path $stateRoot "uninitialized_boss_gauntlet_state"
New-Item -ItemType Directory -Force -Path $uninitializedStateRoot | Out-Null

Invoke-Loader -LoaderArgs @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $uninitializedStateRoot,
    "--infer-save-events",
    "--save-state-report", $saveReportPath
)

$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
Assert-True ([int]$bridgeReport.inferredEventCount -eq 0) "Uninitialized boss gauntlet state should not infer save bridge events."
$bridgeErrors = @(Convert-ToArray $bridgeReport.issues | Where-Object { $_.severity -eq "error" })
Assert-True ($bridgeErrors.Count -eq 0) "Uninitialized boss gauntlet state should not produce save bridge errors."

$badStateRoot = Join-Path $stateRoot "bad_fact_event_predicate"
New-Item -ItemType Directory -Force -Path $badStateRoot | Out-Null

Invoke-LoaderExpectExit -ExpectedExitCode 3 -LoaderArgs @(
    "--config", (Resolve-ProjectPath "config\save_event_bridge_bad_fact_event_config.json"),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-dir", $badStateRoot,
    "--infer-save-events",
    "--save-state-report", $saveReportPath
)

$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
$predicateFailed = @(Convert-ToArray $bridgeReport.plugins | Where-Object { $_.status -eq "predicate-failed" })
Assert-True ($predicateFailed.Count -eq 1) "Bad fact-event predicate should be reported as predicate-failed."
$predicateIssues = @(Convert-ToArray $bridgeReport.issues | Where-Object { $_.code -eq "predicate-evaluation-failed" -and $_.severity -eq "error" })
Assert-True ($predicateIssues.Count -eq 1) "Bad fact-event predicate should produce one error issue."

$badPayloadRoot = Join-Path $stateRoot "bad_payload_projection"
New-Item -ItemType Directory -Force -Path $badPayloadRoot | Out-Null

Invoke-LoaderExpectExit -ExpectedExitCode 3 -LoaderArgs @(
    "--config", (Resolve-ProjectPath "config\save_event_bridge_bad_payload_projection_config.json"),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-dir", $badPayloadRoot,
    "--infer-save-events",
    "--save-state-report", $saveReportPath
)

$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
$payloadFailed = @(Convert-ToArray $bridgeReport.plugins | Where-Object { $_.status -eq "payload-failed" })
Assert-True ($payloadFailed.Count -eq 8) "Bad payload projection rules should be reported as payload-failed."
$payloadIssues = @(Convert-ToArray $bridgeReport.issues | Where-Object { $_.code -eq "payload-build-failed" -and $_.severity -eq "error" })
Assert-True ($payloadIssues.Count -eq 8) "Bad payload projection rules should produce payload-build-failed issues."
Assert-True ([int]$bridgeReport.inferredEventCount -eq 0) "Bad payload projection rules should not emit events."

Write-Host "PASS: save event bridge inferred and executed boss gauntlet selection/attempt events."
