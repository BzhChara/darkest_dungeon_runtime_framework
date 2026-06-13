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

Push-Location $projectRoot.Path
try {
    $previewReportPath = Join-Path $projectRoot.Path "logs\quest_board_policy_preview_report.json"
    Remove-Item -LiteralPath $previewReportPath -Force -ErrorAction SilentlyContinue

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- --config $ConfigPath --validate-only --explain-patches --preview-quest-board-policies --no-inject
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

    Write-Host "PASS: questBoardPolicies validates and expands into a content-backed candidate preview report."
}
finally {
    Pop-Location
}
