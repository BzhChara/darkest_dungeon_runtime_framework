param()

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot "state\profile_promotion_test\$sessionId"
$workspaceRoot = Join-Path $stateRoot "workspace"
$encodedProfileRoot = Join-Path $workspaceRoot "encoded_profile"
$roundTripRoot = Join-Path $workspaceRoot "roundtrip_decoded"
$targetProfileRoot = Join-Path $stateRoot "remote\profile_3"
$backupRoot = Join-Path $stateRoot "promotion_backups"
$promotionId = "profile_promotion_contract"
$restoreId = "profile_promotion_restore_contract"

function Write-TestFile {
    param(
        [string]$Path,
        [string]$Text
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $Text -Encoding UTF8
}

function Read-JsonFile {
    param([string]$Path)

    Assert-DdrtTrue (Test-Path -LiteralPath $Path -PathType Leaf) "JSON file was not found: $Path"
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Invoke-PromotionTool {
    param([hashtable]$ToolParams)

    $output = & (Join-Path $projectRoot "tools\PromoteEncodedProfileWorkspace.ps1") @ToolParams
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "PromoteEncodedProfileWorkspace failed with exit code $LASTEXITCODE"
    }

    return ($output | Out-String) | ConvertFrom-Json
}

New-Item -ItemType Directory -Force -Path $encodedProfileRoot, $roundTripRoot, $targetProfileRoot | Out-Null

Write-TestFile -Path (Join-Path $encodedProfileRoot "persist.estate.json") -Text '{"base_root":{"wallet":{"0":{"type":"gold","amount":20000}}}}'
Write-TestFile -Path (Join-Path $encodedProfileRoot "persist.quest.json") -Text '{"base_root":{"quests":{"0":{"id":"plot_kill_necromancer_3"}}}}'
Write-TestFile -Path (Join-Path $encodedProfileRoot "persist.roster.json") -Text '{"base_root":{"heroes":{"1":{"hero_file_data":{"raw_data":{"base_root":{"heroClass":"crusader","resolveXp":46}}}}}}}'
Write-TestFile -Path (Join-Path $encodedProfileRoot "persist.new_runtime_marker.json") -Text '{"base_root":{"marker":true}}'
Write-TestFile -Path (Join-Path $encodedProfileRoot "persist.unchanged.json") -Text '{"base_root":{"unchanged":true}}'

Copy-Item -LiteralPath (Join-Path $encodedProfileRoot "persist.estate.json") -Destination (Join-Path $roundTripRoot "persist.estate.json") -Force
Copy-Item -LiteralPath (Join-Path $encodedProfileRoot "persist.quest.json") -Destination (Join-Path $roundTripRoot "persist.quest.json") -Force
Copy-Item -LiteralPath (Join-Path $encodedProfileRoot "persist.roster.json") -Destination (Join-Path $roundTripRoot "persist.roster.json") -Force
Copy-Item -LiteralPath (Join-Path $encodedProfileRoot "persist.new_runtime_marker.json") -Destination (Join-Path $roundTripRoot "persist.new_runtime_marker.json") -Force
Copy-Item -LiteralPath (Join-Path $encodedProfileRoot "persist.unchanged.json") -Destination (Join-Path $roundTripRoot "persist.unchanged.json") -Force

$workspaceReport = [pscustomobject]@{
    version = 1
    encodeInitializedProfileRequested = $true
    encodedProfileDirectory = $encodedProfileRoot
    roundTripDecodedDirectory = $roundTripRoot
    files = @(
        [pscustomobject]@{ name = "persist.estate.json"; decodedSha256 = "before-estate" },
        [pscustomobject]@{ name = "persist.quest.json"; decodedSha256 = "before-quest" },
        [pscustomobject]@{ name = "persist.roster.json"; decodedSha256 = "before-roster" },
        [pscustomobject]@{ name = "persist.unchanged.json"; decodedSha256 = "same-unchanged" }
    )
    encoding = [pscustomobject]@{
        status = "completed"
        failedFileCount = 0
        encodedProfileDirectory = $encodedProfileRoot
        roundTripDecodedDirectory = $roundTripRoot
        roundTripValidatedFileCount = 5
        files = @(
            [pscustomobject]@{ name = "persist.estate.json"; status = "roundtrip-validated"; decodedSha256 = "after-estate"; encodedPath = (Join-Path $encodedProfileRoot "persist.estate.json") },
            [pscustomobject]@{ name = "persist.quest.json"; status = "roundtrip-validated"; decodedSha256 = "after-quest"; encodedPath = (Join-Path $encodedProfileRoot "persist.quest.json") },
            [pscustomobject]@{ name = "persist.roster.json"; status = "roundtrip-validated"; decodedSha256 = "after-roster"; encodedPath = (Join-Path $encodedProfileRoot "persist.roster.json") },
            [pscustomobject]@{ name = "persist.new_runtime_marker.json"; status = "roundtrip-validated"; decodedSha256 = "after-new"; encodedPath = (Join-Path $encodedProfileRoot "persist.new_runtime_marker.json") },
            [pscustomobject]@{ name = "persist.unchanged.json"; status = "roundtrip-validated"; decodedSha256 = "same-unchanged"; encodedPath = (Join-Path $encodedProfileRoot "persist.unchanged.json") }
        )
    }
}
$workspaceReport | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $workspaceRoot "decoded_profile_workspace_report.json") -Encoding UTF8

Write-TestFile -Path (Join-Path $targetProfileRoot "persist.estate.json") -Text '{"base_root":{"wallet":{"0":{"type":"gold","amount":1234}}}}'
Write-TestFile -Path (Join-Path $targetProfileRoot "persist.quest.json") -Text '{"base_root":{"quests":{"0":{"id":"plot_tutorial_crypts"}}}}'
Write-TestFile -Path (Join-Path $targetProfileRoot "persist.roster.json") -Text '{"base_root":{"heroes":{}}}'
Write-TestFile -Path (Join-Path $targetProfileRoot "persist.unchanged.json") -Text '{"base_root":{"unchanged":true}}'
Write-TestFile -Path (Join-Path $targetProfileRoot "backup\persist.quest.json") -Text '{"backup":true}'

$dryRunReport = Invoke-PromotionTool -ToolParams @{
    WorkspaceRoot = $workspaceRoot
    TargetProfileDirectory = $targetProfileRoot
    BackupRoot = $backupRoot
    PromotionId = $promotionId
}
Assert-DdrtTrue ([string]$dryRunReport.status -eq "dry-run-would-write") "Promotion dry-run should report dry-run-would-write."
Assert-DdrtTrue ([bool]$dryRunReport.dryRun) "Promotion dry-run should record dryRun=true."
Assert-DdrtTrue (-not [bool]$dryRunReport.written) "Promotion dry-run must not write files."
Assert-DdrtTrue (@(ConvertTo-DdrtArray $dryRunReport.files | Where-Object { $_.name -eq "persist.unchanged.json" }).Count -eq 0) "Promotion dry-run should exclude decoded-unchanged files by default."
Assert-DdrtTrue (-not (Test-Path -LiteralPath (Join-Path $targetProfileRoot "persist.new_runtime_marker.json") -PathType Leaf)) "Promotion dry-run wrote a new file."

$noChangeWorkspaceRoot = Join-Path $stateRoot "workspace_no_decoded_changes"
$noChangeEncodedProfileRoot = Join-Path $noChangeWorkspaceRoot "encoded_profile"
$noChangeRoundTripRoot = Join-Path $noChangeWorkspaceRoot "roundtrip_decoded"
New-Item -ItemType Directory -Force -Path $noChangeEncodedProfileRoot, $noChangeRoundTripRoot | Out-Null
Write-TestFile -Path (Join-Path $noChangeEncodedProfileRoot "persist.unchanged.json") -Text '{"base_root":{"unchanged":true}}'
Copy-Item -LiteralPath (Join-Path $noChangeEncodedProfileRoot "persist.unchanged.json") -Destination (Join-Path $noChangeRoundTripRoot "persist.unchanged.json") -Force

$noChangeWorkspaceReport = [pscustomobject]@{
    version = 1
    encodeInitializedProfileRequested = $true
    encodedProfileDirectory = $noChangeEncodedProfileRoot
    roundTripDecodedDirectory = $noChangeRoundTripRoot
    files = @(
        [pscustomobject]@{ name = "persist.unchanged.json"; decodedSha256 = "same-unchanged" }
    )
    encoding = [pscustomobject]@{
        status = "completed"
        failedFileCount = 0
        encodedProfileDirectory = $noChangeEncodedProfileRoot
        roundTripDecodedDirectory = $noChangeRoundTripRoot
        roundTripValidatedFileCount = 1
        files = @(
            [pscustomobject]@{ name = "persist.unchanged.json"; status = "roundtrip-validated"; decodedSha256 = "same-unchanged"; encodedPath = (Join-Path $noChangeEncodedProfileRoot "persist.unchanged.json") }
        )
    }
}
$noChangeWorkspaceReport | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $noChangeWorkspaceRoot "decoded_profile_workspace_report.json") -Encoding UTF8

$noChangePromotionReport = Invoke-PromotionTool -ToolParams @{
    WorkspaceRoot = $noChangeWorkspaceRoot
    TargetProfileDirectory = $targetProfileRoot
    BackupRoot = $backupRoot
    PromotionId = ($promotionId + "_no_decoded_changes")
}
Assert-DdrtTrue ([string]$noChangePromotionReport.status -eq "dry-run-unchanged") "Promotion dry-run with no decoded changes should report dry-run-unchanged."
Assert-DdrtTrue (@(ConvertTo-DdrtArray $noChangePromotionReport.files).Count -eq 0) "Promotion dry-run with no decoded changes should not plan file writes."
Assert-DdrtTrue ([int]$noChangePromotionReport.errorCount -eq 0) "Promotion dry-run with no decoded changes should not report errors."

$writeReport = Invoke-PromotionTool -ToolParams @{
    WorkspaceRoot = $workspaceRoot
    TargetProfileDirectory = $targetProfileRoot
    BackupRoot = $backupRoot
    PromotionId = $promotionId
    Write = $true
}
Assert-DdrtTrue ([string]$writeReport.status -eq "written") "Promotion write should report written."
Assert-DdrtTrue ([bool]$writeReport.written) "Promotion write should record written=true."
Assert-DdrtTrue ([string]$writeReport.writeMode -eq "direct-overwrite-after-backup") "Promotion write should report explicit non-atomic write mode."
Assert-DdrtTrue (Test-Path -LiteralPath ([string]$writeReport.backupManifestPath) -PathType Leaf) "Promotion write should create a backup manifest."
Assert-DdrtTrue (Test-Path -LiteralPath (Join-Path $targetProfileRoot "persist.new_runtime_marker.json") -PathType Leaf) "Promotion write should copy new encoded profile files."

$targetEstate = Read-JsonFile -Path (Join-Path $targetProfileRoot "persist.estate.json")
Assert-DdrtTrue ([int]$targetEstate.base_root.wallet.'0'.amount -eq 20000) "Promotion write should replace target estate file."
$backupManifest = Read-JsonFile -Path ([string]$writeReport.backupManifestPath)
Assert-DdrtTrue ((ConvertTo-DdrtArray $backupManifest.backedUpFiles).Count -eq 5) "Promotion backup should snapshot existing target files recursively."
Assert-DdrtTrue ((ConvertTo-DdrtArray $backupManifest.promotedFiles | Where-Object { $_.name -eq "persist.new_runtime_marker.json" -and -not [bool]$_.targetExistedBefore }).Count -eq 1) "Promotion backup manifest should record files added by promotion."
Assert-DdrtTrue ((ConvertTo-DdrtArray $backupManifest.promotedFiles | Where-Object { $_.name -eq "persist.unchanged.json" }).Count -eq 0) "Promotion backup manifest should exclude decoded-unchanged files by default."

$unchangedReport = Invoke-PromotionTool -ToolParams @{
    WorkspaceRoot = $workspaceRoot
    TargetProfileDirectory = $targetProfileRoot
    BackupRoot = $backupRoot
    PromotionId = ($promotionId + "_unchanged")
    Write = $true
}
Assert-DdrtTrue ([string]$unchangedReport.status -eq "unchanged") "Second promotion should report unchanged."
Assert-DdrtTrue (-not [bool]$unchangedReport.written) "Second promotion should not rewrite unchanged files."

$restoreDryRunReport = Invoke-PromotionTool -ToolParams @{
    RestoreFromReport = ([string]$writeReport.reportPath)
    PromotionId = ($restoreId + "_dryrun")
}
Assert-DdrtTrue ([string]$restoreDryRunReport.status -eq "dry-run-would-restore") "Restore dry-run should report dry-run-would-restore."
Assert-DdrtTrue ([bool]$restoreDryRunReport.dryRun) "Restore dry-run should record dryRun=true."

$restoreReport = Invoke-PromotionTool -ToolParams @{
    RestoreFromReport = ([string]$writeReport.reportPath)
    PromotionId = $restoreId
    Write = $true
}
Assert-DdrtTrue ([string]$restoreReport.status -eq "restored") "Restore should report restored."
Assert-DdrtTrue ([int]$restoreReport.restoredFileCount -eq 5) "Restore should copy back the full backup snapshot."
Assert-DdrtTrue ([int]$restoreReport.warningCount -eq 1) "Restore should warn about promotion-added files left in place."
Assert-DdrtTrue (Test-Path -LiteralPath (Join-Path $targetProfileRoot "persist.new_runtime_marker.json") -PathType Leaf) "Restore should leave promotion-added files in place for explicit cleanup."

$restoredEstate = Read-JsonFile -Path (Join-Path $targetProfileRoot "persist.estate.json")
Assert-DdrtTrue ([int]$restoredEstate.base_root.wallet.'0'.amount -eq 1234) "Restore should recover the original estate file."
$restoredQuest = Read-JsonFile -Path (Join-Path $targetProfileRoot "persist.quest.json")
Assert-DdrtTrue ([string]$restoredQuest.base_root.quests.'0'.id -eq "plot_tutorial_crypts") "Restore should recover the original quest file."
Assert-DdrtTrue (Test-Path -LiteralPath (Join-Path $targetProfileRoot "backup\persist.quest.json") -PathType Leaf) "Restore should preserve backed-up subdirectory files."

Write-Host "PASS: encoded profile promotion dry-run, backup, write, unchanged, and restore paths passed."
