param(
    [string]$ConfigPath = "config/rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Write-JsonPayload {
    param(
        [string]$Path,
        [object]$Value
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8
}

Push-Location $projectRoot.Path
try {
    $previewReportPath = Join-Path $projectRoot.Path "logs\quest_board_policy_preview_report.json"
    $resolveReportPath = Join-Path $projectRoot.Path "logs\quest_board_policy_resolve_report.json"
    $materializeReportPath = Join-Path $projectRoot.Path "logs\quest_board_policy_materialize_report.json"
    $questBoardPreviewReportPath = Join-Path $projectRoot.Path "logs\quest_board_preview_report.json"
    $saveEventBridgeReportPath = Join-Path $projectRoot.Path "logs\save_event_bridge_report.json"
    $materializeStateRoot = Join-Path $projectRoot.Path "state\quest_board_policy_contract_materialize"
    $autoMaterializeStateRoot = Join-Path $projectRoot.Path "state\quest_board_policy_contract_auto_materialize"
    $fixtureDir = Join-Path $projectRoot.Path "logs\quest_board_policy_contract_test"
    $policyOnlyConfigPath = Join-Path $fixtureDir "policy_only_config.json"
    Remove-Item -LiteralPath $previewReportPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $resolveReportPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $materializeReportPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $questBoardPreviewReportPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $saveEventBridgeReportPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $materializeStateRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $autoMaterializeStateRoot -Recurse -Force -ErrorAction SilentlyContinue

    $policyOnlyConfig = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
    $policyOnlyConfig.pluginDirectories = @("./plugins/_validation/quest_board_policy_contract")
    Write-JsonPayload $policyOnlyConfigPath $policyOnlyConfig

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --validate-only --explain-patches --preview-quest-board-policies --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }

    $reportPath = Join-Path $projectRoot.Path "state\mod_state\_quest_board_policies\validation.quest_board_policy_contract\001_validation_boss_gates.validation.json"
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) "Quest board policy validation report was not created: $reportPath"

    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    Assert-True ($report.type -eq "questBoardPolicy") "Unexpected report type: $($report.type)"
    Assert-True ($report.pluginId -eq "validation.quest_board_policy_contract") "Unexpected plugin id: $($report.pluginId)"
    Assert-True ($report.id -eq "validation_boss_gates") "Unexpected policy id: $($report.id)"
    Assert-True ($report.mode -eq "mixed") "Unexpected policy mode: $($report.mode)"
    Assert-True ([bool]$report.succeeded) "Quest board policy contract should validate successfully."
    Assert-True ([int]$report.entryCount -eq 2) "Quest board policy should contain two entries."
    Assert-True ([int]$report.fixedEntryCount -eq 1) "Quest board policy should contain one fixed entry."
    Assert-True ([int]$report.randomEntryCount -eq 1) "Quest board policy should contain one random/pool entry."
    Assert-True (@($report.refreshTriggers) -contains "onProfileInitialize") "Policy should include onProfileInitialize trigger."
    Assert-True (@($report.refreshTriggers) -contains "onWeekAdvance") "Policy should include onWeekAdvance trigger."
    Assert-True (@($report.refreshTriggers) -contains "immediateOnQuestComplete") "Policy should include immediateOnQuestComplete trigger."
    Assert-True (@($report.issues).Count -eq 0) "Policy validation should not report issues."

    $firstEntry = @($report.entries)[0]
    $secondEntry = @($report.entries)[1]
    Assert-True ($firstEntry.effectiveQuestId -eq "plot_kill_necromancer_3") "First entry effective quest mismatch."
    Assert-True ([int]$firstEntry.availableWhen.weekGte -eq 5) "First entry week gate mismatch."
    Assert-True ($firstEntry.onCompleted -eq "remove") "First entry completion action mismatch."
    Assert-True ($secondEntry.effectiveQuestId -eq "plot_kill_prophet_3") "Second entry effective quest mismatch."
    Assert-True ($secondEntry.pool -eq "champion_boss_followups") "Second entry pool mismatch."
    Assert-True ([int]$secondEntry.weight -eq 2) "Second entry weight mismatch."
    Assert-True (@($secondEntry.availableWhen.completedQuests) -contains "plot_kill_necromancer_3") "Second entry prerequisite quest mismatch."
    Assert-True ($secondEntry.onCompleted -eq "replace") "Second entry completion action mismatch."

    Assert-True (Test-Path -LiteralPath $previewReportPath -PathType Leaf) "Quest board policy preview report was not created: $previewReportPath"
    $preview = Get-Content -Raw -LiteralPath $previewReportPath | ConvertFrom-Json
    Assert-True ([bool]$preview.succeeded) "Quest board policy preview should succeed."
    Assert-True ([int]$preview.policyCount -eq 1) "Preview should contain one policy."
    Assert-True ([int]$preview.readyPolicyCount -eq 1) "Preview should contain one ready policy."
    Assert-True ([int]$preview.candidateQuestCount -eq 2) "Preview should contain two candidate quests."
    Assert-True ([int]$preview.fixedCandidateCount -eq 1) "Preview should contain one fixed candidate."
    Assert-True ([int]$preview.randomCandidateCount -eq 1) "Preview should contain one random/pool candidate."
    Assert-True ([int]$preview.missingRequiredQuestCount -eq 0) "Preview should not miss required quest content."
    Assert-True ([int]$preview.errorCount -eq 0) "Preview should not report errors."

    $previewPolicy = @($preview.policies)[0]
    Assert-True ($previewPolicy.pluginId -eq "validation.quest_board_policy_contract") "Preview plugin id mismatch."
    Assert-True ($previewPolicy.id -eq "validation_boss_gates") "Preview policy id mismatch."
    Assert-True ($previewPolicy.status -eq "ready") "Preview policy status mismatch: $($previewPolicy.status)"
    $firstCandidate = @($previewPolicy.candidates)[0]
    $secondCandidate = @($previewPolicy.candidates)[1]
    Assert-True ($firstCandidate.effectiveQuestId -eq "plot_kill_necromancer_3") "First preview candidate quest mismatch."
    Assert-True ($firstCandidate.contentStatus -eq "found") "First preview candidate should resolve content."
    Assert-True ($firstCandidate.availabilityStatus -eq "requiresRuntimeFacts") "First preview candidate should require runtime facts."
    Assert-True (-not [string]::IsNullOrWhiteSpace($firstCandidate.content.sourcePath)) "First preview candidate should include source path."
    Assert-True (-not [string]::IsNullOrWhiteSpace($firstCandidate.content.type)) "First preview candidate should include quest type."
    Assert-True (-not [string]::IsNullOrWhiteSpace($firstCandidate.content.dungeon)) "First preview candidate should include dungeon."
    Assert-True ($null -ne $firstCandidate.content.difficulty) "First preview candidate should include difficulty."
    Assert-True ($secondCandidate.pool -eq "champion_boss_followups") "Second preview candidate pool mismatch."
    Assert-True ([int]$secondCandidate.weight -eq 2) "Second preview candidate weight mismatch."
    Assert-True ($secondCandidate.availabilityStatus -eq "requiresRuntimeFacts") "Second preview candidate should require runtime facts."

    $beforeNecromancerReportPath = Join-Path $fixtureDir "policy_week_5_no_completed_quests.json"
    Write-JsonPayload $beforeNecromancerReportPath ([pscustomobject]@{
        version = 1
        sessionId = "quest_board_policy_contract"
        generatedAt = [DateTimeOffset]::Now
        parseStatus = "fixture"
        facts = [pscustomobject]@{
            campaignLog = [pscustomobject]@{
                totalWeeks = 5
            }
            progression = [pscustomobject]@{
                completedQuestIds = @()
            }
        }
    })

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --resolve-quest-board-policies --save-state-report $beforeNecromancerReportPath --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader policy resolve failed with exit code $LASTEXITCODE"
    }

    Assert-True (Test-Path -LiteralPath $resolveReportPath -PathType Leaf) "Quest board policy resolve report was not created: $resolveReportPath"
    $resolve = Get-Content -Raw -LiteralPath $resolveReportPath | ConvertFrom-Json
    Assert-True ([bool]$resolve.succeeded) "Quest board policy resolve should succeed before any completed quest."
    Assert-True ([int]$resolve.week -eq 5) "Resolve report should read week 5 from save facts."
    Assert-True ([int]$resolve.resolvedQuestCount -eq 1) "Week 5 resolve should produce one quest."
    Assert-True (@($resolve.resolvedQuestIds) -contains "plot_kill_necromancer_3") "Week 5 resolve should include necromancer."
    Assert-True (-not (@($resolve.resolvedQuestIds) -contains "plot_kill_prophet_3")) "Week 5 resolve should not include prophet."
    $resolvePolicy = @($resolve.policies)[0]
    Assert-True ($resolvePolicy.status -eq "resolved") "Week 5 policy should resolve."
    $resolvedFirst = @($resolvePolicy.candidates)[0]
    $resolvedSecond = @($resolvePolicy.candidates)[1]
    Assert-True ($resolvedFirst.resolutionStatus -eq "active") "Necromancer should be active at week 5."
    Assert-True ($resolvedSecond.resolutionStatus -eq "skipped") "Prophet should be skipped before necromancer is completed."
    Assert-True ($resolvedSecond.predicateStatus -eq "predicateNotMatched") "Prophet should be skipped by predicate."

    $afterNecromancerReportPath = Join-Path $fixtureDir "policy_week_6_necromancer_completed.json"
    $afterNecromancerProfile3ReportPath = Join-Path $fixtureDir "policy_week_6_necromancer_completed_profile_3.json"
    Write-JsonPayload $afterNecromancerReportPath ([pscustomobject]@{
        version = 1
        sessionId = "quest_board_policy_contract"
        generatedAt = [DateTimeOffset]::Now
        parseStatus = "fixture"
        facts = [pscustomobject]@{
            campaignLog = [pscustomobject]@{
                totalWeeks = 6
                latestCompletedPartyRaidRecord = [pscustomobject]@{
                    questId = [pscustomobject]@{
                        names = @("plot_kill_necromancer_3")
                    }
                    start = $false
                    success = $true
                }
            }
            progression = [pscustomobject]@{
                lastRaidQuest = [pscustomobject]@{
                    names = @("plot_kill_necromancer_3")
                }
                lastRaidSuccess = $true
            }
        }
    })
    $profileScopedReport = Get-Content -Raw -LiteralPath $afterNecromancerReportPath | ConvertFrom-Json
    $profileScopedReport | Add-Member -NotePropertyName activeProfile -NotePropertyValue ([pscustomobject]@{
        profile = "profile_3"
        root = "E:\Steam\userdata\1097809614\262060\remote\profile_3"
        confidence = "fixture"
        score = 100
        reasons = @("quest board policy contract fixture")
        alternatives = @()
    })
    Write-JsonPayload $afterNecromancerProfile3ReportPath $profileScopedReport

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --resolve-quest-board-policies --save-state-report $afterNecromancerReportPath --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader policy resolve after completed quest failed with exit code $LASTEXITCODE"
    }

    $resolve = Get-Content -Raw -LiteralPath $resolveReportPath | ConvertFrom-Json
    Assert-True ([bool]$resolve.succeeded) "Quest board policy resolve should succeed after necromancer completion."
    Assert-True ([int]$resolve.week -eq 6) "Resolve report should read week 6 from save facts."
    Assert-True (@($resolve.completedQuestIds) -contains "plot_kill_necromancer_3") "Resolve report should read completed necromancer."
    Assert-True ([int]$resolve.resolvedQuestCount -eq 1) "Week 6 resolve should produce one quest."
    Assert-True (-not (@($resolve.resolvedQuestIds) -contains "plot_kill_necromancer_3")) "Completed necromancer should be removed by onCompleted=remove."
    Assert-True (@($resolve.resolvedQuestIds) -contains "plot_kill_prophet_3") "Week 6 resolve should include prophet."
    $resolvePolicy = @($resolve.policies)[0]
    $resolvedFirst = @($resolvePolicy.candidates)[0]
    $resolvedSecond = @($resolvePolicy.candidates)[1]
    Assert-True ($resolvedFirst.resolutionStatus -eq "skipped") "Completed necromancer should be skipped."
    Assert-True ($resolvedFirst.predicateStatus -eq "completedActionFiltered") "Completed necromancer should be filtered by completion action."
    Assert-True ($resolvedSecond.resolutionStatus -eq "eligiblePoolCandidate") "Prophet should be an eligible pool candidate after necromancer completion."
    Assert-True ($resolvedSecond.predicateStatus -eq "matched") "Prophet predicate should match after necromancer completion."

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --materialize-quest-board-policies --save-state-report $afterNecromancerReportPath --quest-board-policy-slots 1 --quest-board-policy-seed 42 --mod-state-dir $materializeStateRoot --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader policy materialize failed with exit code $LASTEXITCODE"
    }

    Assert-True (Test-Path -LiteralPath $materializeReportPath -PathType Leaf) "Quest board policy materialize report was not created: $materializeReportPath"
    $materialize = Get-Content -Raw -LiteralPath $materializeReportPath | ConvertFrom-Json
    Assert-True ([bool]$materialize.succeeded) "Quest board policy materialize should succeed."
    Assert-True ($materialize.status -eq "materialized") "Materialize report status mismatch: $($materialize.status)"
    Assert-True ([int]$materialize.seed -eq 42) "Materialize report seed mismatch."
    Assert-True ([int]$materialize.slotLimit -eq 1) "Materialize report slot limit mismatch."
    Assert-True ([int]$materialize.selectedQuestCount -eq 1) "Materialize should select one quest."
    Assert-True (@($materialize.selectedQuestIds) -contains "plot_kill_prophet_3") "Materialize should select prophet."
    Assert-True (-not [string]::IsNullOrWhiteSpace($materialize.artifactPath)) "Materialize report should include artifact path."
    Assert-True (Test-Path -LiteralPath $materialize.artifactPath -PathType Leaf) "Materialized quest-board artifact was not written: $($materialize.artifactPath)"

    $artifact = Get-Content -Raw -LiteralPath $materialize.artifactPath | ConvertFrom-Json
    Assert-True ($artifact.action.type -eq "questBoard.replaceWithFixedSet") "Materialized artifact action type mismatch."
    Assert-True ($artifact.plan.kind -eq "questBoard.replaceWithFixedSet") "Materialized artifact plan kind mismatch."
    Assert-True (-not [bool]$artifact.plan.arguments.removeCompleted) "Policy materializer should pre-filter completed quests instead of delegating removeCompleted."
    Assert-True (@($artifact.plan.arguments.questIds) -contains "plot_kill_prophet_3") "Materialized artifact should contain prophet."

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --preview-quest-board --mod-state-dir $materializeStateRoot --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader quest board preview failed with exit code $LASTEXITCODE"
    }

    Assert-True (Test-Path -LiteralPath $questBoardPreviewReportPath -PathType Leaf) "Quest board preview report was not created: $questBoardPreviewReportPath"
    $questBoardPreview = Get-Content -Raw -LiteralPath $questBoardPreviewReportPath | ConvertFrom-Json
    Assert-True ([bool]$questBoardPreview.succeeded) "Quest board preview should consume materialized policy artifact."
    $policyPreviewArtifact = @($questBoardPreview.artifacts) | Where-Object { $_.artifactPath -eq $materialize.artifactPath }
    Assert-True (@($policyPreviewArtifact).Count -eq 1) "Quest board preview should include the materialized policy artifact."
    Assert-True ($policyPreviewArtifact.status -eq "wouldApply") "Materialized policy artifact should be consumable by quest board preview."
    Assert-True ([int]$policyPreviewArtifact.activeQuestCount -eq 1) "Materialized policy artifact should expose one active quest."
    Assert-True (@($policyPreviewArtifact.activeQuests.questId) -contains "plot_kill_prophet_3") "Materialized policy artifact should expose prophet."

    Remove-Item -LiteralPath $materializeReportPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $questBoardPreviewReportPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $saveEventBridgeReportPath -Force -ErrorAction SilentlyContinue

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --infer-save-events --auto-materialize-quest-board-policies --save-state-report $afterNecromancerProfile3ReportPath --quest-board-policy-slots 1 --quest-board-policy-seed 42 --mod-state-dir $autoMaterializeStateRoot --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader save event bridge auto materialize failed with exit code $LASTEXITCODE"
    }

    Assert-True (Test-Path -LiteralPath $saveEventBridgeReportPath -PathType Leaf) "Save event bridge report was not created: $saveEventBridgeReportPath"
    $bridge = Get-Content -Raw -LiteralPath $saveEventBridgeReportPath | ConvertFrom-Json
    Assert-True ([bool]$bridge.succeeded) "Save event bridge should succeed with policy auto materialization."
    Assert-True ([bool]$bridge.questBoardPolicyMaterialization.enabled) "Save event bridge should report enabled policy auto materialization."
    Assert-True ($bridge.questBoardPolicyMaterialization.status -eq "materialized") "Auto materialization status mismatch: $($bridge.questBoardPolicyMaterialization.status)"
    Assert-True ($bridge.questBoardPolicyMaterialization.profileScope.kind -eq "profile") "Auto materialization should report profile scope."
    Assert-True ($bridge.questBoardPolicyMaterialization.profileScope.profileId -eq "profile_3") "Auto materialization profile scope mismatch."
    Assert-True ([int]$bridge.questBoardPolicyMaterialization.selectedQuestCount -eq 1) "Auto materialization should select one quest."
    Assert-True (-not [string]::IsNullOrWhiteSpace($bridge.questBoardPolicyMaterialization.artifactPath)) "Auto materialization should report artifact path."
    Assert-True (Test-Path -LiteralPath $bridge.questBoardPolicyMaterialization.artifactPath -PathType Leaf) "Auto materialization artifact was not written: $($bridge.questBoardPolicyMaterialization.artifactPath)"

    $autoMaterialize = Get-Content -Raw -LiteralPath $materializeReportPath | ConvertFrom-Json
    Assert-True ([bool]$autoMaterialize.succeeded) "Auto materialize report should succeed."
    Assert-True ($autoMaterialize.profileScope.profileId -eq "profile_3") "Auto materialize report should keep profile scope."
    Assert-True (@($autoMaterialize.selectedQuestIds) -contains "plot_kill_prophet_3") "Auto materialize should select prophet."
    $autoArtifact = Get-Content -Raw -LiteralPath $bridge.questBoardPolicyMaterialization.artifactPath | ConvertFrom-Json
    Assert-True ($autoArtifact.profileScope.profileId -eq "profile_3") "Auto materialized artifact should keep profile scope."

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --preview-quest-board --mod-state-dir $autoMaterializeStateRoot --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader quest board preview after auto materialize failed with exit code $LASTEXITCODE"
    }

    $autoQuestBoardPreview = Get-Content -Raw -LiteralPath $questBoardPreviewReportPath | ConvertFrom-Json
    Assert-True ([bool]$autoQuestBoardPreview.succeeded) "Quest board preview should succeed when ignoring unmatched profile-scoped policy artifact."
    $autoPolicyPreviewArtifact = @($autoQuestBoardPreview.artifacts) | Where-Object { $_.artifactPath -eq $bridge.questBoardPolicyMaterialization.artifactPath }
    Assert-True (@($autoPolicyPreviewArtifact).Count -eq 1) "Quest board preview should include the auto materialized policy artifact."
    Assert-True ($autoPolicyPreviewArtifact.status -eq "ignored") "Unscoped quest board preview should ignore profile-scoped policy artifact."

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $policyOnlyConfigPath --preview-quest-board --quest-board-profile-scope profile_3 --mod-state-dir $autoMaterializeStateRoot --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader scoped quest board preview after auto materialize failed with exit code $LASTEXITCODE"
    }

    $autoQuestBoardPreview = Get-Content -Raw -LiteralPath $questBoardPreviewReportPath | ConvertFrom-Json
    Assert-True ([bool]$autoQuestBoardPreview.succeeded) "Scoped quest board preview should consume auto materialized policy artifact."
    Assert-True ($autoQuestBoardPreview.targetProfileId -eq "profile_3") "Scoped quest board preview target profile mismatch."
    $autoPolicyPreviewArtifact = @($autoQuestBoardPreview.artifacts) | Where-Object { $_.artifactPath -eq $bridge.questBoardPolicyMaterialization.artifactPath }
    Assert-True (@($autoPolicyPreviewArtifact).Count -eq 1) "Scoped quest board preview should include the auto materialized policy artifact."
    Assert-True ($autoPolicyPreviewArtifact.status -eq "wouldApply") "Auto materialized policy artifact should be consumable by quest board preview."
    Assert-True ($autoPolicyPreviewArtifact.profileScopeProfileId -eq "profile_3") "Auto materialized policy artifact preview should expose profile scope."
    Assert-True (@($autoPolicyPreviewArtifact.activeQuests.questId) -contains "plot_kill_prophet_3") "Auto materialized policy artifact should expose prophet."

    Write-Host "PASS: questBoardPolicies validates, previews, resolves, materializes, auto-materializes from save facts, and feeds the existing quest-board artifact consumer."
}
finally {
    Pop-Location
}
