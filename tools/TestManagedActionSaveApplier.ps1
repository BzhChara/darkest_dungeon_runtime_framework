param(
    [string]$ConfigPath = "config\rule_contract_validation_config.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\managed_action_save_applier_test\$sessionId"
$stateRoot = Join-Path $projectRoot.Path "state\managed_action_save_applier_test\$sessionId"
$saveRoot = Join-Path $stateRoot "decoded_save"
$sourceSaveRoot = Join-Path $projectRoot.Path ".research\DDSaveEditor-v0.0.70\decoded_current"
$pluginId = "validation.boss_gauntlet_campaign_contract"

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

function Read-ApplyReport {
    $path = Join-Path $projectRoot.Path "logs\managed_action_apply_report.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Managed action apply report was not created: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Read-DecodedEstate {
    $path = Join-Path $saveRoot "persist.estate.json"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Decoded estate file was not copied: $path"
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Get-WalletAmount {
    param(
        [object]$Estate,
        [string]$Currency
    )

    $entries = @($Estate.base_root.wallet.PSObject.Properties | ForEach-Object { $_.Value })
    $entry = @($entries | Where-Object { $_.type -eq $Currency }) | Select-Object -First 1
    Assert-True ($null -ne $entry) "Wallet currency was not found: $Currency"
    return [int]$entry.amount
}

function Get-TrinketAmount {
    param(
        [object]$Estate,
        [string]$Id
    )

    $items = $Estate.base_root.trinkets.items
    if ($null -eq $items) {
        return $null
    }

    $entries = @($items.PSObject.Properties | ForEach-Object { $_.Value })
    $entry = @($entries | Where-Object { $_.type -eq "trinket" -and $_.id -eq $Id }) | Select-Object -First 1
    if ($null -eq $entry) {
        return $null
    }

    return [int]$entry.amount
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

Assert-True (Test-Path -LiteralPath (Join-Path $sourceSaveRoot "persist.estate.json") -PathType Leaf) "Decoded current save fixture is missing persist.estate.json."
New-Item -ItemType Directory -Force -Path $testRoot, $saveRoot | Out-Null
Get-ChildItem -LiteralPath $sourceSaveRoot -Filter "*.json" |
    Copy-Item -Destination $saveRoot -Force

$baseArgs = @(
    "--config", (Resolve-ProjectPath $ConfigPath),
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-id", $pluginId,
    "--mod-state-dir", $stateRoot
)

Invoke-Loader -LoaderArgs ($baseArgs + @("--init-mod-state"))
Invoke-Loader -LoaderArgs ($baseArgs + @("--emit-event", "profile.initialization_requested"))

$estate = Read-DecodedEstate
$startingGold = Get-WalletAmount -Estate $estate -Currency "gold"
Assert-True ($startingGold -ne 20000) "Fixture should start with a non-normalized gold amount so the write assertion is meaningful."
Assert-True ($null -eq (Get-TrinketAmount -Estate $estate -Id "focus_ring")) "Fixture should start without focus_ring so the trinket write assertion is meaningful."

Invoke-Loader -LoaderArgs ($baseArgs + @("--apply-managed-actions", "--managed-action-save-dir", $saveRoot))
$dryRunReport = Read-ApplyReport
Assert-True ([bool]$dryRunReport.dryRun) "First apply pass should be dry-run by default."
Assert-True ([int]$dryRunReport.artifactCount -eq 11) "Dry-run should inspect eleven boss gauntlet initialization artifacts."
Assert-True ([int]$dryRunReport.supportedActionCount -eq 2) "Dry-run should recognize two currently supported decoded-save actions."
Assert-True ([int]$dryRunReport.dryRunActionCount -eq 2) "Dry-run should report two dry-run actions."
Assert-True ([int]$dryRunReport.appliedActionCount -eq 0) "Dry-run should not report written actions."
Assert-True ([int]$dryRunReport.unsupportedActionCount -eq 9) "Dry-run should report the remaining profile-normalization actions as unsupported."
Assert-True ([int]$dryRunReport.failedActionCount -eq 0) "Dry-run should not fail on unsupported future actions."
Assert-True ([int]$dryRunReport.changedFileCount -eq 1) "Dry-run should report one would-change decoded save file."

$estate = Read-DecodedEstate
Assert-True ((Get-WalletAmount -Estate $estate -Currency "gold") -eq $startingGold) "Dry-run must not modify decoded save JSON."
Assert-True ($null -eq (Get-TrinketAmount -Estate $estate -Id "focus_ring")) "Dry-run must not add trinkets to decoded save JSON."

Invoke-Loader -LoaderArgs ($baseArgs + @("--apply-managed-actions", "--write-managed-actions", "--managed-action-save-dir", $saveRoot))
$writeReport = Read-ApplyReport
Assert-True (-not [bool]$writeReport.dryRun) "Write pass should record dryRun=false."
Assert-True ([int]$writeReport.supportedActionCount -eq 2) "Write pass should recognize two currently supported decoded-save actions."
Assert-True ([int]$writeReport.dryRunActionCount -eq 0) "Write pass should not report dry-run actions."
Assert-True ([int]$writeReport.appliedActionCount -eq 2) "Write pass should apply two currently supported decoded-save actions."
Assert-True ([int]$writeReport.changedFileCount -eq 1) "Write pass should change one decoded save file."
Assert-True (@(Convert-ToArray $writeReport.files | Where-Object { $_.written -eq $true }).Count -eq 1) "Write pass should mark one file as written."

$estate = Read-DecodedEstate
Assert-True ((Get-WalletAmount -Estate $estate -Currency "gold") -eq 20000) "Write pass should set starting gold to 20000."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "bust") -eq 0) "Write pass should set starting busts to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "portrait") -eq 0) "Write pass should set starting portraits to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "deed") -eq 0) "Write pass should set starting deeds to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "crest") -eq 0) "Write pass should set starting crests to 0."
Assert-True ((Get-WalletAmount -Estate $estate -Currency "shard") -eq 0) "Write pass should set starting shards to 0."
Assert-True ((Get-TrinketAmount -Estate $estate -Id "focus_ring") -eq 2) "Write pass should add two copies of focus_ring."
Assert-True ((Get-TrinketAmount -Estate $estate -Id "berserk_mask") -eq 2) "Write pass should add two copies of berserk_mask."

Write-Host "PASS: managed action save applier dry-run and decoded wallet/trinket write assertions passed."
