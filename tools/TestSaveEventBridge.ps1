param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\save_event_bridge_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\save_event_bridge_test\$sessionId"
$payloadRoot = Join-Path $testRoot "payloads"

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

function Read-ChallengeState {
    $path = Join-Path $stateRoot "validation.challenge_run_contract.json"
    Assert-True (Test-Path -LiteralPath $path) "Sidecar state was not created: $path"
    $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    return $document.state.challengeRun
}

New-Item -ItemType Directory -Force -Path $testRoot, $payloadRoot | Out-Null

$baseArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", "validation.challenge_run_contract",
    "--mod-state-dir", $stateRoot
)

Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.run_started"))

$selectionPayloadPath = Write-JsonPayload "selection_confirmed.json" ([pscustomobject]@{
    stageId = "stage_1_necromancer"
    selectedHeroIds = @("1", "2", "7", "8")
    selectedTrinketIds = @("berserk_mask", "immunity_mask", "fortunate_armlet", "sb_4")
})
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "challenge.stage_selection_confirmed", "--event-payload-file", $selectionPayloadPath))

$questId = "plot_kill_necromancer_1"
$questHash = Get-DsonHashSigned $questId
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
    }
})

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $saveReportPath))

$state = Read-ChallengeState
Assert-True ([int]$state.currentStageIndex -eq 1) "Save event bridge should advance currentStageIndex to 1."
Assert-True ((Convert-ToArray $state.completedStageIds) -contains "stage_1_necromancer") "Save event bridge should record completed stage id."
Assert-True ((Convert-ToArray $state.usedHeroIds).Count -eq 4) "Save event bridge should consume locked heroes."
Assert-True ((Convert-ToArray $state.usedTrinketIds).Count -eq 4) "Save event bridge should consume locked trinkets."
Assert-True ($null -eq $state.lockedStageSelection) "Save event bridge completion should clear locked selection."

$bridgeReportPath = Join-Path $projectRoot.Path "logs\save_event_bridge_report.json"
$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
Assert-True ([int]$bridgeReport.inferredEventCount -eq 1) "Save event bridge should infer exactly one event."
$executed = @(Convert-ToArray $bridgeReport.plugins | Where-Object { $_.status -eq "event-executed" })
Assert-True ($executed.Count -eq 1) "Save event bridge should execute one matching plugin event."
Assert-True ($executed[0].eventId -eq "challenge.stage_completed") "Save event bridge should emit challenge.stage_completed."

Invoke-Loader -LoaderArgs ($baseArgs + @("--infer-save-events", "--save-state-report", $saveReportPath))

$stateAfterNoMatch = Read-ChallengeState
Assert-True ([int]$stateAfterNoMatch.currentStageIndex -eq 1) "No-match save event bridge pass should leave currentStageIndex unchanged."
$bridgeReport = Get-Content -Raw -LiteralPath $bridgeReportPath | ConvertFrom-Json
Assert-True ([int]$bridgeReport.inferredEventCount -eq 0) "No-match save event bridge pass should not infer an event."
$notMatched = @(Convert-ToArray $bridgeReport.plugins | Where-Object { $_.status -eq "predicate-not-matched" })
Assert-True ($notMatched.Count -eq 2) "No-match save event bridge pass should leave both fact-event rules unmatched."

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

Write-Host "PASS: save event bridge inferred and executed challenge completion."
