param(
    [string]$SourceProfileDirectory = "E:\Steam\userdata\1097809614\262060\remote\profile_3",
    [string]$OutputRoot = "state\decoded_profiles",
    [string]$SessionId = "",
    [string]$SaveEditorJar = ".research\DDSaveEditor-v0.0.70\DDSaveEditor.jar",
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [string]$ModStateId = "validation.boss_gauntlet_campaign_contract",
    [switch]$Initialize,
    [switch]$WriteManagedActions,
    [switch]$EncodeInitializedProfile,
    [switch]$NoBuild,
    [bool]$AllowNonAtomicStateWrites = $true
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot
$requiredInitializationFiles = @(
    "persist.estate.json",
    "persist.roster.json",
    "persist.upgrades.json",
    "persist.town.json",
    "persist.quest.json"
)

function Resolve-WorkspacePath {
    param(
        [string]$Path,
        [string]$Name
    )

    $fullPath = [System.IO.Path]::GetFullPath((Resolve-DdrtProjectPath $Path))
    $projectFullPath = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $projectChildPrefix = $projectFullPath + [System.IO.Path]::DirectorySeparatorChar
    $isProjectRoot = $fullPath.Equals($projectFullPath, [System.StringComparison]::OrdinalIgnoreCase)
    $isProjectChild = $fullPath.StartsWith($projectChildPrefix, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not ($isProjectRoot -or $isProjectChild)) {
        throw "$Name must stay inside the project root: $fullPath"
    }

    return $fullPath
}

function New-FileReport {
    param(
        [System.IO.FileInfo]$SourceFile,
        [string]$DecodedPath,
        [string]$Status,
        [int]$ExitCode,
        [string]$Message
    )

    $decodedExists = Test-Path -LiteralPath $DecodedPath -PathType Leaf
    $decodedItem = if ($decodedExists) { Get-Item -LiteralPath $DecodedPath } else { $null }
    $decodedHash = if ($decodedExists) { (Get-FileHash -LiteralPath $DecodedPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { "" }

    return [pscustomobject]@{
        name = $SourceFile.Name
        sourcePath = $SourceFile.FullName
        decodedPath = $DecodedPath
        status = $Status
        exitCode = $ExitCode
        message = $Message
        sourceBytes = $SourceFile.Length
        decodedBytes = if ($null -ne $decodedItem) { $decodedItem.Length } else { 0 }
        sourceSha256 = (Get-FileHash -LiteralPath $SourceFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        decodedSha256 = $decodedHash
    }
}

function New-EncodingFileReport {
    param(
        [object]$DecodedFileReport,
        [string]$EncodedPath,
        [string]$RoundTripDecodedPath,
        [string]$Status,
        [int]$EncodeExitCode,
        [int]$RoundTripDecodeExitCode,
        [string]$EncodeMessage,
        [string]$RoundTripDecodeMessage
    )

    $encodedExists = Test-Path -LiteralPath $EncodedPath -PathType Leaf
    $encodedItem = if ($encodedExists) { Get-Item -LiteralPath $EncodedPath } else { $null }
    $roundTripExists = Test-Path -LiteralPath $RoundTripDecodedPath -PathType Leaf
    $roundTripItem = if ($roundTripExists) { Get-Item -LiteralPath $RoundTripDecodedPath } else { $null }

    return [pscustomobject]@{
        name = [string]$DecodedFileReport.name
        decodedPath = [string]$DecodedFileReport.decodedPath
        encodedPath = $EncodedPath
        roundTripDecodedPath = $RoundTripDecodedPath
        status = $Status
        encodeExitCode = $EncodeExitCode
        roundTripDecodeExitCode = $RoundTripDecodeExitCode
        encodeMessage = $EncodeMessage
        roundTripDecodeMessage = $RoundTripDecodeMessage
        decodedSha256 = if (Test-Path -LiteralPath ([string]$DecodedFileReport.decodedPath) -PathType Leaf) { (Get-FileHash -LiteralPath ([string]$DecodedFileReport.decodedPath) -Algorithm SHA256).Hash.ToLowerInvariant() } else { "" }
        encodedSha256 = if ($encodedExists) { (Get-FileHash -LiteralPath $EncodedPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { "" }
        roundTripDecodedSha256 = if ($roundTripExists) { (Get-FileHash -LiteralPath $RoundTripDecodedPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { "" }
        encodedBytes = if ($null -ne $encodedItem) { $encodedItem.Length } else { 0 }
        roundTripDecodedBytes = if ($null -ne $roundTripItem) { $roundTripItem.Length } else { 0 }
    }
}

function Write-WorkspaceReport {
    param([object]$Report)

    $json = $Report | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $script:workspaceReportPath -Value $json -Encoding UTF8
    Set-Content -LiteralPath $script:logReportPath -Value $json -Encoding UTF8
}

if ($WriteManagedActions -and -not $Initialize) {
    throw "-WriteManagedActions requires -Initialize."
}

if ($EncodeInitializedProfile -and (-not $Initialize -or -not $WriteManagedActions)) {
    throw "-EncodeInitializedProfile requires -Initialize and -WriteManagedActions."
}

$sourceProfile = Resolve-Path -LiteralPath $SourceProfileDirectory -ErrorAction SilentlyContinue
Assert-DdrtTrue ($null -ne $sourceProfile) "Source profile directory was not found: $SourceProfileDirectory"
Assert-DdrtTrue (Test-Path -LiteralPath $sourceProfile.Path -PathType Container) "Source profile path is not a directory: $($sourceProfile.Path)"

$saveEditorPath = Get-DdrtResolvedPath `
    -Path $SaveEditorJar `
    -Leaf `
    -MissingMessage "DDSaveEditor jar was not found: $SaveEditorJar"

$configFullPath = Get-DdrtResolvedPath `
    -Path $ConfigPath `
    -Leaf `
    -MissingMessage "Config file was not found: $ConfigPath"

if ([string]::IsNullOrWhiteSpace($SessionId)) {
    $SessionId = ((Split-Path -Leaf $sourceProfile.Path) + "_" + (Get-Date -Format "yyyyMMdd_HHmmss_fff"))
}

$outputRootPath = Resolve-WorkspacePath -Path $OutputRoot -Name "OutputRoot"
$workspaceRoot = Join-Path $outputRootPath $SessionId
$decodedSaveDir = Join-Path $workspaceRoot "decoded_save"
$encodedProfileDir = Join-Path $workspaceRoot "encoded_profile"
$roundTripDecodedDir = Join-Path $workspaceRoot "roundtrip_decoded"
$modStateDir = Join-Path $workspaceRoot "mod_state"
$logRoot = Resolve-WorkspacePath -Path "logs\decoded_profile_workspaces" -Name "LogRoot"
$script:workspaceReportPath = Join-Path $workspaceRoot "decoded_profile_workspace_report.json"
$script:logReportPath = Join-Path $logRoot ($SessionId + ".json")

Assert-DdrtTrue (-not (Test-Path -LiteralPath $workspaceRoot)) "Workspace already exists: $workspaceRoot"

New-Item -ItemType Directory -Force -Path $decodedSaveDir | Out-Null
New-Item -ItemType Directory -Force -Path $modStateDir | Out-Null
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

$persistFiles = @(Get-ChildItem -LiteralPath $sourceProfile.Path -Filter "persist*.json" -File | Sort-Object Name)
Assert-DdrtTrue ($persistFiles.Count -gt 0) "No top-level persist*.json files were found in: $($sourceProfile.Path)"

$fileReports = @()
foreach ($file in $persistFiles) {
    $decodedPath = Join-Path $decodedSaveDir $file.Name
    $decodeOutput = & java -jar $saveEditorPath decode --output $decodedPath $file.FullName 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        $fileReports += New-FileReport `
            -SourceFile $file `
            -DecodedPath $decodedPath `
            -Status "decoded" `
            -ExitCode $exitCode `
            -Message (($decodeOutput | Out-String).Trim())
    }
    else {
        $fileReports += New-FileReport `
            -SourceFile $file `
            -DecodedPath $decodedPath `
            -Status "failed" `
            -ExitCode $exitCode `
            -Message (($decodeOutput | Out-String).Trim())
    }
}

$decodedNames = @($fileReports | Where-Object { $_.status -eq "decoded" } | ForEach-Object { $_.name })
$missingRequired = @($requiredInitializationFiles | Where-Object { $decodedNames -notcontains $_ })
$decodeFailed = @($fileReports | Where-Object { $_.status -ne "decoded" })

$initializationReport = $null
$initializationStatus = "skipped"
$initializationMessage = ""
$encodingFileReports = @()
$encodingStatus = "skipped"
$encodingMessage = ""

$report = [pscustomobject]@{
    version = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    sourceProfileDirectory = $sourceProfile.Path
    workspaceRoot = $workspaceRoot
    decodedSaveDirectory = $decodedSaveDir
    encodedProfileDirectory = if ($EncodeInitializedProfile) { $encodedProfileDir } else { "" }
    roundTripDecodedDirectory = if ($EncodeInitializedProfile) { $roundTripDecodedDir } else { "" }
    modStateDirectory = $modStateDir
    reportPath = $script:workspaceReportPath
    logReportPath = $script:logReportPath
    saveEditorJar = $saveEditorPath
    configPath = $configFullPath
    modStateId = $ModStateId
    initializeRequested = [bool]$Initialize
    writeManagedActions = [bool]$WriteManagedActions
    encodeInitializedProfileRequested = [bool]$EncodeInitializedProfile
    decodedFileCount = @($fileReports | Where-Object { $_.status -eq "decoded" }).Count
    failedFileCount = $decodeFailed.Count
    requiredInitializationFiles = $requiredInitializationFiles
    missingRequiredInitializationFiles = $missingRequired
    files = $fileReports
    initialization = [pscustomobject]@{
        status = $initializationStatus
        message = $initializationMessage
        reportPath = ""
        succeeded = $null
        warningCount = 0
        errorCount = 0
    }
    encoding = [pscustomobject]@{
        status = $encodingStatus
        message = $encodingMessage
        encodedProfileDirectory = if ($EncodeInitializedProfile) { $encodedProfileDir } else { "" }
        roundTripDecodedDirectory = if ($EncodeInitializedProfile) { $roundTripDecodedDir } else { "" }
        encodedFileCount = 0
        failedFileCount = 0
        roundTripValidatedFileCount = 0
        files = $encodingFileReports
    }
}

Write-WorkspaceReport -Report $report

if ($decodeFailed.Count -gt 0) {
    throw "Failed to decode $($decodeFailed.Count) persist file(s). Report: $script:workspaceReportPath"
}

if ($Initialize -and $missingRequired.Count -gt 0) {
    throw "Decoded workspace is missing required initialization file(s): $($missingRequired -join ', '). Report: $script:workspaceReportPath"
}

if ($Initialize) {
    try {
        if (-not $NoBuild) {
            & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
            if ($LASTEXITCODE -ne 0) {
                throw "Build failed with exit code $LASTEXITCODE"
            }
        }

        $loaderArgs = @(
            "--config", $configFullPath,
            "--mod-state-dir", $modStateDir,
            "--initialize-decoded-profile",
            "--managed-action-save-dir", $decodedSaveDir,
            "--no-inject"
        )

        if (-not [string]::IsNullOrWhiteSpace($ModStateId)) {
            $loaderArgs += @("--mod-state-id", $ModStateId)
        }

        if ($AllowNonAtomicStateWrites) {
            $loaderArgs += "--allow-non-atomic-state-writes"
        }

        if ($WriteManagedActions) {
            $loaderArgs += "--write-managed-actions"
        }

        Invoke-DdrtLoader -LoaderArgs $loaderArgs
        $decodedInitializationReportPath = Join-Path $projectRoot "logs\decoded_profile_initialization_report.json"
        Assert-DdrtTrue (Test-Path -LiteralPath $decodedInitializationReportPath -PathType Leaf) "Decoded profile initialization report was not written: $decodedInitializationReportPath"
        $initializationReport = Get-Content -Raw -LiteralPath $decodedInitializationReportPath | ConvertFrom-Json
        $initializationStatus = "completed"
        $initializationMessage = ""
    }
    catch {
        $initializationStatus = "failed"
        $initializationMessage = $_.Exception.Message
    }

    $report.initialization = [pscustomobject]@{
        status = $initializationStatus
        message = $initializationMessage
        reportPath = if ($null -ne $initializationReport) { [string]$initializationReport.reportPath } else { "" }
        succeeded = if ($null -ne $initializationReport) { [bool]$initializationReport.succeeded } else { $false }
        warningCount = if ($null -ne $initializationReport) { [int]$initializationReport.warningCount } else { 0 }
        errorCount = if ($null -ne $initializationReport) { [int]$initializationReport.errorCount } else { 1 }
    }
    Write-WorkspaceReport -Report $report

    if ($initializationStatus -ne "completed" -or -not [bool]$report.initialization.succeeded) {
        throw "Decoded profile initialization failed. Report: $script:workspaceReportPath"
    }
}

if ($EncodeInitializedProfile) {
    try {
        New-Item -ItemType Directory -Force -Path $encodedProfileDir, $roundTripDecodedDir | Out-Null

        foreach ($sourceFile in (Get-ChildItem -LiteralPath $sourceProfile.Path -File | Sort-Object Name)) {
            Copy-Item -LiteralPath $sourceFile.FullName -Destination (Join-Path $encodedProfileDir $sourceFile.Name) -Force
        }

        foreach ($fileReport in @($fileReports | Where-Object { $_.status -eq "decoded" } | Sort-Object name)) {
            $encodedPath = Join-Path $encodedProfileDir ([string]$fileReport.name)
            $roundTripDecodedPath = Join-Path $roundTripDecodedDir ([string]$fileReport.name)
            $encodeOutput = & java -jar $saveEditorPath encode --output $encodedPath ([string]$fileReport.decodedPath) 2>&1
            $encodeExitCode = $LASTEXITCODE
            $roundTripDecodeExitCode = -1
            $roundTripDecodeMessage = ""
            $status = "encode-failed"

            if ($encodeExitCode -eq 0) {
                $roundTripOutput = & java -jar $saveEditorPath decode --output $roundTripDecodedPath $encodedPath 2>&1
                $roundTripDecodeExitCode = $LASTEXITCODE
                $roundTripDecodeMessage = (($roundTripOutput | Out-String).Trim())
                if ($roundTripDecodeExitCode -eq 0) {
                    try {
                        Get-Content -Raw -LiteralPath $roundTripDecodedPath | ConvertFrom-Json | Out-Null
                        $status = "roundtrip-validated"
                    }
                    catch {
                        $status = "roundtrip-json-parse-failed"
                        $roundTripDecodeMessage = $_.Exception.Message
                    }
                }
                else {
                    $status = "roundtrip-decode-failed"
                }
            }

            $encodingFileReports += New-EncodingFileReport `
                -DecodedFileReport $fileReport `
                -EncodedPath $encodedPath `
                -RoundTripDecodedPath $roundTripDecodedPath `
                -Status $status `
                -EncodeExitCode $encodeExitCode `
                -RoundTripDecodeExitCode $roundTripDecodeExitCode `
                -EncodeMessage (($encodeOutput | Out-String).Trim()) `
                -RoundTripDecodeMessage $roundTripDecodeMessage
        }

        $failedEncodingReports = @($encodingFileReports | Where-Object { $_.status -ne "roundtrip-validated" })
        $encodingStatus = if ($failedEncodingReports.Count -eq 0) { "completed" } else { "failed" }
        $encodingMessage = if ($failedEncodingReports.Count -eq 0) { "" } else { "Failed to encode or roundtrip-validate $($failedEncodingReports.Count) persist file(s)." }
    }
    catch {
        $encodingStatus = "failed"
        $encodingMessage = $_.Exception.Message
    }

    $report.encoding = [pscustomobject]@{
        status = $encodingStatus
        message = $encodingMessage
        encodedProfileDirectory = $encodedProfileDir
        roundTripDecodedDirectory = $roundTripDecodedDir
        encodedFileCount = @($encodingFileReports | Where-Object { $_.encodeExitCode -eq 0 }).Count
        failedFileCount = @($encodingFileReports | Where-Object { $_.status -ne "roundtrip-validated" }).Count
        roundTripValidatedFileCount = @($encodingFileReports | Where-Object { $_.status -eq "roundtrip-validated" }).Count
        files = $encodingFileReports
    }
    Write-WorkspaceReport -Report $report

    if ($encodingStatus -ne "completed") {
        throw "Decoded profile encoding failed. Report: $script:workspaceReportPath"
    }
}

Write-Output ($report | ConvertTo-Json -Depth 8)
