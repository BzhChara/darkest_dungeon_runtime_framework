param(
    [string]$ConfigPath = ""
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stateRoot = Join-Path $projectRoot.Path "state\continuous_profile_action_apply_test\$sessionId"
$saveRoot = Join-Path $stateRoot "decoded_save"
$artifactRoot = Join-Path $stateRoot "_managed_actions"
$config = if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    Join-Path $projectRoot.Path "config\rule_contract_validation_config.json"
} else {
    $ConfigPath
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-Utf8Text {
    param([string]$Path)

    return Get-Content -Raw -Encoding UTF8 -LiteralPath $Path
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Value
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $Value | ConvertTo-Json -Depth 32 | Set-Content -Encoding UTF8 -LiteralPath $Path
}

function Invoke-Loader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function New-ManagedActionArtifact {
    param(
        [string]$Path,
        [string]$Type,
        [string]$Target,
        [string]$RuleId,
        [int]$ActionIndex,
        [hashtable]$Arguments
    )

    Write-JsonFile -Path $Path -Value @{
        version = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        status = "materialized"
        eventId = "test.event"
        pluginId = "validation.continuous_profile_apply"
        sourceName = "test"
        sourcePath = "tools/TestContinuousProfileActionApply.ps1"
        loadOrder = 0
        ruleIndex = 0
        ruleId = $RuleId
        actionIndex = $ActionIndex
        action = @{
            type = $Type
            capability = "profile.normalization"
            risk = "decoded-save"
            required = $true
        }
        plan = @{
            kind = $Type
            effect = ($Type -replace '^.*\.', '')
            target = $Target
            arguments = $Arguments
        }
    }
}

function Read-ApplyReport {
    $path = Join-Path $projectRoot.Path "logs\managed_action_apply_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Managed action apply report was not created: $path"
    return Read-Utf8Text -Path $path | ConvertFrom-Json
}

function Count-ObjectProperties {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value.PSObject.Properties).Count
}

New-Item -ItemType Directory -Force -Path $saveRoot, $artifactRoot | Out-Null

Write-JsonFile -Path (Join-Path $saveRoot "persist.town.json") -Value @{
    base_root = @{
        buildings = @{
            stage_coach = @{
                store = @{
                    "0" = @{
                        generated = @{
                            "0" = @{ hero_class = "crusader"; id = "hero_a" }
                            "1" = @{ hero_class = "vestal"; id = "hero_b" }
                        }
                    }
                }
            }
            nomad_wagon = @{
                store = @{
                    "0" = @{
                        generated = @{
                            "0" = @{ id = "trinket_generated_a" }
                        }
                        inventory = @{
                            items = @{
                                "0" = @{ id = "trinket_inventory_a" }
                                "1" = @{ id = "trinket_inventory_b" }
                            }
                        }
                    }
                }
            }
        }
        districts = @{
            buildings = @{}
        }
    }
}

Write-JsonFile -Path (Join-Path $saveRoot "persist.estate.json") -Value @{
    base_root = @{
        wallet = @{
            gold = 50
        }
    }
}

Write-JsonFile -Path (Join-Path $saveRoot "persist.town_event.json") -Value @{
    base_root = @{
        current_result_event_id = 123
        has_unclaimed_interaction = $true
        event_cost = @{
            gold = 250
        }
    }
}

New-ManagedActionArtifact `
    -Path (Join-Path $artifactRoot "001_wallet.setCurrencyAmounts.json") `
    -Type "wallet.setCurrencyAmounts" `
    -Target "profile.wallet" `
    -RuleId "starting_wallet" `
    -ActionIndex 0 `
    -Arguments @{ amounts = @{ gold = 20000 } }

New-ManagedActionArtifact `
    -Path (Join-Path $artifactRoot "002_stagecoach.suppressRecruits.json") `
    -Type "stagecoach.suppressRecruits" `
    -Target "profile.stagecoach" `
    -RuleId "continuous_stagecoach" `
    -ActionIndex 0 `
    -Arguments @{ mode = "empty" }

New-ManagedActionArtifact `
    -Path (Join-Path $artifactRoot "003_town.suppressStoreItems.json") `
    -Type "town.suppressStoreItems" `
    -Target "profile.town.stores" `
    -RuleId "continuous_store" `
    -ActionIndex 0 `
    -Arguments @{
        mode = "empty"
        buildingIds = @("nomad_wagon")
        sections = @("generated", "inventory.items")
    }

New-ManagedActionArtifact `
    -Path (Join-Path $artifactRoot "004_inventory.disableItemSale.json") `
    -Type "inventory.disableItemSale" `
    -Target "profile.inventory" `
    -RuleId "continuous_sale_policy" `
    -ActionIndex 0 `
    -Arguments @{ itemKind = "trinket"; disabled = $true }

New-ManagedActionArtifact `
    -Path (Join-Path $artifactRoot "005_townEvent.overrideCurrent.json") `
    -Type "townEvent.overrideCurrent" `
    -Target "profile.townEvent" `
    -RuleId "continuous_town_event" `
    -ActionIndex 0 `
    -Arguments @{
        event = @{
            mode = "suppress"
            message = "Enjoy purgatory."
        }
    }

$baseArgs = @("--config", $config, "--mod-state-dir", $stateRoot, "--apply-continuous-profile-actions", "--managed-action-save-dir", $saveRoot, "--no-inject")

Invoke-Loader -LoaderArgs $baseArgs
$dryRunReport = Read-ApplyReport
Assert-True ($dryRunReport.applyMode -eq "continuousProfile") "Dry-run report should use continuousProfile apply mode."
Assert-True ($dryRunReport.artifactCount -eq 4) "Dry-run should select only the four continuous artifacts."
Assert-True ($dryRunReport.actions.actionType -notcontains "wallet.setCurrencyAmounts") "Dry-run should not apply one-shot wallet artifacts."

$town = Read-Utf8Text -Path (Join-Path $saveRoot "persist.town.json") | ConvertFrom-Json
Assert-True ((Count-ObjectProperties $town.base_root.buildings.stage_coach.store."0".generated) -eq 2) "Dry-run should not clear stagecoach recruits."
Assert-True ((Count-ObjectProperties $town.base_root.buildings.nomad_wagon.store."0".inventory.items) -eq 2) "Dry-run should not clear store inventory."

Invoke-Loader -LoaderArgs ($baseArgs + @("--write-managed-actions"))
$writeReport = Read-ApplyReport
Assert-True ($writeReport.applyMode -eq "continuousProfile") "Write report should use continuousProfile apply mode."
Assert-True ($writeReport.appliedActionCount -eq 4) "Write pass should apply four continuous artifacts."
Assert-True ($writeReport.actions.actionType -notcontains "wallet.setCurrencyAmounts") "Write pass should not apply one-shot wallet artifacts."

$town = Read-Utf8Text -Path (Join-Path $saveRoot "persist.town.json") | ConvertFrom-Json
Assert-True ((Count-ObjectProperties $town.base_root.buildings.stage_coach.store."0".generated) -eq 0) "Write pass should clear stagecoach recruits."
Assert-True ((Count-ObjectProperties $town.base_root.buildings.nomad_wagon.store."0".generated) -eq 0) "Write pass should clear generated store items."
Assert-True ((Count-ObjectProperties $town.base_root.buildings.nomad_wagon.store."0".inventory.items) -eq 0) "Write pass should clear store inventory items."

$estate = Read-Utf8Text -Path (Join-Path $saveRoot "persist.estate.json") | ConvertFrom-Json
Assert-True ([int]$estate.base_root.wallet.gold -eq 50) "Continuous apply must not replay starting wallet initialization."

$townEvent = Read-Utf8Text -Path (Join-Path $saveRoot "persist.town_event.json") | ConvertFrom-Json
Assert-True ([int]$townEvent.base_root.current_result_event_id -eq 0) "Write pass should suppress current town event."
Assert-True (-not [bool]$townEvent.base_root.has_unclaimed_interaction) "Write pass should clear unclaimed event interaction."

$profilePolicy = Read-Utf8Text -Path (Join-Path $saveRoot "_ddrt_profile_policy.json") | ConvertFrom-Json
Assert-True ([bool]$profilePolicy.profilePolicies.inventory.saleDisabled.trinket) "Write pass should record trinket sale-disable policy."
Assert-True ($profilePolicy.profilePolicies.townEvent.mode -eq "suppress") "Write pass should record town-event policy."

Write-Host "Continuous profile action apply test passed: $sessionId"
