param(
    [string]$WorkspaceRoot = "",
    [string]$TargetProfileDirectory = "",
    [string]$BackupRoot = "state\profile_promotions",
    [string]$PromotionId = "",
    [string]$RestoreFromReport = "",
    [switch]$Write,
    [switch]$AllowExternalTarget,
    [switch]$AllowRunningGameSaveWrite,
    [string]$GameExecutablePath = "E:\Steam\steamapps\common\DarkestDungeon\_windows\win64\Darkest.exe"
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot

function Get-FullPath {
    param([string]$Path)

    return [System.IO.Path]::GetFullPath((Resolve-DdrtProjectPath $Path))
}

function Test-InsideDirectory {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    return $fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith(($fullRoot + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-ProjectScopedPath {
    param(
        [string]$Path,
        [string]$Name
    )

    $fullPath = Get-FullPath $Path
    if (-not (Test-InsideDirectory -Root $projectRoot -Path $fullPath)) {
        throw "$Name must stay inside the project root: $fullPath"
    }

    return $fullPath
}

function Resolve-TargetProfilePath {
    param([string]$Path)

    $fullPath = Get-FullPath $Path
    if (-not (Test-InsideDirectory -Root $projectRoot -Path $fullPath) -and -not $AllowExternalTarget) {
        throw "TargetProfileDirectory is outside the project root. Pass -AllowExternalTarget to make that explicit: $fullPath"
    }

    Assert-DdrtTrue (Test-Path -LiteralPath $fullPath -PathType Container) "Target profile directory was not found: $fullPath"
    return $fullPath
}

function Test-RunningGameShouldBlock {
    param([string]$TargetPath)

    if ($AllowRunningGameSaveWrite -or (Test-InsideDirectory -Root $projectRoot -Path $TargetPath)) {
        return $false
    }

    $expectedGamePath = [System.IO.Path]::GetFullPath($GameExecutablePath)
    foreach ($process in Get-Process -Name Darkest -ErrorAction SilentlyContinue) {
        try {
            $processPath = [System.IO.Path]::GetFullPath($process.MainModule.FileName)
            if ($processPath.Equals($expectedGamePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        catch {
            return $true
        }
    }

    return $false
}

function Get-FileSha256 {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Convert-ToRelativePath {
    param(
        [string]$Root,
        [string]$Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path)
}

function Assert-PathInsideTarget {
    param(
        [string]$TargetRoot,
        [string]$CandidatePath,
        [string]$Name
    )

    if (-not (Test-InsideDirectory -Root $TargetRoot -Path $CandidatePath)) {
        throw "$Name resolved outside target profile directory: $CandidatePath"
    }
}

function New-Issue {
    param(
        [string]$Severity,
        [string]$Code,
        [string]$Path,
        [string]$Message
    )

    return [pscustomobject]@{
        severity = $Severity
        code = $Code
        path = $Path
        message = $Message
    }
}

function New-FilePlan {
    param(
        [System.IO.FileInfo]$SourceFile,
        [string]$TargetPath
    )

    $targetExists = Test-Path -LiteralPath $TargetPath -PathType Leaf
    $sourceHash = Get-FileSha256 $SourceFile.FullName
    $targetHash = if ($targetExists) { Get-FileSha256 $TargetPath } else { "" }
    $changed = (-not $targetExists) -or (-not $sourceHash.Equals($targetHash, [System.StringComparison]::OrdinalIgnoreCase))

    return [pscustomobject]@{
        name = $SourceFile.Name
        sourcePath = $SourceFile.FullName
        targetPath = $TargetPath
        targetExistedBefore = $targetExists
        sourceSha256 = $sourceHash
        targetSha256Before = $targetHash
        changed = $changed
        written = $false
        targetSha256After = ""
    }
}

function Backup-TargetProfile {
    param(
        [string]$TargetRoot,
        [string]$BackupDirectory,
        [array]$PromotedFiles
    )

    $snapshotDirectory = Join-Path $BackupDirectory "profile_snapshot"
    New-Item -ItemType Directory -Force -Path $snapshotDirectory | Out-Null
    $backedUpFiles = @()

    foreach ($targetFile in (Get-ChildItem -LiteralPath $TargetRoot -File -Recurse | Sort-Object FullName)) {
        $relativePath = Convert-ToRelativePath -Root $TargetRoot -Path $targetFile.FullName
        $backupPath = Join-Path $snapshotDirectory $relativePath
        $backupParent = Split-Path -Parent $backupPath
        if (-not [string]::IsNullOrWhiteSpace($backupParent)) {
            New-Item -ItemType Directory -Force -Path $backupParent | Out-Null
        }

        Copy-Item -LiteralPath $targetFile.FullName -Destination $backupPath -Force
        $backedUpFiles += [pscustomobject]@{
            relativePath = $relativePath
            targetPath = $targetFile.FullName
            backupPath = $backupPath
            sha256 = Get-FileSha256 $targetFile.FullName
            bytes = $targetFile.Length
        }
    }

    return [pscustomobject]@{
        snapshotDirectory = $snapshotDirectory
        backedUpFiles = $backedUpFiles
        promotedFiles = $PromotedFiles
    }
}

function Write-Report {
    param(
        [object]$Report,
        [string]$ReportPath
    )

    $parent = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}

function Get-PromotionId {
    param(
        [string]$Prefix,
        [string]$ExplicitId
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitId)) {
        return $ExplicitId
    }

    return $Prefix + "_" + (Get-Date -Format "yyyyMMdd_HHmmss_fff")
}

function Invoke-Promotion {
    Assert-DdrtTrue (-not [string]::IsNullOrWhiteSpace($WorkspaceRoot)) "WorkspaceRoot is required for promotion."
    Assert-DdrtTrue (-not [string]::IsNullOrWhiteSpace($TargetProfileDirectory)) "TargetProfileDirectory is required for promotion."

    $workspacePath = Resolve-ProjectScopedPath -Path $WorkspaceRoot -Name "WorkspaceRoot"
    $targetPath = Resolve-TargetProfilePath -Path $TargetProfileDirectory
    $backupRootPath = Resolve-ProjectScopedPath -Path $BackupRoot -Name "BackupRoot"
    $id = Get-PromotionId -Prefix ("promote_" + (Split-Path -Leaf $targetPath)) -ExplicitId $PromotionId
    $logRoot = Resolve-ProjectScopedPath -Path "logs\profile_promotions" -Name "LogRoot"
    $reportPath = Join-Path $logRoot ($id + ".json")
    $backupDirectory = Join-Path $backupRootPath $id
    $backupManifestPath = Join-Path $backupDirectory "backup_manifest.json"
    $issues = @()

    $workspaceReportPath = Join-Path $workspacePath "decoded_profile_workspace_report.json"
    if (-not (Test-Path -LiteralPath $workspaceReportPath -PathType Leaf)) {
        $issues += New-Issue -Severity "error" -Code "workspace-report-missing" -Path $workspaceReportPath -Message "decoded profile workspace report was not found."
    }

    $workspaceReport = $null
    $encodedProfileDirectory = Join-Path $workspacePath "encoded_profile"
    if ($issues.Count -eq 0) {
        $workspaceReport = Get-Content -Raw -LiteralPath $workspaceReportPath | ConvertFrom-Json
        if (-not [bool]$workspaceReport.encodeInitializedProfileRequested) {
            $issues += New-Issue -Severity "error" -Code "workspace-not-encoded" -Path $workspaceReportPath -Message "workspace was not created with -EncodeInitializedProfile."
        }

        if ([string]$workspaceReport.encoding.status -ne "completed") {
            $issues += New-Issue -Severity "error" -Code "workspace-encoding-incomplete" -Path $workspaceReportPath -Message "workspace encoding status is not completed."
        }

        if ([int]$workspaceReport.encoding.failedFileCount -ne 0) {
            $issues += New-Issue -Severity "error" -Code "workspace-encoding-failed-files" -Path $workspaceReportPath -Message "workspace encoding report contains failed files."
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$workspaceReport.encoding.encodedProfileDirectory)) {
            $encodedProfileDirectory = [string]$workspaceReport.encoding.encodedProfileDirectory
        }
    }

    $encodedProfilePath = [System.IO.Path]::GetFullPath($encodedProfileDirectory)
    if (-not (Test-InsideDirectory -Root $workspacePath -Path $encodedProfilePath)) {
        $issues += New-Issue -Severity "error" -Code "encoded-profile-outside-workspace" -Path $encodedProfilePath -Message "encoded profile directory must stay inside the decoded profile workspace."
    }

    if (-not (Test-Path -LiteralPath $encodedProfilePath -PathType Container)) {
        $issues += New-Issue -Severity "error" -Code "encoded-profile-missing" -Path $encodedProfilePath -Message "encoded profile directory was not found."
    }

    if (Test-RunningGameShouldBlock -TargetPath $targetPath) {
        $issues += New-Issue -Severity "error" -Code "game-running" -Path $targetPath -Message "Darkest.exe is running while the target profile is outside the project root; exit the game or pass -AllowRunningGameSaveWrite."
    }

    $filePlans = @()
    if ($issues.Count -eq 0) {
        $sourceFiles = @(Get-ChildItem -LiteralPath $encodedProfilePath -File | Sort-Object Name)
        if ($sourceFiles.Count -eq 0) {
            $issues += New-Issue -Severity "error" -Code "encoded-profile-empty" -Path $encodedProfilePath -Message "encoded profile directory contains no top-level files."
        }

        foreach ($sourceFile in $sourceFiles) {
            $targetFilePath = Join-Path $targetPath $sourceFile.Name
            Assert-PathInsideTarget -TargetRoot $targetPath -CandidatePath $targetFilePath -Name "target file"
            $filePlans += New-FilePlan -SourceFile $sourceFile -TargetPath $targetFilePath
        }
    }

    $changed = @($filePlans | Where-Object { [bool]$_.changed }).Count -gt 0
    $written = $false
    $writeMode = "none"

    if ($issues.Count -eq 0 -and $Write -and $changed) {
        try {
            $backup = Backup-TargetProfile -TargetRoot $targetPath -BackupDirectory $backupDirectory -PromotedFiles $filePlans
            $manifest = [pscustomobject]@{
                version = 1
                createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
                promotionId = $id
                targetProfileDirectory = $targetPath
                workspaceRoot = $workspacePath
                encodedProfileDirectory = $encodedProfilePath
                backupDirectory = $backupDirectory
                snapshotDirectory = $backup.snapshotDirectory
                promotedFiles = $backup.promotedFiles
                backedUpFiles = $backup.backedUpFiles
            }
            Write-Report -Report $manifest -ReportPath $backupManifestPath

            foreach ($plan in $filePlans) {
                if ([bool]$plan.changed) {
                    Copy-Item -LiteralPath ([string]$plan.sourcePath) -Destination ([string]$plan.targetPath) -Force
                    $plan.written = $true
                    $plan.targetSha256After = Get-FileSha256 ([string]$plan.targetPath)
                    if (-not ([string]$plan.targetSha256After).Equals([string]$plan.sourceSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $issues += New-Issue -Severity "error" -Code "written-hash-mismatch" -Path ([string]$plan.targetPath) -Message "written file hash does not match source encoded profile file."
                    }
                }
            }

            $written = @($filePlans | Where-Object { [bool]$_.written }).Count -gt 0
            $writeMode = "direct-overwrite-after-backup"
        }
        catch {
            $issues += New-Issue -Severity "error" -Code "write-failed" -Path $targetPath -Message $_.Exception.Message
        }
    }

    $status = if (@($issues | Where-Object { $_.severity -eq "error" }).Count -gt 0) {
        "blocked"
    }
    elseif (-not $Write) {
        if ($changed) { "dry-run-would-write" } else { "dry-run-unchanged" }
    }
    elseif ($written) {
        "written"
    }
    else {
        "unchanged"
    }

    $report = [pscustomobject]@{
        version = 1
        mode = "promote"
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        promotionId = $id
        reportPath = $reportPath
        status = $status
        dryRun = (-not $Write)
        workspaceRoot = $workspacePath
        workspaceReportPath = $workspaceReportPath
        encodedProfileDirectory = $encodedProfilePath
        targetProfileDirectory = $targetPath
        backupDirectory = if ($written) { $backupDirectory } else { "" }
        backupManifestPath = if ($written) { $backupManifestPath } else { "" }
        changed = $changed
        written = $written
        writeMode = $writeMode
        fileCount = $filePlans.Count
        changedFileCount = @($filePlans | Where-Object { [bool]$_.changed }).Count
        writtenFileCount = @($filePlans | Where-Object { [bool]$_.written }).Count
        warningCount = @($issues | Where-Object { $_.severity -eq "warning" }).Count
        errorCount = @($issues | Where-Object { $_.severity -eq "error" }).Count
        files = $filePlans
        issues = $issues
    }

    Write-Report -Report $report -ReportPath $reportPath
    return $report
}

function Invoke-Restore {
    Assert-DdrtTrue (-not [string]::IsNullOrWhiteSpace($RestoreFromReport)) "RestoreFromReport is required for restore."

    $sourceReportPath = Get-FullPath $RestoreFromReport
    Assert-DdrtTrue (Test-Path -LiteralPath $sourceReportPath -PathType Leaf) "Promotion report was not found: $sourceReportPath"
    $sourceReport = Get-Content -Raw -LiteralPath $sourceReportPath | ConvertFrom-Json
    $targetPath = if ([string]::IsNullOrWhiteSpace($TargetProfileDirectory)) {
        [string]$sourceReport.targetProfileDirectory
    }
    else {
        Resolve-TargetProfilePath -Path $TargetProfileDirectory
    }

    $targetPath = Resolve-TargetProfilePath -Path $targetPath
    $id = Get-PromotionId -Prefix ("restore_" + (Split-Path -Leaf $targetPath)) -ExplicitId $PromotionId
    $logRoot = Resolve-ProjectScopedPath -Path "logs\profile_promotions" -Name "LogRoot"
    $reportPath = Join-Path $logRoot ($id + ".json")
    $issues = @()
    $restoredFiles = @()

    if ([string]$sourceReport.status -ne "written") {
        $issues += New-Issue -Severity "error" -Code "source-report-not-written" -Path $sourceReportPath -Message "restore requires a promotion report with status=written."
    }

    $backupManifestPath = [string]$sourceReport.backupManifestPath
    if ([string]::IsNullOrWhiteSpace($backupManifestPath) -or -not (Test-Path -LiteralPath $backupManifestPath -PathType Leaf)) {
        $issues += New-Issue -Severity "error" -Code "backup-manifest-missing" -Path $backupManifestPath -Message "backup manifest was not found."
    }

    if (Test-RunningGameShouldBlock -TargetPath $targetPath) {
        $issues += New-Issue -Severity "error" -Code "game-running" -Path $targetPath -Message "Darkest.exe is running while the target profile is outside the project root; exit the game or pass -AllowRunningGameSaveWrite."
    }

    $manifest = $null
    if ($issues.Count -eq 0) {
        $manifest = Get-Content -Raw -LiteralPath $backupManifestPath | ConvertFrom-Json
        if (-not ([string]$manifest.targetProfileDirectory).Equals($targetPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $issues += New-Issue -Severity "error" -Code "target-profile-mismatch" -Path $targetPath -Message "restore target does not match the backup manifest target profile."
        }
    }

    if ($issues.Count -eq 0 -and $Write) {
        try {
            foreach ($entry in (ConvertTo-DdrtArray $manifest.backedUpFiles)) {
                $targetFilePath = Join-Path $targetPath ([string]$entry.relativePath)
                Assert-PathInsideTarget -TargetRoot $targetPath -CandidatePath $targetFilePath -Name "restore target file"
                $targetParent = Split-Path -Parent $targetFilePath
                if (-not [string]::IsNullOrWhiteSpace($targetParent)) {
                    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
                }

                Copy-Item -LiteralPath ([string]$entry.backupPath) -Destination $targetFilePath -Force
                $restoredFiles += [pscustomobject]@{
                    relativePath = [string]$entry.relativePath
                    targetPath = $targetFilePath
                    backupPath = [string]$entry.backupPath
                    restored = $true
                    targetSha256After = Get-FileSha256 $targetFilePath
                }
            }

            $addedPromotionFiles = @(ConvertTo-DdrtArray $manifest.promotedFiles | Where-Object { -not [bool]$_.targetExistedBefore })
            if ($addedPromotionFiles.Count -gt 0) {
                $issues += New-Issue -Severity "warning" -Code "promotion-added-files-left-in-place" -Path $targetPath -Message "promotion added files that were not present before; restore keeps those files in place and reports them for explicit cleanup."
            }
        }
        catch {
            $issues += New-Issue -Severity "error" -Code "restore-failed" -Path $targetPath -Message $_.Exception.Message
        }
    }

    $status = if (@($issues | Where-Object { $_.severity -eq "error" }).Count -gt 0) {
        "blocked"
    }
    elseif (-not $Write) {
        "dry-run-would-restore"
    }
    else {
        "restored"
    }

    $report = [pscustomobject]@{
        version = 1
        mode = "restore"
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        promotionId = $id
        reportPath = $reportPath
        status = $status
        dryRun = (-not $Write)
        sourcePromotionReportPath = $sourceReportPath
        backupManifestPath = $backupManifestPath
        targetProfileDirectory = $targetPath
        restoredFileCount = $restoredFiles.Count
        warningCount = @($issues | Where-Object { $_.severity -eq "warning" }).Count
        errorCount = @($issues | Where-Object { $_.severity -eq "error" }).Count
        restoredFiles = $restoredFiles
        issues = $issues
    }

    Write-Report -Report $report -ReportPath $reportPath
    return $report
}

$result = if ([string]::IsNullOrWhiteSpace($RestoreFromReport)) {
    Invoke-Promotion
}
else {
    Invoke-Restore
}

Write-Output ($result | ConvertTo-Json -Depth 20)
