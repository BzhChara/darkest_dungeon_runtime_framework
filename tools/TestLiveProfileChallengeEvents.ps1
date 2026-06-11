param(
    [string]$LiveProfileDirectory = "E:\Steam\userdata\1097809614\262060\remote\profile_3",
    [string]$ConfigPath = "config\rule_contract_validation_config.json",
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon",
    [string]$ExportScriptPath = "tools\ExportSaveSampleFacts.ps1",
    [string]$ModStateDirectory = "",
    [ValidateSet("Auto", "None", "SelectionConfirmed", "StageCompleted", "StageFailed")]
    [string]$ExpectedOutcome = "Auto",
    [switch]$UseExistingModState,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$propertyFlags = [System.Reflection.BindingFlags]"Public,Instance,IgnoreCase"

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

function Get-ObjectProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $property = $Value.PSObject.Properties |
            Where-Object { $_.Name -ieq $Name } |
            Select-Object -First 1
        if ($null -eq $property) {
            return $null
        }

        return $property.Value
    }

    $propertyInfo = $Value.GetType().GetProperty($Name, $propertyFlags)
    if ($null -eq $propertyInfo) {
        return $null
    }

    return $propertyInfo.GetValue($Value)
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Get-PathValue {
    param(
        [object]$Root,
        [string]$Path
    )

    $current = $Root
    foreach ($part in $Path -split "\.") {
        if ($null -eq $current) {
            return $null
        }

        $current = Get-ObjectProperty $current $part
    }

    return $current
}

function Test-ContainsValue {
    param(
        [object]$Actual,
        [object]$Expected
    )

    foreach ($item in Convert-ToArray $Actual) {
        if ([string]$item -eq [string]$Expected) {
            return $true
        }
    }

    return $false
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Read-ChallengeState {
    param([string]$Root)

    $path = Join-Path $Root "validation.challenge_run_contract.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Challenge sidecar state was not found: $path"
    $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    return Get-PathValue $document "state.challengeRun"
}

function Get-ExecutedEventIds {
    param([object]$BridgeReport)

    return @(
        Convert-ToArray (Get-ObjectProperty $BridgeReport "plugins") |
            Where-Object { (Get-ObjectProperty $_ "status") -eq "event-executed" } |
            ForEach-Object { Get-ObjectProperty $_ "eventId" }
    )
}

function Get-LiveOutcome {
    param(
        [object]$Facts,
        [object]$ChallengeState
    )

    $stage = Get-ObjectProperty $ChallengeState "currentStage"
    if ($null -eq $stage) {
        return "None"
    }

    $sourceQuestId = [string](Get-ObjectProperty $stage "sourceQuestId")
    $partySize = [int](Get-ObjectProperty $ChallengeState "partySize")
    $lockedSelection = Get-ObjectProperty $ChallengeState "lockedStageSelection"

    $raidId = [string](Get-PathValue $Facts "raid.instance.id")
    $raidHeroCountValue = Get-PathValue $Facts "raid.party.heroCount"
    if (-not [string]::IsNullOrWhiteSpace($raidId) -and
        $raidId -eq $sourceQuestId -and
        $null -ne $raidHeroCountValue -and
        [int]$raidHeroCountValue -eq $partySize) {
        return "SelectionConfirmed"
    }

    $latestCompletedQuestNames = Get-PathValue $Facts "campaignLog.latestCompletedPartyRaidRecord.questId.names"
    $latestCompletedSuccess = Get-PathValue $Facts "campaignLog.latestCompletedPartyRaidRecord.success"
    if ((Test-ContainsValue $latestCompletedQuestNames $sourceQuestId) -and $latestCompletedSuccess -eq $true) {
        return "StageCompleted"
    }

    $lastRaidQuestNames = Get-PathValue $Facts "progression.lastRaidQuest.names"
    $lastRaidSuccess = Get-PathValue $Facts "progression.lastRaidSuccess"
    if ($null -ne $lockedSelection -and (Test-ContainsValue $lastRaidQuestNames $sourceQuestId)) {
        if ($lastRaidSuccess -eq $true) {
            return "StageCompleted"
        }

        if ($lastRaidSuccess -eq $false) {
            return "StageFailed"
        }
    }

    return "None"
}

Push-Location $projectRoot.Path
try {
    $profilePath = Resolve-Path -LiteralPath $LiveProfileDirectory -ErrorAction SilentlyContinue
    Assert-True ($null -ne $profilePath) "Live profile directory was not found: $LiveProfileDirectory"

    $configFullPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $ConfigPath) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $configFullPath) "Config file was not found: $ConfigPath"

    $assemblyFullPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $AssemblyPath) -ErrorAction SilentlyContinue
    if ($null -eq $assemblyFullPath -and -not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
        $assemblyFullPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $AssemblyPath) -ErrorAction SilentlyContinue
    }
    Assert-True ($null -ne $assemblyFullPath) "Built launcher assembly was not found at '$AssemblyPath'. Run: dotnet build launcher/DDRuntimeLoader.csproj -c Release"

    $exportScriptFullPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $ExportScriptPath) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $exportScriptFullPath) "Export script was not found: $ExportScriptPath"

    $gameDirectoryPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $GameDirectory) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $gameDirectoryPath) "Game directory was not found: $GameDirectory"

    $stateRoot = if ([string]::IsNullOrWhiteSpace($ModStateDirectory)) {
        Join-Path $projectRoot.Path "state\live_profile_challenge_event_test\$sessionId"
    } else {
        Resolve-ProjectPath $ModStateDirectory
    }

    if (-not $UseExistingModState) {
        New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    } else {
        Assert-True (Test-Path -LiteralPath $stateRoot -PathType Container) "Existing mod state directory was not found: $stateRoot"
    }

    $exportScript = $exportScriptFullPath.Path
    $exportOutput = & $exportScript `
        -SampleDirectory $profilePath.Path `
        -AssemblyPath $assemblyFullPath.Path `
        -GameDirectory $gameDirectoryPath.Path `
        -SessionPrefix "live_profile_challenge_events"

    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Save fact export failed with exit code $LASTEXITCODE"
    }

    $exportReport = ($exportOutput | Out-String) | ConvertFrom-Json
    Assert-True ([int]$exportReport.accessIssueCount -eq 0) "Save fact export reported access issues: $($exportReport.accessIssueCount)"

    $saveStateReportPath = Resolve-Path -LiteralPath ([string]$exportReport.output) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $saveStateReportPath) "Save fact export report was not written: $($exportReport.output)"

    $saveStateReport = Get-Content -Raw -LiteralPath $saveStateReportPath.Path | ConvertFrom-Json
    Assert-True ((Convert-ToArray (Get-ObjectProperty $saveStateReport "accessIssues")).Count -eq 0) "Save fact export report contains access issues."

    $baseArgs = @(
        "--config", $configFullPath.Path,
        "--no-inject",
        "--allow-non-atomic-state-writes",
        "--mod-state-id", "validation.challenge_run_contract",
        "--mod-state-dir", $stateRoot
    )

    if (-not $UseExistingModState) {
        Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
        Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.run_started"))
    }

    $preBridgeState = Read-ChallengeState -Root $stateRoot
    $facts = Get-ObjectProperty $saveStateReport "facts"
    Assert-True ($null -ne $facts) "Save state report does not contain facts."

    $actualExpectedOutcome = if ($ExpectedOutcome -eq "Auto") {
        Get-LiveOutcome $facts $preBridgeState
    } else {
        $ExpectedOutcome
    }

    Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $saveStateReportPath.Path))

    $bridgeReportPath = Join-Path $projectRoot.Path "logs\save_event_bridge_report.json"
    Assert-True (Test-Path -LiteralPath $bridgeReportPath -PathType Leaf) "Save event bridge report was not written: $bridgeReportPath"
    $bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
    $postBridgeState = Read-ChallengeState -Root $stateRoot
    $executedEventIds = Get-ExecutedEventIds $bridgeReport
    $issues = Convert-ToArray (Get-ObjectProperty $bridgeReport "issues")
    $errorIssues = @($issues | Where-Object { (Get-ObjectProperty $_ "severity") -eq "error" })
    Assert-True ($errorIssues.Count -eq 0) "Save event bridge reported error issues."

    $currentStage = Get-ObjectProperty $preBridgeState "currentStage"
    $stageId = [string](Get-ObjectProperty $currentStage "id")

    switch ($actualExpectedOutcome) {
        "None" {
            Assert-True ([int](Get-ObjectProperty $bridgeReport "inferredEventCount") -eq 0) "Expected no inferred challenge events from the live profile."
            Assert-True ($executedEventIds.Count -eq 0) "Expected no executed challenge events from the live profile."
            Assert-True ([int](Get-ObjectProperty $postBridgeState "currentStageIndex") -eq [int](Get-ObjectProperty $preBridgeState "currentStageIndex")) "No-event bridge pass should not advance the challenge stage."
        }
        "SelectionConfirmed" {
            Assert-True ($executedEventIds.Count -eq 1) "Expected exactly one executed event for active live raid selection."
            Assert-True ($executedEventIds[0] -eq "challenge.stage_selection_confirmed") "Expected challenge.stage_selection_confirmed, found '$($executedEventIds -join ",")'."
            $lockedSelection = Get-ObjectProperty $postBridgeState "lockedStageSelection"
            Assert-True ($null -ne $lockedSelection) "Selection confirmation should lock the live challenge selection."
            Assert-True ((Get-ObjectProperty $lockedSelection "stageId") -eq $stageId) "Locked selection should target stage '$stageId'."
            Assert-True ((Convert-ToArray (Get-ObjectProperty $lockedSelection "heroIds")).Count -eq [int](Get-ObjectProperty $preBridgeState "partySize")) "Locked selection should contain the configured party size."
        }
        "StageCompleted" {
            Assert-True ((Test-ContainsValue $executedEventIds "challenge.stage_completed")) "Expected challenge.stage_completed to execute."
            Assert-True ([int](Get-ObjectProperty $postBridgeState "currentStageIndex") -eq ([int](Get-ObjectProperty $preBridgeState "currentStageIndex") + 1)) "Stage completion should advance currentStageIndex by one."
            Assert-True ((Test-ContainsValue (Get-ObjectProperty $postBridgeState "completedStageIds") $stageId)) "Stage completion should record completed stage '$stageId'."
            Assert-True ($null -eq (Get-ObjectProperty $postBridgeState "lockedStageSelection")) "Stage completion should clear the locked selection."
            Assert-True ((Convert-ToArray (Get-ObjectProperty $postBridgeState "usedHeroIds")).Count -gt 0) "Stage completion should consume selected heroes."
        }
        "StageFailed" {
            Assert-True ($executedEventIds.Count -eq 1) "Expected exactly one executed event for failed live stage."
            Assert-True ($executedEventIds[0] -eq "challenge.stage_failed") "Expected challenge.stage_failed, found '$($executedEventIds -join ",")'."
            Assert-True ($null -ne (Get-ObjectProperty $postBridgeState "lockedStageSelection")) "Stage failure should keep the locked selection for retry."
            $attempts = Convert-ToArray (Get-ObjectProperty $postBridgeState "stageAttempts")
            Assert-True (($attempts | Where-Object { (Get-ObjectProperty $_ "stageId") -eq $stageId }).Count -gt 0) "Stage failure should record a failed attempt for '$stageId'."
        }
    }

    Write-Host "PASS: live profile challenge event bridge outcome '$actualExpectedOutcome' validated."
    Write-Host "Save state report: $($saveStateReportPath.Path)"
    Write-Host "Bridge report: $bridgeReportPath"
    Write-Host "State directory: $stateRoot"
    Write-Host "Executed events: $(if ($executedEventIds.Count -eq 0) { '<none>' } else { $executedEventIds -join ', ' })"
}
finally {
    Pop-Location
}
