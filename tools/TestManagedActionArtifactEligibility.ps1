param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ManagedActionProducerTestHelpers.ps1")

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot "state\managed_action_artifact_eligibility_test\$sessionId"
$artifactRoot = Join-Path $stateRoot "_managed_actions"
$ownerA = [ordered]@{
    pluginId = "validation.boss_gauntlet_campaign_contract"
    sourcePath = (Resolve-Path -LiteralPath (Join-Path $projectRoot "plugins\_validation\boss_gauntlet_campaign_contract\patches.json")).Path
}
$ownerB = [ordered]@{
    pluginId = "validation.quest_board_policy_contract"
    sourcePath = (Resolve-Path -LiteralPath (Join-Path $projectRoot "plugins\_validation\quest_board_policy_contract\patches.json")).Path
}
$script:artifactSequence = 0
$script:artifactTimeBase = [DateTime]::UtcNow.AddMinutes(-1)
$oldVersionPath = $null

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
        [string]$PolicyId,
        [int]$RuleIndex,
        [string]$Mode
    )

    return [ordered]@{
        pluginId = [string]$Owner.pluginId
        sourcePath = [string]$Owner.sourcePath
        ruleIndex = $RuleIndex
        policyId = $PolicyId
        mode = $Mode
        status = "selected"
        selectedQuestIds = @()
    }
}

function New-ActivePolicyRows {
    return @(
        (New-PolicyRow -Owner $ownerA -PolicyId "boss_gauntlet_darkest_finale_chain.linear_progression" -RuleIndex 1 -Mode "fixed"),
        (New-PolicyRow -Owner $ownerB -PolicyId "validation_boss_gates" -RuleIndex 1 -Mode "mixed")
    )
}

function Write-FrameworkArtifact {
    param(
        [string]$Name,
        [string]$Status,
        [string]$SourcePath,
        [string]$RuleId,
        [string]$ProfileId,
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
        version = 2
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
        profileScope = [ordered]@{
            kind = "profile"
            profileId = $ProfileId
            profileRoot = "E:\Steam\userdata\fixture\262060\remote\$ProfileId"
            source = "managed action eligibility fixture"
        }
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

    Add-ManagedActionTestProducer -Artifact $artifact -Producer $script:frameworkProducer | Out-Null

    $path = Join-Path $artifactRoot $Name
    $artifact | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding UTF8
    $script:artifactSequence++
    [System.IO.File]::SetLastWriteTimeUtc($path, $script:artifactTimeBase.AddSeconds($script:artifactSequence))
    return $path
}

function Invoke-EligibilityPreview {
    param([string]$ProfileId)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- `
        --config (Resolve-ProjectPath $ConfigPath) `
        --preview-quest-board `
        --quest-board-profile-scope $ProfileId `
        --mod-state-dir $stateRoot `
        --no-inject | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader eligibility preview failed with exit code $LASTEXITCODE"
    }

    $reportPath = Join-Path $projectRoot "logs\quest_board_preview_report.json"
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) "Quest board preview report was not written: $reportPath"
    return Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
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

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- `
        --config (Resolve-ProjectPath $ConfigPath) `
        --validate-only `
        --mod-state-dir $stateRoot `
        --no-inject
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader producer catalog generation failed with exit code $LASTEXITCODE"
    }

    $script:frameworkProducer = Get-ManagedActionTestProducer `
        -ProjectRoot $projectRoot `
        -Kind "questBoardPolicySet"
    $activePolicies = @(New-ActivePolicyRows)
    $activeOwners = @($ownerA, $ownerB)

    $invalidSupersessionSource = Join-Path $projectRoot "logs\eligibility_invalid_supersession_resolve.json"
    $validOldPath = Write-FrameworkArtifact `
        -Name "001_valid_old.json" `
        -Status "materialized" `
        -SourcePath $invalidSupersessionSource `
        -RuleId "invalid_new_must_not_supersede" `
        -ProfileId "eligibility_invalid_new" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_necromancer_3")
    $invalidNewPath = Write-FrameworkArtifact `
        -Name "002_invalid_new_owner_set.json" `
        -Status "materialized" `
        -SourcePath $invalidSupersessionSource `
        -RuleId "invalid_new_must_not_supersede" `
        -ProfileId "eligibility_invalid_new" `
        -Owners @($ownerA) `
        -Policies $activePolicies `
        -QuestIds @("plot_darkest_dungeon_4")

    $multiOwnerPath = Write-FrameworkArtifact `
        -Name "003_valid_multi_owner.json" `
        -Status "materialized" `
        -SourcePath (Join-Path $projectRoot "logs\eligibility_multi_owner_resolve.json") `
        -RuleId "valid_multi_owner" `
        -ProfileId "eligibility_multi_owner" `
        -Owners $activeOwners `
        -Policies $activePolicies `
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
        -ProfileId "eligibility_inactive_owner" `
        -Owners @($inactiveOwner, $ownerB) `
        -Policies $activePolicies `
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
        -ProfileId "eligibility_wrong_owner_source" `
        -Owners @($wrongPathOwner, $ownerB) `
        -Policies $activePolicies `
        -QuestIds @("plot_darkest_dungeon_4")

    $emptySupersessionSource = Join-Path $projectRoot "logs\eligibility_empty_supersession_resolve.json"
    $emptyOldPath = Write-FrameworkArtifact `
        -Name "006_empty_group_old.json" `
        -Status "materialized" `
        -SourcePath $emptySupersessionSource `
        -RuleId "empty_marker_supersedes" `
        -ProfileId "eligibility_empty_marker" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_prophet_3")
    $emptyMarkerPath = Write-FrameworkArtifact `
        -Name "007_empty_group_marker.json" `
        -Status "empty" `
        -SourcePath $emptySupersessionSource `
        -RuleId "empty_marker_supersedes" `
        -ProfileId "eligibility_empty_marker" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @()

    $unknownSupersessionSource = Join-Path $projectRoot "logs\eligibility_unknown_supersession_resolve.json"
    $unknownOldPath = Write-FrameworkArtifact `
        -Name "008_unknown_group_old.json" `
        -Status "materialized" `
        -SourcePath $unknownSupersessionSource `
        -RuleId "unknown_must_not_supersede" `
        -ProfileId "eligibility_unknown_status" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_necromancer_3")
    $unknownNewPath = Write-FrameworkArtifact `
        -Name "009_unknown_group_new.json" `
        -Status "unexpected" `
        -SourcePath $unknownSupersessionSource `
        -RuleId "unknown_must_not_supersede" `
        -ProfileId "eligibility_unknown_status" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @()

    $definitionMismatchPath = Write-FrameworkArtifact `
        -Name "010_definition_mismatch.json" `
        -Status "materialized" `
        -SourcePath (Join-Path $projectRoot "logs\eligibility_definition_mismatch_resolve.json") `
        -RuleId "definition_mismatch" `
        -ProfileId "eligibility_definition_mismatch" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_necromancer_3")
    $definitionMismatch = Get-Content -Raw -LiteralPath $definitionMismatchPath | ConvertFrom-Json -AsHashtable
    $definitionMismatch.producer.definitionSha256 = "0" * 64
    $definitionMismatch | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $definitionMismatchPath -Encoding UTF8

    $oldVersionPath = Write-FrameworkArtifact `
        -Name "011_old_version.json" `
        -Status "materialized" `
        -SourcePath (Join-Path $projectRoot "logs\eligibility_old_version_resolve.json") `
        -RuleId "old_version" `
        -ProfileId "eligibility_old_version" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_prophet_3")
    $oldVersion = Get-Content -Raw -LiteralPath $oldVersionPath | ConvertFrom-Json -AsHashtable
    $oldVersion.version = 1
    $oldVersion | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $oldVersionPath -Encoding UTF8

    $invalidShapeSource = Join-Path $projectRoot "logs\eligibility_invalid_shape_resolve.json"
    $validShapeOldPath = Write-FrameworkArtifact `
        -Name "012_valid_shape_old.json" `
        -Status "materialized" `
        -SourcePath $invalidShapeSource `
        -RuleId "invalid_shape_must_not_supersede" `
        -ProfileId "eligibility_invalid_shape" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_necromancer_3")
    $invalidShapeNewPath = Write-FrameworkArtifact `
        -Name "013_invalid_shape_new.json" `
        -Status "materialized" `
        -SourcePath $invalidShapeSource `
        -RuleId "invalid_shape_must_not_supersede" `
        -ProfileId "eligibility_invalid_shape" `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_darkest_dungeon_4")
    $invalidShapeNew = Get-Content -Raw -LiteralPath $invalidShapeNewPath | ConvertFrom-Json -AsHashtable
    $invalidShapeNew.plan.arguments.Remove("questIds")
    $invalidShapeNew | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $invalidShapeNewPath -Encoding UTF8

    $invalidReport = Invoke-EligibilityPreview -ProfileId "eligibility_invalid_new"
    Assert-True ([bool]$invalidReport.succeeded) "Eligibility preview should finish without errors."
    Assert-True ([int]$invalidReport.errorCount -eq 0) "Ineligible artifacts should be warnings, not preview errors."
    Assert-True ((Find-ArtifactReport -Report $invalidReport -ArtifactPath $validOldPath)[0].status -eq "wouldApply") "Invalid newer artifact must not supersede the valid older artifact."
    Assert-True ((Find-ArtifactReport -Report $invalidReport -ArtifactPath $invalidNewPath)[0].status -eq "ineligible") "Owner/policy mismatch should be ineligible."

    $invalidShapeReport = Invoke-EligibilityPreview -ProfileId "eligibility_invalid_shape"
    Assert-True ((Find-ArtifactReport -Report $invalidShapeReport -ArtifactPath $validShapeOldPath)[0].status -eq "wouldApply") "A newer quest-board artifact without questIds must not supersede the valid older artifact."
    Assert-True ((Find-ArtifactReport -Report $invalidShapeReport -ArtifactPath $invalidShapeNewPath)[0].status -eq "ineligible") "A quest-board artifact without questIds should be ineligible."
    Assert-True ((@($invalidShapeReport.issues | Where-Object { $_.code -eq "managed-artifact-quest-board-contract-invalid" })).Count -eq 1) "Expected one invalid quest-board action contract issue."

    $multiOwnerReport = Invoke-EligibilityPreview -ProfileId "eligibility_multi_owner"
    Assert-True ((Find-ArtifactReport -Report $multiOwnerReport -ArtifactPath $multiOwnerPath)[0].status -eq "wouldApply") "A valid multi-owner framework artifact should remain consumable."
    Assert-True ((Find-ArtifactReport -Report $multiOwnerReport -ArtifactPath $inactiveOwnerPath)[0].status -eq "ineligible") "An inactive framework owner should be ineligible."
    Assert-True ((Find-ArtifactReport -Report $multiOwnerReport -ArtifactPath $wrongSourcePath)[0].status -eq "ineligible") "A same-id wrong-path framework owner should be ineligible."

    $emptyReport = Invoke-EligibilityPreview -ProfileId "eligibility_empty_marker"
    Assert-True ((Find-ArtifactReport -Report $emptyReport -ArtifactPath $emptyOldPath)[0].status -eq "ignored") "A valid empty marker should supersede the older materialized artifact."
    Assert-True ((Find-ArtifactReport -Report $emptyReport -ArtifactPath $emptyMarkerPath)[0].status -eq "ignored") "A valid empty marker should be accepted and then ignored as an empty status."

    $report = Invoke-EligibilityPreview -ProfileId "eligibility_unknown_status"
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $unknownOldPath)[0].status -eq "wouldApply") "An unknown newer status must not supersede a valid older artifact."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $unknownNewPath)[0].status -eq "ineligible") "An unknown framework status should be ineligible."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $definitionMismatchPath)[0].status -eq "ineligible") "A stale producer definition must be ineligible."
    Assert-True ((Find-ArtifactReport -Report $report -ArtifactPath $oldVersionPath)[0].status -eq "ineligible") "A v1 artifact must be ineligible without a compatibility path."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-owner-set-mismatch" })).Count -eq 1) "Expected one owner/policy set mismatch issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-owner-inactive" })).Count -eq 1) "Expected one inactive owner issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-owner-source-mismatch" })).Count -eq 1) "Expected one owner source mismatch issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-framework-contract-invalid" })).Count -eq 1) "Expected one invalid framework contract issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-producer-definition-mismatch" })).Count -eq 1) "Expected one stale producer definition issue."
    Assert-True ((@($report.issues | Where-Object { $_.code -eq "managed-artifact-version-unsupported" })).Count -eq 1) "Expected one unsupported v1 artifact issue."

    $stateRoot = Join-Path $projectRoot "state\managed_action_artifact_shape_test\$sessionId"
    $artifactRoot = Join-Path $stateRoot "_managed_actions"
    $script:artifactSequence = 0
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

    $shapeProfileId = "eligibility_shape_matrix"
    $shapeSource = Join-Path $projectRoot "logs\eligibility_shape_matrix_resolve.json"
    $shapeValidPath = Write-FrameworkArtifact `
        -Name "001_valid.json" `
        -Status "materialized" `
        -SourcePath $shapeSource `
        -RuleId "shape_matrix" `
        -ProfileId $shapeProfileId `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_necromancer_3")
    $emptyMaterializedPath = Write-FrameworkArtifact `
        -Name "002_empty_materialized.json" `
        -Status "materialized" `
        -SourcePath $shapeSource `
        -RuleId "shape_matrix" `
        -ProfileId $shapeProfileId `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @()
    $nonArrayPath = Write-FrameworkArtifact `
        -Name "003_non_array.json" `
        -Status "materialized" `
        -SourcePath $shapeSource `
        -RuleId "shape_matrix" `
        -ProfileId $shapeProfileId `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_prophet_3")
    $nonArrayArtifact = Get-Content -Raw -LiteralPath $nonArrayPath | ConvertFrom-Json -AsHashtable
    $nonArrayArtifact.plan.arguments.questIds = "plot_kill_prophet_3"
    $nonArrayArtifact | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $nonArrayPath -Encoding UTF8

    $nonStringPath = Write-FrameworkArtifact `
        -Name "004_non_string_item.json" `
        -Status "materialized" `
        -SourcePath $shapeSource `
        -RuleId "shape_matrix" `
        -ProfileId $shapeProfileId `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_kill_prophet_3")
    $nonStringArtifact = Get-Content -Raw -LiteralPath $nonStringPath | ConvertFrom-Json -AsHashtable
    $nonStringArtifact.plan.arguments.questIds = @("plot_kill_prophet_3", 17)
    $nonStringArtifact | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $nonStringPath -Encoding UTF8

    $blankStringPath = Write-FrameworkArtifact `
        -Name "005_blank_string_item.json" `
        -Status "materialized" `
        -SourcePath $shapeSource `
        -RuleId "shape_matrix" `
        -ProfileId $shapeProfileId `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @(" ")
    $nonEmptyMarkerPath = Write-FrameworkArtifact `
        -Name "006_non_empty_marker.json" `
        -Status "empty" `
        -SourcePath $shapeSource `
        -RuleId "shape_matrix" `
        -ProfileId $shapeProfileId `
        -Owners $activeOwners `
        -Policies $activePolicies `
        -QuestIds @("plot_darkest_dungeon_4")

    $shapeReport = Invoke-EligibilityPreview -ProfileId $shapeProfileId
    Assert-True ((Find-ArtifactReport -Report $shapeReport -ArtifactPath $shapeValidPath)[0].status -eq "wouldApply") "Invalid newer quest-board shapes must not supersede a valid older artifact."
    foreach ($invalidShapePath in @($emptyMaterializedPath, $nonArrayPath, $nonStringPath, $blankStringPath, $nonEmptyMarkerPath)) {
        Assert-True ((Find-ArtifactReport -Report $shapeReport -ArtifactPath $invalidShapePath)[0].status -eq "ineligible") "Malformed questIds artifact should be ineligible: $invalidShapePath"
    }
    Assert-True ((@($shapeReport.issues | Where-Object { $_.code -eq "managed-artifact-quest-board-contract-invalid" })).Count -eq 5) "Expected all five malformed questIds variants to fail the shared quest-board contract."

    Write-Host "PASS: managed action artifact eligibility and supersession contract."
}
finally {
    if ($null -ne $oldVersionPath -and (Test-Path -LiteralPath $oldVersionPath -PathType Leaf)) {
        Remove-Item -LiteralPath $oldVersionPath -Force
    }
    Pop-Location
}
