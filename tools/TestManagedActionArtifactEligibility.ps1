param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot "state\managed_action_artifact_eligibility_test\$sessionId"
$artifactRoot = Join-Path $stateRoot "_managed_actions"
$ownerA = [ordered]@{
    pluginId = "validation.managed_action_owner_contract"
    sourcePath = (Resolve-Path -LiteralPath (Join-Path $projectRoot "plugins\_validation\managed_action_owner_contract\patches.json")).Path
}
$ownerB = [ordered]@{
    pluginId = "validation.quest_board_policy_contract"
    sourcePath = (Resolve-Path -LiteralPath (Join-Path $projectRoot "plugins\_validation\quest_board_policy_contract\patches.json")).Path
}
$script:artifactSequence = 0
$script:artifactTimeBase = [DateTime]::UtcNow.AddMinutes(-1)

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

    return Join-Path $projectRoot $Path
}

function New-PolicyRow {
    param(
        [System.Collections.IDictionary]$Owner,
        [string]$PolicyId
    )

    return [ordered]@{
        pluginId = [string]$Owner.pluginId
        sourcePath = [string]$Owner.sourcePath
        policyId = $PolicyId
        mode = "fixed"
        status = "selected"
        selectedQuestIds = @()
    }
}

function Write-FrameworkArtifact {
    param(
        [string]$Name,
        [string]$Status,
        [string]$SourcePath,
        [string]$RuleId,
        [object[]]$Owners,
        [object[]]$Policies,
        [string[]]$QuestIds
    )

    $eventId = if ($Status -eq "materialized") {
        "quest.board.policies.materialized"
    } else {
        "quest.board.policies.empty"
    }
    $artifact = [ordered]@{
        version = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        status = $Status
        eventId = $eventId
        pluginId = "framework.quest_board_policy_materializer"
        sourceName = "Quest Board Policy Materializer"
        sourcePath = $SourcePath
        owners = @($Owners)
        loadOrder = [int]::MaxValue
        ruleIndex = 0
        ruleId = $RuleId
        actionIndex = 0
        action = [ordered]@{
            type = "questBoard.replaceWithFixedSet"
            capability = "quest_board.replace_with_fixed_set"
            risk = "managed"
            required = $false
        }
        payload = [ordered]@{
            source = "questBoardPolicies"
            selectedQuestCount = @($QuestIds).Count
        }
        plan = [ordered]@{
            kind = "questBoard.replaceWithFixedSet"
            effect = "replaceWithFixedSet"
            target = "profile.quest_board"
            source = "questBoardPolicies"
            arguments = [ordered]@{
                target = "profile.quest_board"
                questIds = @($QuestIds)
                removeCompleted = $false
                source = "questBoardPolicies"
                policies = @($Policies)
            }
        }
    }

    $path = Join-Path $artifactRoot $Name
    $artifact | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding UTF8
    $script:artifactSequence++
    [System.IO.File]::SetLastWriteTimeUtc($path, $script:artifactTimeBase.AddSeconds($script:artifactSequence))
    return $path
}

function Find-ArtifactReport {
    param(
        [object]$Report,
        [string]$ArtifactPath
    )

    return @($Report.artifacts | Where-Object {
        [string]$_.artifactPath -eq $ArtifactPath
    })
}

Push-Location $projectRoot
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

    $invalidSupersessionSource = Join-Path $projectRoot "logs\eligibility_invalid_supersession_resolve.json"
    $validOldPath = Write-FrameworkArtifact `
        -Name "001_valid_old.json" `
        -Status "materialized" `
        -SourcePath $invalidSupersessionSource `
        -RuleId "invalid_new_must_not_supersede" `
        -Owners @($ownerA) `
        -Policies @((New-PolicyRow -Owner $ownerA -PolicyId "valid_old")) `
        -QuestIds @("plot_kill_necromancer_3")
    $invalidNewPath = Write-FrameworkArtifact `
        -Name "002_invalid_new_owner_set.json" `
        -Status "materialized" `
        -SourcePath $invalidSupersessionSource `
        -RuleId "invalid_new_must_not_supersede" `
        -Owners @($ownerA) `
        -Policies @((New-PolicyRow -Owner $ownerB -PolicyId "mismatched_policy")) `
        -QuestIds @("plot_darkest_dungeon_4")

    $multiOwnerPath = Write-FrameworkArtifact `
        -Name "003_valid_multi_owner.json" `
        -Status "materialized" `
        -SourcePath (Join-Path $projectRoot "logs\eligibility_multi_owner_resolve.json") `
        -RuleId "valid_multi_owner" `
        -Owners @($ownerA, $ownerB) `
        -Policies @(
            (New-PolicyRow -Owner $ownerA -PolicyId "multi_owner_a"),
            (New-PolicyRow -Owner $ownerB -PolicyId "multi_owner_b")
        ) `
        -QuestIds @("plot_kill_necromancer_3", "plot_kill_prophet_3")

    $inactiveOwner = [ordered]@{
        pluginId = "validation.disabled_managed_action_owner"
        sourcePath = [string]$ownerA.sourcePath
    }
    $inactiveOwnerPath = Write-FrameworkArtifact `
        -Name "004_inactive_owner.json" `
        -Status "materialized" `
        -SourcePath (Join-Path $projectRoot "logs\eligibility_inactive_owner_resolve.json") `
        -RuleId "inactive_owner" `
        -Owners @($inactiveOwner) `
        -Policies @((New-PolicyRow -Owner $inactiveOwner -PolicyId "inactive_owner")) `
        -QuestIds @("plot_darkest_dungeon_3")

    $wrongPathOwner = [ordered]@{
        pluginId = [string]$ownerA.pluginId
        sourcePath = $PSCommandPath
    }
    $wrongSourcePath = Write-FrameworkArtifact `
        -Name "005_wrong_owner_source.json" `
        -Status "materialized" `
        -SourcePath (Join-Path $projectRoot "logs\eligibility_wrong_owner_source_resolve.json") `
        -RuleId "wrong_owner_source" `
        -Owners @($wrongPathOwner) `
        -Policies @((New-PolicyRow -Owner $wrongPathOwner -PolicyId "wrong_owner_source")) `
        -QuestIds @("plot_darkest_dungeon_4")

    $emptySupersessionSource = Join-Path $projectRoot "logs\eligibility_empty_supersession_resolve.json"
    $emptyOldPath = Write-FrameworkArtifact `
        -Name "006_empty_group_old.json" `
        -Status "materialized" `
        -SourcePath $emptySupersessionSource `
        -RuleId "empty_marker_supersedes" `
        -Owners @($ownerA) `
        -Policies @((New-PolicyRow -Owner $ownerA -PolicyId "empty_group_old")) `
        -QuestIds @("plot_kill_prophet_3")
    $emptyMarkerPath = Write-FrameworkArtifact `
        -Name "007_empty_group_marker.json" `
        -Status "empty" `
        -SourcePath $emptySupersessionSource `
        -RuleId "empty_marker_supersedes" `
        -Owners @() `
        -Policies @() `
        -QuestIds @()

    $unknownSupersessionSource = Join-Path $projectRoot "logs\eligibility_unknown_supersession_resolve.json"
    $unknownOldPath = Write-FrameworkArtifact `
        -Name "008_unknown_group_old.json" `
        -Status "materialized" `
        -SourcePath $unknownSupersessionSource `
        -RuleId "unknown_must_not_supersede" `
        -Owners @($ownerA) `
        -Policies @((New-PolicyRow -Owner $ownerA -PolicyId "unknown_group_old")) `
        -QuestIds @("plot_kill_necromancer_3")
    $unknownNewPath = Write-FrameworkArtifact `
        -Name "009_unknown_group_new.json" `
        -Status "unexpected" `
        -SourcePath $unknownSupersessionSource `
        -RuleId "unknown_must_not_supersede" `
        -Owners @() `
        -Policies @() `
        -QuestIds @()

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- `
        --config (Resolve-ProjectPath $ConfigPath) `
        --preview-quest-board `
        --mod-state-dir $stateRoot `
        --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader eligibility preview failed with exit code $LASTEXITCODE"
    }

    $reportPath = Join-Path $projectRoot "logs\quest_board_preview_report.json"
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) "Quest board preview report was not written: $reportPath"
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json

    Assert-True ([bool]$report.succeeded) "Eligibility preview should finish without errors."
    Assert-True ([int]$report.errorCount -eq 0) "Ineligible artifacts should be warnings, not preview errors."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $validOldPath)[0].status -eq "wouldApply") "Invalid newer artifact must not supersede the valid older artifact."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $invalidNewPath)[0].status -eq "ineligible") "Owner/policy mismatch should be ineligible."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $multiOwnerPath)[0].status -eq "wouldApply") "A valid multi-owner framework artifact should remain consumable."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $inactiveOwnerPath)[0].status -eq "ineligible") "An inactive framework owner should be ineligible."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $wrongSourcePath)[0].status -eq "ineligible") "A same-id wrong-path framework owner should be ineligible."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $emptyOldPath)[0].status -eq "ignored") "A valid empty marker should supersede the older materialized artifact."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $emptyMarkerPath)[0].status -eq "ignored") "A valid empty marker should be accepted and then ignored as an empty status."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $unknownOldPath)[0].status -eq "wouldApply") "An unknown newer status must not supersede a valid older artifact."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $unknownNewPath)[0].status -eq "ineligible") "An unknown framework status should be ineligible."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-owner-set-mismatch" })).Count -eq 1) "Expected one owner/policy set mismatch issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-owner-inactive" })).Count -eq 1) "Expected one inactive owner issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-owner-source-mismatch" })).Count -eq 1) "Expected one owner source mismatch issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-framework-contract-invalid" })).Count -eq 1) "Expected one invalid framework contract issue."

    Write-Host "PASS: managed action artifact eligibility and supersession contract."
}
finally {
    Pop-Location
}
