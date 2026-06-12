param(
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\quest_chain_board_artifact_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\quest_chain_board_artifact_test\$sessionId"
$pluginRoot = Join-Path $testRoot "plugins\quest_chain_board_artifact"
$saveRoot = Join-Path $stateRoot "decoded_save"
$configPath = Join-Path $projectRoot.Path "config\_quest_chain_board_artifact_test_$sessionId.json"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Read-ApplyReport {
    $path = Join-Path $projectRoot.Path "logs\managed_action_apply_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Managed action apply report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-QuestBoardPreviewReport {
    $path = Join-Path $projectRoot.Path "logs\quest_board_preview_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Quest board preview report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-QuestBoardLaunchPreflightReport {
    $path = Join-Path $projectRoot.Path "logs\quest_board_launch_preflight_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Quest board launch preflight report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedQuest {
    $path = Join-Path $saveRoot "persist.quest.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded quest file was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Get-QuestIds {
    param([object]$Quest)

    return @($Quest.base_root.quests.PSObject.Properties |
        Sort-Object { [int]$_.Name } |
        ForEach-Object { [string]$_.Value.id })
}

function Get-QuestById {
    param(
        [object]$Quest,
        [string]$Id
    )

    $entry = @($Quest.base_root.quests.PSObject.Properties |
        ForEach-Object { $_.Value } |
        Where-Object { $_.id -eq $Id }) | Select-Object -First 1
    Assert-True ($null -ne $entry) "Expected quest board entry: $Id"
    return $entry
}

function Write-DecodedQuestFixture {
    $path = Join-Path $saveRoot "persist.quest.json"
    @'
{
  "base_root": {
    "version": 41,
    "quests": {
      "0": {
        "id": "plot_tutorial_crypts",
        "map_name": "tutorial_crypts",
        "torch_setting": "",
        "raid_rules_override": "",
        "is_plot_quest": true,
        "type": "explore",
        "dungeon": "crypts",
        "difficulty": 1,
        "length": 1,
        "counted_in_generation": true,
        "goal_ids": [
          "explore_all_rooms"
        ],
        "progression_goal_ids": 0,
        "use_default_progression_goals": true,
        "completion_reward": {
          "resolve_xp": 2,
          "resolve_xp_per_wave_kill": 0,
          "items_definition": {
            "items": {
              "0": {
                "id": "",
                "type": "gold",
                "amount": 3000
              }
            }
          },
          "additional_threshold_trinket_rewards": {},
          "trinket_retention_ids": [],
          "max_times_dungeon_xp_awarded": 0
        },
        "threshold_rewards": {},
        "completion_threshold": 0,
        "is_from_town_event": false
      }
    },
    "trinket_retention_ids": [],
    "plot_quest_total": 44
  }
}
'@ | Set-Content -LiteralPath $path -Encoding UTF8
}

Push-Location $projectRoot.Path
try {
    New-Item -ItemType Directory -Force -Path $pluginRoot, $saveRoot | Out-Null

    $manifest = [ordered]@{
        id = "validation.quest_chain_board_artifact"
        name = "Validation - Quest Chain Board Artifact"
        version = "0.1.0"
        enabled = $true
        capabilities = @("quest.chain.define", "quest_board.replace_with_fixed_set")
        virtualFileRules = @()
        mapTemplates = @()
        mapLayoutTemplates = @()
        questChains = @(
            [ordered]@{
                id = "quest_chain_board_probe"
                name = "Quest Chain Board Probe"
                mode = "fixed_order"
                questBoard = [ordered]@{
                    enabled = $true
                    mode = "replaceWithFixedSet"
                    questIdSource = "sourceQuestId"
                    removeCompleted = $false
                }
                stages = @(
                    [ordered]@{
                        id = "stage_necromancer"
                        name = "Necromancer"
                        order = 0
                        sourceQuestId = "plot_kill_necromancer_3"
                        targetQuestId = "quest_chain_necromancer"
                        region = "crypts"
                        difficulty = 5
                        tags = @("boss", "quest_board")
                    },
                    [ordered]@{
                        id = "stage_prophet"
                        name = "Prophet"
                        order = 1
                        sourceQuestId = "plot_kill_prophet_3"
                        targetQuestId = "quest_chain_prophet"
                        region = "ruins"
                        difficulty = 5
                        tags = @("boss", "quest_board")
                    }
                )
            }
        )
        eventRules = @()
        factEventRules = @()
        stateSchema = [ordered]@{}
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $pluginRoot "patches.json") -Encoding UTF8

    $config = [ordered]@{
        gameExecutablePath = (Join-Path $GameDirectory "_windows\win64\Darkest.exe")
        gameWorkingDirectory = $GameDirectory
        runtimeDllPath = "./runtime/bin/x64/Release/RuntimeHook.dll"
        logDirectory = "./logs"
        modStateDirectory = $stateRoot
        enableInjection = $false
        killGameOnInjectionFailure = $false
        startSuspendedForInjection = $false
        fileIoObserveOnly = $true
        fileIoLogExtensions = @(".json")
        fileIoMaxLogEntries = 20
        fileIoDeduplicate = $true
        eventProbeEnabled = $false
        pluginDirectories = @($pluginRoot)
        pluginPatchManifestName = "patches.json"
        virtualFileEnabled = $true
        virtualFileTarget = ""
        virtualFileFind = ""
        virtualFileReplace = ""
        virtualFileRules = @()
    }
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8

    Write-DecodedQuestFixture
    $quest = Read-DecodedQuest
    Assert-True ((Get-QuestIds -Quest $quest).Count -eq 1) "Fixture should start with one quest board entry."
    Assert-True ((Get-QuestIds -Quest $quest) -contains "plot_tutorial_crypts") "Fixture should start with tutorial quest."

    Invoke-Loader -LoaderArgs @("--config", $configPath, "--validate-only", "--no-inject")

    $questChainReportPath = Join-Path $stateRoot "_quest_chains\validation.quest_chain_board_artifact\001_quest_chain_board_probe.validation.json"
    $questChainManagedReportPath = Join-Path $stateRoot "_quest_chains\validation.quest_chain_board_artifact\001_quest_chain_board_probe.managed.quest_board.json"
    $questChainManagedArtifactPath = Join-Path $stateRoot "_managed_actions\static_validation.quest_chain_board_artifact_001_quest_chain_board_probe_questBoard.replaceWithFixedSet.json"
    Assert-True (Test-Path -LiteralPath $questChainReportPath -PathType Leaf) "Quest chain validation report was not created: $questChainReportPath"
    Assert-True (Test-Path -LiteralPath $questChainManagedReportPath -PathType Leaf) "Quest chain managed report was not created: $questChainManagedReportPath"
    Assert-True (Test-Path -LiteralPath $questChainManagedArtifactPath -PathType Leaf) "Quest chain managed artifact was not created: $questChainManagedArtifactPath"

    $artifactCount = @(Get-ChildItem -LiteralPath (Join-Path $stateRoot "_managed_actions") -Filter "*.json").Count
    Assert-True ($artifactCount -eq 1) "Quest chain materialization should produce exactly one deterministic managed artifact."

    $managedArtifact = Get-Content -Raw -LiteralPath $questChainManagedArtifactPath | ConvertFrom-Json
    Assert-True ($managedArtifact.status -eq "materialized") "Quest chain artifact should be materialized."
    Assert-True ($managedArtifact.action.type -eq "questBoard.replaceWithFixedSet") "Quest chain artifact action type mismatch."
    Assert-True ($managedArtifact.plan.arguments.questIds[0] -eq "plot_kill_necromancer_3") "First materialized quest id mismatch."
    Assert-True ($managedArtifact.plan.arguments.questIds[1] -eq "plot_kill_prophet_3") "Second materialized quest id mismatch."

    Invoke-Loader -LoaderArgs @("--config", $configPath, "--preview-quest-board", "--no-inject")
    $previewReport = Read-QuestBoardPreviewReport
    Assert-True ([bool]$previewReport.succeeded) "Quest board preview should succeed."
    Assert-True ([int]$previewReport.artifactCount -eq 1) "Quest board preview should inspect one managed artifact."
    Assert-True ([int]$previewReport.questBoardArtifactCount -eq 1) "Quest board preview should find one questBoard artifact."
    Assert-True ([int]$previewReport.wouldApplyArtifactCount -eq 1) "Quest board preview should report one applicable artifact."
    Assert-True ([int]$previewReport.finalActiveQuestCount -eq 2) "Quest board preview should report two final active quests."
    Assert-True ([int]$previewReport.errorCount -eq 0) "Quest board preview should not report errors."
    Assert-True ($previewReport.finalActiveQuests[0].questId -eq "plot_kill_necromancer_3") "Quest board preview first quest id mismatch."
    Assert-True ($previewReport.finalActiveQuests[0].stageId -eq "stage_necromancer") "Quest board preview first stage id mismatch."
    Assert-True ($previewReport.finalActiveQuests[0].dungeon -eq "crypts") "Quest board preview should preserve content-defined necromancer dungeon."
    Assert-True ([int]$previewReport.finalActiveQuests[0].contentDifficulty -eq 5) "Quest board preview should preserve content-defined necromancer difficulty."
    Assert-True ($previewReport.finalActiveQuests[1].questId -eq "plot_kill_prophet_3") "Quest board preview second quest id mismatch."
    Assert-True ($previewReport.finalActiveQuests[1].stageId -eq "stage_prophet") "Quest board preview second stage id mismatch."

    Invoke-Loader -LoaderArgs @("--config", $configPath, "--dry-run", "--no-inject")
    $preflightReport = Read-QuestBoardLaunchPreflightReport
    Assert-True ([bool]$preflightReport.succeeded) "Quest board launch preflight should succeed."
    Assert-True ($preflightReport.mode -eq "dry-run") "Quest board launch preflight should record dry-run mode."
    Assert-True ([bool]$preflightReport.questBoardPreviewSucceeded) "Quest board launch preflight should include a successful preview."
    Assert-True ([bool]$preflightReport.hasQuestBoardCandidate) "Quest board launch preflight should detect a quest board candidate."
    Assert-True ($preflightReport.candidateQuestBoardStatus -eq "previewOnly") "Quest board launch preflight should mark quest board as preview-only."
    Assert-True ([int]$preflightReport.candidateQuestCount -eq 2) "Quest board launch preflight should report two candidate quests."
    Assert-True ($preflightReport.runtimeQuestBoardConsumerStatus -eq "notImplemented") "Quest board launch preflight should not claim a live quest-board consumer exists."
    Assert-True (-not [bool]$preflightReport.willRuntimeReplaceQuestBoard) "Quest board launch preflight must not claim runtime quest board replacement."
    Assert-True (-not [bool]$preflightReport.willRuntimeForceQuestContentAvailable) "This test has no fixed-stage quest content overlay."
    Assert-True ([int]$preflightReport.warningCount -eq 1) "Quest board launch preflight should warn that live quest-board consumer is not implemented."
    Assert-True ([int]$preflightReport.errorCount -eq 0) "Quest board launch preflight should not report errors."
    Assert-True ($preflightReport.candidateQuests[0].questId -eq "plot_kill_necromancer_3") "Quest board launch preflight first candidate quest id mismatch."

    Invoke-Loader -LoaderArgs @("--config", $configPath, "--apply-managed-actions", "--managed-action-save-dir", $saveRoot, "--no-inject")
    $dryRunReport = Read-ApplyReport
    Assert-True ([bool]$dryRunReport.dryRun) "First apply pass should be dry-run by default."
    Assert-True ([int]$dryRunReport.artifactCount -eq 1) "Dry-run should inspect the quest chain managed artifact only."
    Assert-True ([int]$dryRunReport.supportedActionCount -eq 1) "Dry-run should recognize the questBoard action."
    Assert-True ([int]$dryRunReport.dryRunActionCount -eq 1) "Dry-run should report one dry-run action."
    Assert-True ([int]$dryRunReport.appliedActionCount -eq 0) "Dry-run should not write actions."
    Assert-True ([int]$dryRunReport.failedActionCount -eq 0) "Dry-run should not fail."
    Assert-True ([int]$dryRunReport.changedFileCount -eq 1) "Dry-run should report one would-change decoded save file."

    $quest = Read-DecodedQuest
    Assert-True ((Get-QuestIds -Quest $quest).Count -eq 1) "Dry-run must not replace quest board entries."
    Assert-True ((Get-QuestIds -Quest $quest) -contains "plot_tutorial_crypts") "Dry-run must keep tutorial quest."

    Invoke-Loader -LoaderArgs @("--config", $configPath, "--apply-managed-actions", "--write-managed-actions", "--managed-action-save-dir", $saveRoot, "--no-inject")
    $writeReport = Read-ApplyReport
    Assert-True (-not [bool]$writeReport.dryRun) "Write pass should record dryRun=false."
    Assert-True ([int]$writeReport.artifactCount -eq 1) "Write pass should inspect the quest chain managed artifact only."
    Assert-True ([int]$writeReport.supportedActionCount -eq 1) "Write pass should recognize one supported action."
    Assert-True ([int]$writeReport.appliedActionCount -eq 1) "Write pass should apply one action."
    Assert-True ([int]$writeReport.failedActionCount -eq 0) "Write pass should not fail."
    Assert-True ([int]$writeReport.changedFileCount -eq 1) "Write pass should change persist.quest.json."

    $quest = Read-DecodedQuest
    $questIds = @(Get-QuestIds -Quest $quest)
    Assert-True ($questIds.Count -eq 2) "Write pass should replace quest board with two quest-chain stages."
    Assert-True ($questIds[0] -eq "plot_kill_necromancer_3") "Write pass should preserve quest chain order for necromancer."
    Assert-True ($questIds[1] -eq "plot_kill_prophet_3") "Write pass should preserve quest chain order for prophet."
    $necroQuest = Get-QuestById -Quest $quest -Id "plot_kill_necromancer_3"
    Assert-True ([string]$necroQuest.dungeon -eq "crypts") "Quest board writer should preserve content-defined necromancer dungeon."
    Assert-True ([int]$necroQuest.difficulty -eq 5) "Quest board writer should preserve content-defined necromancer difficulty."
    Assert-True (@($necroQuest.goal_ids)[0] -eq "kill_necromancer_C") "Quest board writer should preserve content-defined necromancer goal."

    Invoke-Loader -LoaderArgs @("--config", $configPath, "--apply-managed-actions", "--write-managed-actions", "--managed-action-save-dir", $saveRoot, "--no-inject")
    $secondWriteReport = Read-ApplyReport
    Assert-True ([int]$secondWriteReport.artifactCount -eq 1) "Repeated apply should still see one deterministic quest chain artifact."
    Assert-True ([int]$secondWriteReport.appliedActionCount -eq 1) "Repeated apply should still process the supported action."
    Assert-True ([int]$secondWriteReport.changedFileCount -eq 0) "Repeated write should be idempotent against an already-normalized quest board."

    $artifactCount = @(Get-ChildItem -LiteralPath (Join-Path $stateRoot "_managed_actions") -Filter "*.json").Count
    Assert-True ($artifactCount -eq 1) "Repeated loader runs must not accumulate duplicate quest chain managed artifacts."

    Write-Host "PASS: questChains materialize and preview a questBoard artifact that dry-runs and writes decoded persist.quest.json."
}
finally {
    Pop-Location
}
