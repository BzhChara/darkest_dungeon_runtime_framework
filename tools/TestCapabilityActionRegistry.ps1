param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot "logs\capability_action_registry_test\$sessionId"
$pluginRoot = Join-Path $testRoot "plugins"
$corePluginRoot = Join-Path $pluginRoot "registry_core"
$crossPluginRoot = Join-Path $pluginRoot "registry_cross"
$logRoot = Join-Path $testRoot "runtime_logs"
$requiredStateRoot = Join-Path $projectRoot "state\capability_action_registry_test\$sessionId\required"
$optionalStateRoot = Join-Path $projectRoot "state\capability_action_registry_test\$sessionId\optional"
$saveRoot = Join-Path $testRoot "decoded_save"
$configPath = Join-Path $testRoot "registry_config.json"

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
    param(
        [string[]]$LoaderArgs,
        [switch]$ExpectFailure
    )

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($ExpectFailure) {
        Assert-True ($LASTEXITCODE -ne 0) "DDRuntimeLoader was expected to fail but returned exit code 0."
        return
    }

    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Value
    )

    $Value | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Find-LogLine {
    param(
        [string]$Text,
        [string]$Needle
    )

    return @($Text -split "`r?`n" | Where-Object { $_.Contains($Needle, [System.StringComparison]::Ordinal) })
}

New-Item -ItemType Directory -Force -Path @(
    $corePluginRoot,
    $crossPluginRoot,
    $logRoot,
    $requiredStateRoot,
    $optionalStateRoot,
    $saveRoot
) | Out-Null

$coreManifest = [ordered]@{
    id = "validation.registry_core"
    name = "Validation - Capability Registry Core"
    version = "0.1.0"
    enabled = $true
    capabilities = @(
        "state.sidecar",
        "profile.mark_initialized",
        "campaign.observe_week_advance",
        "town.set_building_levels",
        "validation.self_granted"
    )
    eventRules = @(
        [ordered]@{
            id = "valid_with_optional_planned_action"
            on = "validation.registry_run"
            requiresCapabilities = @("state.sidecar")
            actions = @(
                [ordered]@{
                    type = "state.incrementCounter"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $true
                    args = [ordered]@{ key = "counter"; amount = 1 }
                },
                [ordered]@{
                    type = "upgrade.advancePending"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $false
                    args = [ordered]@{ stateKey = "pending"; amount = 1 }
                },
                [ordered]@{
                    type = "wallet.setCurrencyAmounts"
                    capability = "validation.self_granted"
                    risk = "safe"
                    required = $false
                    args = [ordered]@{ amounts = [ordered]@{ gold = 999999 } }
                }
            )
        },
        [ordered]@{
            id = "unknown_capability_self_grant"
            on = "validation.registry_run"
            requiresCapabilities = @("validation.self_granted")
            actions = @(
                [ordered]@{
                    type = "state.incrementCounter"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $true
                    args = [ordered]@{ key = "counter"; amount = 1 }
                }
            )
        },
        [ordered]@{
            id = "planned_capability_self_grant"
            on = "validation.registry_run"
            requiresCapabilities = @("campaign.observe_week_advance")
            actions = @(
                [ordered]@{
                    type = "state.incrementCounter"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $true
                    args = [ordered]@{ key = "counter"; amount = 1 }
                }
            )
        },
        [ordered]@{
            id = "required_planned_action"
            on = "validation.registry_run"
            requiresCapabilities = @("state.sidecar")
            actions = @(
                [ordered]@{
                    type = "upgrade.advancePending"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $true
                    args = [ordered]@{ stateKey = "pending"; amount = 1 }
                }
            )
        },
        [ordered]@{
            id = "action_capability_mismatch"
            on = "validation.registry_run"
            requiresCapabilities = @("state.sidecar", "profile.mark_initialized")
            actions = @(
                [ordered]@{
                    type = "state.incrementCounter"
                    capability = "profile.mark_initialized"
                    risk = "safe"
                    required = $true
                    args = [ordered]@{ key = "counter"; amount = 1 }
                }
            )
        },
        [ordered]@{
            id = "action_risk_mismatch"
            on = "validation.registry_run"
            requiresCapabilities = @("state.sidecar")
            actions = @(
                [ordered]@{
                    type = "state.incrementCounter"
                    capability = "state.sidecar"
                    risk = "managed"
                    required = $true
                    args = [ordered]@{ key = "counter"; amount = 1 }
                }
            )
        },
        [ordered]@{
            id = "action_type_case_mismatch"
            on = "validation.registry_run"
            requiresCapabilities = @("state.sidecar")
            actions = @(
                [ordered]@{
                    type = "State.incrementCounter"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $true
                    args = [ordered]@{ key = "counter"; amount = 1 }
                }
            )
        },
        [ordered]@{
            id = "optional_supported_action_failure"
            on = "validation.optional_failure"
            requiresCapabilities = @("state.sidecar")
            actions = @(
                [ordered]@{
                    type = "state.incrementCounter"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $false
                    args = [ordered]@{ key = "counter"; amount = "invalid" }
                }
            )
        },
        [ordered]@{
            id = "required_missing_save_consumer"
            on = "validation.materialize_required"
            requiresCapabilities = @("state.sidecar", "town.set_building_levels")
            actions = @(
                [ordered]@{
                    type = "town.setBuildingLevels"
                    capability = "town.set_building_levels"
                    risk = "managed"
                    required = $true
                    args = [ordered]@{ levels = [ordered]@{ abbey = 1 } }
                }
            )
        },
        [ordered]@{
            id = "optional_missing_save_consumer"
            on = "validation.materialize_optional"
            requiresCapabilities = @("state.sidecar", "town.set_building_levels")
            actions = @(
                [ordered]@{
                    type = "town.setBuildingLevels"
                    capability = "town.set_building_levels"
                    risk = "managed"
                    required = $false
                    args = [ordered]@{ levels = [ordered]@{ abbey = 1 } }
                }
            )
        }
    )
    stateSchema = [ordered]@{
        counter = [ordered]@{ type = "integer"; default = 0 }
        pending = [ordered]@{ type = "array"; default = @() }
    }
}

$crossManifest = [ordered]@{
    id = "validation.registry_cross"
    name = "Validation - Capability Registry Cross Plugin"
    version = "0.1.0"
    enabled = $true
    capabilities = @()
    eventRules = @(
        [ordered]@{
            id = "cross_plugin_capability_grant"
            on = "validation.registry_run"
            requiresCapabilities = @("state.sidecar")
            actions = @(
                [ordered]@{
                    type = "state.incrementCounter"
                    capability = "state.sidecar"
                    risk = "safe"
                    required = $true
                    args = [ordered]@{ key = "counter"; amount = 1 }
                }
            )
        }
    )
    stateSchema = [ordered]@{
        counter = [ordered]@{ type = "integer"; default = 0 }
    }
}

Write-JsonFile -Path (Join-Path $corePluginRoot "patches.json") -Value $coreManifest
Write-JsonFile -Path (Join-Path $crossPluginRoot "patches.json") -Value $crossManifest

$config = Get-Content -Raw -LiteralPath (Join-Path $projectRoot "config\default_config.json") | ConvertFrom-Json
$config.enableInjection = $false
$config.saveWatchEnabled = $false
$config.saveEventBridgeEnabled = $false
$config.logDirectory = $logRoot
$config.modStateDirectory = $requiredStateRoot
$config.pluginDirectories = @($pluginRoot)
Write-JsonFile -Path $configPath -Value $config

if (-not $NoBuild) {
    & dotnet build "launcher/DDRuntimeLoader.csproj" -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
}

$baseArgs = @(
    "--config", $configPath,
    "--no-inject",
    "--allow-non-atomic-state-writes"
)

Invoke-Loader -LoaderArgs ($baseArgs + @("--explain-rules"))
$launcherLogPath = Join-Path $logRoot "launcher.log"
$launcherLog = Get-Content -Raw -LiteralPath $launcherLogPath
$plannedCapabilityLine = @(Find-LogLine -Text $launcherLog -Needle "framework-capability id=campaign.observe_week_advance")
Assert-True ($plannedCapabilityLine.Count -eq 1 -and $plannedCapabilityLine[0].Contains("status=planned") -and $plannedCapabilityLine[0].Contains("available=False")) "Planned capability should be visible and unavailable in registry diagnostics."
$artifactOnlyActionLine = @(Find-LogLine -Text $launcherLog -Needle "framework-action type=town.setBuildingLevels")
Assert-True ($artifactOnlyActionLine.Count -eq 1 -and $artifactOnlyActionLine[0].Contains("consumers=[managed-action-artifact-store]") -and $artifactOnlyActionLine[0].Contains("liveEnforced=False")) "Artifact-only action should not be reported as live enforced."
$trinketActionLine = @(Find-LogLine -Text $launcherLog -Needle "framework-action type=trinket.patchEntry")
Assert-True ($trinketActionLine.Count -eq 1 -and $trinketActionLine[0].Contains("decoded-save-recognizer") -and -not $trinketActionLine[0].Contains("decoded-save-applier")) "Content-overlay-only trinket action should be recognized without claiming a decoded-save effect consumer."
$unknownCapabilityLine = @(Find-LogLine -Text $launcherLog -Needle 'id="unknown_capability_self_grant"')
Assert-True ($unknownCapabilityLine.Count -eq 1 -and $unknownCapabilityLine[0].Contains("required capabilities unavailable: validation.self_granted(status=unknown,source=unregistered)")) "Unknown capability self-grant should skip its rule."
$plannedSelfGrantLine = @(Find-LogLine -Text $launcherLog -Needle 'id="planned_capability_self_grant"')
Assert-True ($plannedSelfGrantLine.Count -eq 1 -and $plannedSelfGrantLine[0].Contains("required capabilities unavailable: campaign.observe_week_advance(status=planned")) "Planned capability self-grant should skip its rule."
$crossPluginLine = @(Find-LogLine -Text $launcherLog -Needle 'id="cross_plugin_capability_grant"')
Assert-True ($crossPluginLine.Count -eq 1 -and $crossPluginLine[0].Contains("required capabilities not declared by plugin: state.sidecar")) "Another plugin's declaration must not grant a capability."
$plannedActionLine = @(Find-LogLine -Text $launcherLog -Needle 'id="required_planned_action"')
Assert-True ($plannedActionLine.Count -eq 1 -and $plannedActionLine[0].Contains("action[0] action type upgrade.advancePending is unavailable (status=planned)")) "Required planned action should skip its rule at load time."
$capabilityMismatchLine = @(Find-LogLine -Text $launcherLog -Needle 'id="action_capability_mismatch"')
Assert-True ($capabilityMismatchLine.Count -eq 1 -and $capabilityMismatchLine[0].Contains("does not support capability profile.mark_initialized")) "Action/capability mismatch should skip its rule."
$riskMismatchLine = @(Find-LogLine -Text $launcherLog -Needle 'id="action_risk_mismatch"')
Assert-True ($riskMismatchLine.Count -eq 1 -and $riskMismatchLine[0].Contains("requires risk=safe, declared risk=managed")) "Action risk mismatch should skip its rule."
$caseMismatchLine = @(Find-LogLine -Text $launcherLog -Needle 'id="action_type_case_mismatch"')
Assert-True ($caseMismatchLine.Count -eq 1 -and $caseMismatchLine[0].Contains("action type State.incrementCounter is not registered")) "Action type ids should use exact registered casing."

Invoke-Loader -LoaderArgs ($baseArgs + @(
    "--mod-state-id", "validation.registry_core",
    "--emit-event", "validation.registry_run"
))
$eventReport = Get-Content -Raw -LiteralPath (Join-Path $logRoot "runtime_event_report.json") | ConvertFrom-Json
Assert-True ([bool]$eventReport.succeeded) "Optional planned action should not fail runtime event execution."
Assert-True ([int]$eventReport.ruleCount -eq 1) "Only the valid rule with an optional planned action should remain active."
Assert-True ([int]$eventReport.executedActionCount -eq 1) "The valid sidecar action should execute exactly once."
$optionalRuntimeAction = @($eventReport.rules.actions | Where-Object { $_.type -eq "upgrade.advancePending" })
Assert-True ($optionalRuntimeAction.Count -eq 1 -and $optionalRuntimeAction[0].status -eq "skipped") "Optional planned action should be skipped with a warning."
$invalidOptionalAction = @($eventReport.rules.actions | Where-Object { $_.type -eq "wallet.setCurrencyAmounts" })
Assert-True ($invalidOptionalAction.Count -eq 1 -and $invalidOptionalAction[0].status -eq "skipped") "Optional executable action with invalid capability/risk metadata must not materialize."
Assert-True ([int]$eventReport.materializedActionCount -eq 0) "Invalid optional managed action must not write an artifact."
Assert-True (@($eventReport.issues | Where-Object { $_.code -eq "optional-action-contract-invalid" -and $_.severity -eq "warning" }).Count -eq 2) "Both invalid optional actions should report contract warnings."
$stateDocument = Get-Content -Raw -LiteralPath (Join-Path $requiredStateRoot "validation.registry_core.json") | ConvertFrom-Json
Assert-True ([int]$stateDocument.state.counter -eq 1) "Skipped rules must not execute their counters."

Invoke-Loader -LoaderArgs ($baseArgs + @(
    "--mod-state-id", "validation.registry_core",
    "--emit-event", "validation.optional_failure"
))
$optionalFailureReport = Get-Content -Raw -LiteralPath (Join-Path $logRoot "runtime_event_report.json") | ConvertFrom-Json
Assert-True ([bool]$optionalFailureReport.succeeded) "Optional supported action parameter failure should not fail the event."
$optionalFailureAction = @($optionalFailureReport.rules.actions | Where-Object { $_.type -eq "state.incrementCounter" })
Assert-True ($optionalFailureAction.Count -eq 1 -and $optionalFailureAction[0].status -eq "failed") "Optional supported action failure should remain visible in the action report."
Assert-True (@($optionalFailureReport.issues | Where-Object { $_.code -eq "action-failed" -and $_.severity -eq "warning" }).Count -eq 1) "Optional supported action failure should be downgraded to a warning."
$stateAfterOptionalFailure = Get-Content -Raw -LiteralPath (Join-Path $requiredStateRoot "validation.registry_core.json") | ConvertFrom-Json
Assert-True ([int]$stateAfterOptionalFailure.state.counter -eq 1) "Optional failed action must not change state."

Invoke-Loader -LoaderArgs ($baseArgs + @(
    "--mod-state-id", "validation.registry_core",
    "--emit-event", "validation.materialize_required"
))
Invoke-Loader -LoaderArgs ($baseArgs + @(
    "--apply-managed-actions",
    "--managed-action-save-dir", $saveRoot
)) -ExpectFailure
$requiredApplyReport = Get-Content -Raw -LiteralPath (Join-Path $logRoot "managed_action_apply_report.json") | ConvertFrom-Json
Assert-True (-not [bool]$requiredApplyReport.succeeded) "Required action without a decoded-save consumer should fail apply."
Assert-True ([int]$requiredApplyReport.failedActionCount -eq 1) "Required missing consumer should count as one failed action."
Assert-True ([int]$requiredApplyReport.unsupportedActionCount -eq 0) "Required missing consumer must not be downgraded to unsupported warning."
Assert-True (@($requiredApplyReport.issues | Where-Object { $_.code -eq "managed-action-required-consumer-missing" }).Count -eq 1) "Required missing consumer should use the dedicated error code."

$optionalArgs = @(
    "--config", $configPath,
    "--no-inject",
    "--allow-non-atomic-state-writes",
    "--mod-state-dir", $optionalStateRoot
)
Invoke-Loader -LoaderArgs ($optionalArgs + @(
    "--mod-state-id", "validation.registry_core",
    "--emit-event", "validation.materialize_optional"
))
Invoke-Loader -LoaderArgs ($optionalArgs + @(
    "--apply-managed-actions",
    "--managed-action-save-dir", $saveRoot
))
$optionalApplyReport = Get-Content -Raw -LiteralPath (Join-Path $logRoot "managed_action_apply_report.json") | ConvertFrom-Json
Assert-True ([bool]$optionalApplyReport.succeeded) "Optional action without a decoded-save consumer should remain non-fatal."
Assert-True ([int]$optionalApplyReport.failedActionCount -eq 0) "Optional missing consumer should not count as failed."
Assert-True ([int]$optionalApplyReport.unsupportedActionCount -eq 1) "Optional missing consumer should count as unsupported."
Assert-True (@($optionalApplyReport.issues | Where-Object { $_.code -eq "managed-action-applier-not-implemented" }).Count -eq 1) "Optional missing consumer should keep the warning code."

$registrySource = Get-Content -Raw -LiteralPath (Join-Path $projectRoot "launcher\Patching\FrameworkCapabilityRegistry.cs")
$executorSource = Get-Content -Raw -LiteralPath (Join-Path $projectRoot "launcher\Patching\RuntimeEventExecutor.cs")
$applierSource = Get-Content -Raw -LiteralPath (Join-Path $projectRoot "launcher\Patching\ManagedActionSaveApplier.cs")
$manifestCapabilities = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot "plugins") -Filter "patches.json" -Recurse | ForEach-Object {
    $manifest = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
    @($manifest.capabilities)
} | Where-Object { $_ } | Sort-Object -Unique)
$registeredCapabilities = @([regex]::Matches($registrySource, '(?m)^\s*Capability\("([^"]+)"') | ForEach-Object {
    $_.Groups[1].Value
} | Sort-Object -Unique)
$manifestActions = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot "plugins") -Filter "patches.json" -Recurse | ForEach-Object {
    $manifest = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
    foreach ($rule in @($manifest.eventRules)) {
        foreach ($action in @($rule.actions)) {
            $action.type
        }
    }
} | Where-Object { $_ } | Sort-Object -Unique)
$registeredActions = @([regex]::Matches($registrySource, '(?m)^\s*(?:SidecarAction|ManagedAction|PlannedAction)\("([^"]+)"') | ForEach-Object {
    $_.Groups[1].Value
} | Sort-Object -Unique)
$availableRegistryActions = @([regex]::Matches($registrySource, '(?m)^\s*(?:SidecarAction|ManagedAction)\("([^"]+)"') | ForEach-Object {
    $_.Groups[1].Value
} | Sort-Object -Unique)
$executorActions = @([regex]::Matches($executorSource, '(?m)^\s*"([^"]+)" => (?:Build|Execute)') | ForEach-Object {
    $_.Groups[1].Value
} | Sort-Object -Unique)
$decodedRegistryActions = @([regex]::Matches($registrySource, '(?m)^\s*ManagedAction\("([^"]+)"[^\r\n]*DecodedSave(?:Recognition)?Consumer') | ForEach-Object {
    $_.Groups[1].Value
} | Sort-Object -Unique)
$decodedApplyHandlers = @([regex]::Matches($applierSource, '(?m)^\s*case "([^"]+)":') | ForEach-Object {
    $_.Groups[1].Value
} | Sort-Object -Unique)

$missingCapabilities = @($manifestCapabilities | Where-Object { $_ -notin $registeredCapabilities })
$missingManifestActions = @($manifestActions | Where-Object { $_ -notin $registeredActions })
$registryActionsWithoutExecutor = @($availableRegistryActions | Where-Object { $_ -notin $executorActions })
$executorActionsWithoutRegistry = @($executorActions | Where-Object { $_ -notin $availableRegistryActions })
$decodedConsumersWithoutHandler = @($decodedRegistryActions | Where-Object { $_ -notin $decodedApplyHandlers })
$decodedHandlersWithoutConsumer = @($decodedApplyHandlers | Where-Object { $_ -notin $decodedRegistryActions })

Assert-True ($missingCapabilities.Count -eq 0) "Plugin capabilities missing from registry: $($missingCapabilities -join ', ')"
Assert-True ($missingManifestActions.Count -eq 0) "Plugin actions missing from registry: $($missingManifestActions -join ', ')"
Assert-True ($registryActionsWithoutExecutor.Count -eq 0) "Available registry actions missing executor cases: $($registryActionsWithoutExecutor -join ', ')"
Assert-True ($executorActionsWithoutRegistry.Count -eq 0) "Executor cases missing available registry actions: $($executorActionsWithoutRegistry -join ', ')"
Assert-True ($decodedConsumersWithoutHandler.Count -eq 0) "Decoded-save consumers missing apply handlers: $($decodedConsumersWithoutHandler -join ', ')"
Assert-True ($decodedHandlersWithoutConsumer.Count -eq 0) "Decoded-save handlers missing registry consumers: $($decodedHandlersWithoutConsumer -join ', ')"

Write-Host "PASS: capability/action registry blocks self-grants, validates action contracts, and preserves required consumer failures."
