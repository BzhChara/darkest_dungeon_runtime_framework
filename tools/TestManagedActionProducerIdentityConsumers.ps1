param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "ManagedActionProducerTestHelpers.ps1")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot "state\managed_action_producer_identity_consumer_test\$sessionId"
$artifactRoot = Join-Path $stateRoot "_managed_actions"
$saveRoot = Join-Path $stateRoot "decoded_save"
$pluginSearchRoot = Join-Path $stateRoot "plugins"
$pluginDirectory = Join-Path $pluginSearchRoot "duplicate_rule_identity"
$pluginManifestPath = Join-Path $pluginDirectory "patches.json"
$configPath = Join-Path $stateRoot "config.json"
$overlayReportPath = Join-Path $projectRoot "logs\managed_action_overlay_manifest.json"
$applyReportPath = Join-Path $projectRoot "logs\managed_action_apply_report.json"
$pluginId = "validation.managed_action_producer_identity_consumers"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Value
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $Value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function New-EventRule {
    param(
        [string]$RuleId,
        [string]$EventId,
        [string]$ActionType,
        [string]$Capability,
        [System.Collections.IDictionary]$Arguments
    )

    return [ordered]@{
        id = $RuleId
        enabled = $true
        on = $EventId
        phase = "normal"
        priority = 0
        requiresCapabilities = @($Capability)
        actions = @(
            [ordered]@{
                type = $ActionType
                capability = $Capability
                risk = "managed"
                required = $false
                args = $Arguments
            }
        )
    }
}

function Write-ManagedArtifact {
    param(
        [string]$Name,
        [object]$Producer,
        [System.Collections.IDictionary]$Plan
    )

    $artifact = [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        status = "materialized"
        plan = $Plan
    }
    Add-ManagedActionTestProducer -Artifact $artifact -Producer $Producer | Out-Null
    $path = Join-Path $artifactRoot $Name
    Write-JsonFile -Path $path -Value $artifact
    return $path
}

Push-Location $projectRoot
try {
    if (-not $NoBuild) {
        & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }

    New-Item -ItemType Directory -Force -Path $artifactRoot, $saveRoot, $pluginDirectory | Out-Null

    $questRuleOne = New-EventRule `
        -RuleId "duplicate_quest_board" `
        -EventId "validation.duplicate_quest_board" `
        -ActionType "questBoard.replaceWithFixedSet" `
        -Capability "quest_board.replace_with_fixed_set" `
        -Arguments ([ordered]@{
            target = "profile.quest_board"
            questIds = @("plot_kill_necromancer_3")
            removeCompleted = $false
        })
    $questRuleTwo = New-EventRule `
        -RuleId "duplicate_quest_board" `
        -EventId "validation.duplicate_quest_board" `
        -ActionType "questBoard.replaceWithFixedSet" `
        -Capability "quest_board.replace_with_fixed_set" `
        -Arguments ([ordered]@{
            target = "profile.quest_board"
            questIds = @("plot_kill_prophet_3")
            removeCompleted = $false
        })
    $townEventRuleOne = New-EventRule `
        -RuleId "duplicate_town_event" `
        -EventId "validation.duplicate_town_event" `
        -ActionType "townEvent.overrideCurrent" `
        -Capability "town_event.override_current" `
        -Arguments ([ordered]@{
            target = "profile.townEvent"
            event = [ordered]@{ mode = "suppress" }
        })
    $townEventRuleTwo = New-EventRule `
        -RuleId "duplicate_town_event" `
        -EventId "validation.duplicate_town_event" `
        -ActionType "townEvent.overrideCurrent" `
        -Capability "town_event.override_current" `
        -Arguments ([ordered]@{
            target = "profile.townEvent"
            event = [ordered]@{ mode = "paused" }
        })

    Write-JsonFile -Path $pluginManifestPath -Value ([ordered]@{
        id = $pluginId
        name = "Validation - Managed Action Producer Identity Consumers"
        version = "0.1.0"
        enabled = $true
        capabilities = @(
            "quest_board.replace_with_fixed_set",
            "town_event.override_current"
        )
        virtualFileRules = @()
        mapTemplates = @()
        mapLayoutTemplates = @()
        questChains = @()
        eventRules = @(
            $questRuleOne,
            $questRuleTwo,
            $townEventRuleOne,
            $townEventRuleTwo
        )
        factEventRules = @()
        stateSchema = [ordered]@{}
    })

    $config = Get-Content -Raw -LiteralPath (Join-Path $projectRoot "config\rule_contract_validation_config.json") | ConvertFrom-Json -AsHashtable
    $config.pluginDirectories = @($pluginSearchRoot)
    $config.modStateDirectory = $stateRoot
    $config.enableInjection = $false
    Write-JsonFile -Path $configPath -Value $config

    Invoke-Loader -LoaderArgs @(
        "--config", $configPath,
        "--mod-state-dir", $stateRoot,
        "--validate-only",
        "--no-inject"
    )

    $catalog = Read-ManagedActionProducerCatalog -ProjectRoot $projectRoot
    $questProducers = @($catalog.producers | Where-Object {
        [string]$_.pluginId -eq $pluginId -and
        [string]$_.actionType -eq "questBoard.replaceWithFixedSet"
    } | Sort-Object -Property ruleIndex)
    $townEventProducers = @($catalog.producers | Where-Object {
        [string]$_.pluginId -eq $pluginId -and
        [string]$_.actionType -eq "townEvent.overrideCurrent"
    } | Sort-Object -Property ruleIndex)
    Assert-True ($questProducers.Count -eq 2) "Expected two duplicate-id quest-board producer contracts."
    Assert-True ($townEventProducers.Count -eq 2) "Expected two duplicate-id town-event producer contracts."

    $questArtifactOne = Write-ManagedArtifact `
        -Name "001_quest_rule_index_1.json" `
        -Producer $questProducers[0] `
        -Plan ([ordered]@{
            kind = "questBoard.replaceWithFixedSet"
            effect = "replaceWithFixedSet"
            target = "profile.quest_board"
            arguments = [ordered]@{
                target = "profile.quest_board"
                questIds = @("plot_kill_necromancer_3")
                removeCompleted = $false
            }
        })
    $questArtifactTwo = Write-ManagedArtifact `
        -Name "002_quest_rule_index_2.json" `
        -Producer $questProducers[1] `
        -Plan ([ordered]@{
            kind = "questBoard.replaceWithFixedSet"
            effect = "replaceWithFixedSet"
            target = "profile.quest_board"
            arguments = [ordered]@{
                target = "profile.quest_board"
                questIds = @("plot_kill_prophet_3")
                removeCompleted = $false
            }
        })
    $townEventArtifactOne = Write-ManagedArtifact `
        -Name "003_town_event_rule_index_3.json" `
        -Producer $townEventProducers[0] `
        -Plan ([ordered]@{
            kind = "townEvent.overrideCurrent"
            effect = "overrideCurrent"
            target = "profile.townEvent"
            arguments = [ordered]@{
                target = "profile.townEvent"
                event = [ordered]@{ mode = "suppress" }
            }
        })
    $townEventArtifactTwo = Write-ManagedArtifact `
        -Name "004_town_event_rule_index_4.json" `
        -Producer $townEventProducers[1] `
        -Plan ([ordered]@{
            kind = "townEvent.overrideCurrent"
            effect = "overrideCurrent"
            target = "profile.townEvent"
            arguments = [ordered]@{
                target = "profile.townEvent"
                event = [ordered]@{ mode = "paused" }
            }
        })

    Write-JsonFile -Path (Join-Path $saveRoot "persist.town_event.json") -Value ([ordered]@{
        base_root = [ordered]@{
            current_result_event_id = 123
            has_unclaimed_interaction = $true
            event_cost = [ordered]@{ gold = 250 }
        }
    })

    Remove-Item -LiteralPath $overlayReportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs @(
        "--config", $configPath,
        "--mod-state-dir", $stateRoot,
        "--dry-run",
        "--no-inject"
    )
    Assert-True (Test-Path -LiteralPath $overlayReportPath -PathType Leaf) "Managed action overlay report was not written."
    $overlayReport = Get-Content -Raw -LiteralPath $overlayReportPath | ConvertFrom-Json
    $questOverlays = @($overlayReport.overlays | Where-Object { $_.kind -eq "questBoard.replaceWithFixedSet" })
    Assert-True ($questOverlays.Count -eq 2) "Overlay selection must preserve duplicate rule ids at different rule indices."
    Assert-True ((@($questOverlays.ruleIndex | Sort-Object) -join ',') -eq "1,2") "Overlay report should contain both producer rule indices."

    Remove-Item -LiteralPath $applyReportPath -Force -ErrorAction SilentlyContinue
    Invoke-Loader -LoaderArgs @(
        "--config", $configPath,
        "--mod-state-dir", $stateRoot,
        "--apply-continuous-profile-actions",
        "--managed-action-save-dir", $saveRoot,
        "--no-inject"
    )
    Assert-True (Test-Path -LiteralPath $applyReportPath -PathType Leaf) "Managed action apply report was not written."
    $applyReport = Get-Content -Raw -LiteralPath $applyReportPath | ConvertFrom-Json
    $townEventActions = @($applyReport.actions | Where-Object { $_.actionType -eq "townEvent.overrideCurrent" })
    $expectedTownEventPaths = @($townEventArtifactOne, $townEventArtifactTwo) | Sort-Object
    Assert-True ($townEventActions.Count -eq 2) "Continuous-profile selection must preserve duplicate rule ids at different rule indices."
    Assert-True ((@($townEventActions.artifactPath | Sort-Object) -join '|') -eq ($expectedTownEventPaths -join '|')) "Continuous-profile report should include both producer artifacts."
    Assert-True ((@($townEventActions | Where-Object { $_.status -eq "dry-run" })).Count -eq 2) "Both independent town-event actions should reach the decoded-save consumer."
    Assert-True (Test-Path -LiteralPath $questArtifactOne -PathType Leaf) "Quest-board fixture artifact should remain available."
    Assert-True (Test-Path -LiteralPath $questArtifactTwo -PathType Leaf) "Quest-board fixture artifact should remain available."

    Write-Host "PASS: managed action producer identity consumer selection."
}
finally {
    Pop-Location
}
