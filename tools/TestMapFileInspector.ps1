param(
    [string]$ConfigPath = "config\default_config.json",
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$sessionId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$testRoot = Join-Path $projectRoot.Path "logs\map_file_inspector_test\$sessionId"
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

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

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Assert-MapTopology {
    param(
        [object]$MapFacts,
        [string]$MapName,
        [int]$ExpectedAreas,
        [bool]$ExpectFinalRoom,
        [int]$ExpectedReachableAreas = -1
    )

    if ($ExpectedReachableAreas -lt 0) {
        $ExpectedReachableAreas = $ExpectedAreas
    }

    Assert-True ($null -ne $MapFacts.topology) "$MapName topology facts were not exported."
    Assert-True ([bool]$MapFacts.topology.hasEntranceArea) "$MapName topology entrance area did not resolve."
    Assert-True ($MapFacts.topology.reachableAreaCount -eq $ExpectedReachableAreas) "$MapName topology reachable area count mismatch."
    Assert-True ($MapFacts.topology.invalidDoorTargetCount -eq 0) "$MapName topology reported invalid door targets."
    Assert-True ((Convert-ToArray $MapFacts.topology.issues).Count -eq 0) "$MapName topology reported issues."
    if ($ExpectFinalRoom) {
        Assert-True ([bool]$MapFacts.topology.hasFinalRoom) "$MapName topology final room did not resolve."
        Assert-True ([bool]$MapFacts.topology.entranceCanReachFinal) "$MapName topology final room was not reachable from entrance."
    }
}

function Test-MapReport {
    param(
        [string]$MapName,
        [int]$ExpectedAreas,
        [int]$ExpectedRooms,
        [int]$ExpectedCorridors,
        [int]$ExpectedTiles,
        [string]$ExpectedEntrance,
        [AllowNull()][string]$ExpectedFinal
    )

    $mapPath = Join-Path $GameDirectory "maps\$MapName.dm"
    $outputPath = Join-Path $testRoot "$MapName.json"
    Assert-True (Test-Path -LiteralPath $mapPath -PathType Leaf) "Map file missing: $mapPath"

    Invoke-Loader @(
        "--config", $ConfigPath,
        "--inspect-map-file", $mapPath,
        "--map-report-output", $outputPath,
        "--no-inject"
    )

    Assert-True (Test-Path -LiteralPath $outputPath -PathType Leaf) "Map report was not created: $outputPath"
    $report = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json

    Assert-True ($report.file.parseStatus -eq "dsonPartialDecoded") "$MapName did not parse as DSON."
    Assert-True ((Convert-ToArray $report.accessIssues).Count -eq 0) "$MapName reported access issues."
    Assert-True ([bool]$report.map.hasStaticSave) "$MapName static save was not decoded."
    Assert-True ($report.map.areaCount -eq $ExpectedAreas) "$MapName area count mismatch."
    Assert-True ($report.map.roomCount -eq $ExpectedRooms) "$MapName room count mismatch."
    Assert-True ($report.map.corridorCount -eq $ExpectedCorridors) "$MapName corridor count mismatch."
    Assert-True ($report.map.tileCount -eq $ExpectedTiles) "$MapName tile count mismatch."
    Assert-True ($report.map.entranceAreaId -eq $ExpectedEntrance) "$MapName entrance mismatch."
    if ([string]::IsNullOrEmpty($ExpectedFinal)) {
        Assert-True ([string]::IsNullOrEmpty($report.map.finalRoomId)) "$MapName final room mismatch."
    } else {
        Assert-True ($report.map.finalRoomId -eq $ExpectedFinal) "$MapName final room mismatch."
    }
    Assert-True ((Convert-ToArray $report.map.areas).Count -eq $ExpectedAreas) "$MapName areas were not exported."
    Assert-True ((Convert-ToArray $report.map.dynamicAreas).Count -eq $ExpectedAreas) "$MapName dynamic areas were not exported."
    Assert-MapTopology -MapFacts $report.map -MapName $MapName -ExpectedAreas $ExpectedAreas -ExpectFinalRoom (-not [string]::IsNullOrEmpty($ExpectedFinal))
}

function Test-MapFinalRoomPrototype {
    $sourcePath = Join-Path $GameDirectory "maps\DD_map4.dm"
    $outputMapPath = Join-Path $testRoot "DD_map4_final_rooC.dm"
    $reportPath = Join-Path $testRoot "DD_map4_final_rooC.prototype.json"
    Assert-True (Test-Path -LiteralPath $sourcePath -PathType Leaf) "Map file missing: $sourcePath"

    Invoke-Loader @(
        "--config", $ConfigPath,
        "--prototype-map-final-room", $sourcePath,
        "--map-final-room-id", "rooC",
        "--map-prototype-output", $outputMapPath,
        "--map-prototype-report-output", $reportPath,
        "--no-inject"
    )

    Assert-True (Test-Path -LiteralPath $outputMapPath -PathType Leaf) "Prototype map was not created: $outputMapPath"
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) "Prototype report was not created: $reportPath"
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

    Assert-True ([bool]$report.succeeded) "Prototype report did not succeed."
    Assert-True ((Convert-ToArray $report.accessIssues).Count -eq 0) "Prototype report had access issues."
    Assert-True ($report.previousFinalRoomId -eq "rooB") "Prototype source final room mismatch."
    Assert-True ($report.targetFinalRoomId -eq "rooC") "Prototype target final room mismatch."
    Assert-True ($report.outputMap.finalRoomId -eq "rooC") "Prototype output final room was not updated."
    Assert-True ($report.outputMap.areaCount -eq 4) "Prototype output area count changed."
    Assert-True ($report.outputMap.roomCount -eq 3) "Prototype output room count changed."
    Assert-True ($report.outputMap.corridorCount -eq 1) "Prototype output corridor count changed."
    Assert-True ($report.outputMap.tileCount -eq 31) "Prototype output tile count changed."
    Assert-MapTopology -MapFacts $report.outputMap -MapName "DD_map4 final-room prototype output" -ExpectedAreas 4 -ExpectFinalRoom $true

    $script:PrototypeMapPath = $outputMapPath
}

function Test-MapTemplatePrototype {
    $sourcePath = Join-Path $GameDirectory "maps\DD_map4.dm"
    $specPath = Join-Path $testRoot "DD_map4_template_spec.json"
    $outputMapPath = Join-Path $testRoot "DD_map4_template_rooC_tile.dm"
    $reportPath = Join-Path $testRoot "DD_map4_template_rooC_tile.prototype.json"
    Assert-True (Test-Path -LiteralPath $sourcePath -PathType Leaf) "Map file missing: $sourcePath"

    $spec = [ordered]@{
        version = 1
        name = "dd4_rooC_dynamic_tile_probe"
        finalRoomId = "rooC"
        dynamicTiles = @(
            [ordered]@{
                areaId = "rooC"
                tileId = "tile0"
                content = 8
                knowledge = 1
                critScout = $true
            }
        )
        staticTiles = @(
            [ordered]@{
                areaId = "rooB"
                tileId = "tile0"
                mapPosition = @(12, 5)
            },
            [ordered]@{
                areaId = "rooC"
                tileId = "tile0"
                mapPosition = @(20, 2)
            }
        )
        staticDoors = @(
            [ordered]@{
                areaId = "rooB"
                doorSlot = "door4"
                disabled = $true
            },
            [ordered]@{
                areaId = "rooC"
                doorSlot = "door0"
                targetTileId = "tile27"
                doorType = 0
                implied = $true
            }
        )
        staticTileDoors = @(
            [ordered]@{
                areaId = "corA"
                tileId = "tile17"
                disabled = $true
            },
            [ordered]@{
                areaId = "corA"
                tileId = "tile27"
                targetAreaId = "rooC"
                targetTileIndex = 0
                doorType = 2
                implied = $true
            }
        )
    }
    $spec | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $specPath -Encoding UTF8

    Invoke-Loader @(
        "--config", $ConfigPath,
        "--prototype-map-template", $sourcePath,
        "--map-template-spec", $specPath,
        "--map-prototype-output", $outputMapPath,
        "--map-prototype-report-output", $reportPath,
        "--no-inject"
    )

    Assert-True (Test-Path -LiteralPath $outputMapPath -PathType Leaf) "Template prototype map was not created: $outputMapPath"
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) "Template prototype report was not created: $reportPath"
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

    Assert-True ([bool]$report.succeeded) "Template prototype report did not succeed."
    Assert-True ((Convert-ToArray $report.accessIssues).Count -eq 0) "Template prototype report had access issues."
    Assert-True ($report.specName -eq "dd4_rooC_dynamic_tile_probe") "Template prototype spec name mismatch."
    Assert-True ((Convert-ToArray $report.mutations).Count -eq 21) "Template prototype mutation count mismatch."
    Assert-True ($report.outputMap.finalRoomId -eq "rooC") "Template prototype output final room was not updated."
    Assert-True ($report.outputMap.areaCount -eq 4) "Template prototype output area count changed."
    Assert-True ($report.outputMap.roomCount -eq 3) "Template prototype output room count changed."
    Assert-True ($report.outputMap.corridorCount -eq 1) "Template prototype output corridor count changed."
    Assert-True ($report.outputMap.tileCount -eq 31) "Template prototype output tile count changed."
    Assert-MapTopology -MapFacts $report.outputMap -MapName "DD_map4 template prototype output" -ExpectedAreas 4 -ExpectFinalRoom $true -ExpectedReachableAreas 3

    $rooC = Convert-ToArray $report.outputMap.dynamicAreas | Where-Object { $_.areaId -eq "rooC" } | Select-Object -First 1
    Assert-True ($null -ne $rooC) "Template prototype output dynamic area rooC was not found."
    $tile0 = Convert-ToArray $rooC.tileSamples | Where-Object { $_.tileId -eq "tile0" } | Select-Object -First 1
    Assert-True ($null -ne $tile0) "Template prototype output dynamic tile rooC.tile0 was not found."
    Assert-True ($tile0.content -eq 8) "Template prototype output dynamic tile content was not updated."
    Assert-True ($tile0.knowledge -eq 1) "Template prototype output dynamic tile knowledge was not updated."
    Assert-True ([bool]$tile0.critScout) "Template prototype output dynamic tile critScout was not updated."

    $rooB = Convert-ToArray $report.outputMap.areas | Where-Object { $_.areaId -eq "rooB" } | Select-Object -First 1
    Assert-True ($null -ne $rooB) "Template prototype output static area rooB was not found."
    $door4 = Convert-ToArray $rooB.doors | Where-Object { $_.slotId -eq "door4" } | Select-Object -First 1
    $rooBTile0 = Convert-ToArray $rooB.tileSamples | Where-Object { $_.tileId -eq "tile0" } | Select-Object -First 1
    Assert-True ($null -ne $rooBTile0) "Template prototype output static tile rooB.tile0 was not found."
    Assert-True (($rooBTile0.mapPosition -join ",") -eq "12,5") "Template prototype output static tile rooB.tile0 mapPosition was not updated."
    Assert-True ($null -eq $door4) "Template prototype output static door rooB.door4 was not disabled."

    $corA = Convert-ToArray $report.outputMap.areas | Where-Object { $_.areaId -eq "corA" } | Select-Object -First 1
    Assert-True ($null -ne $corA) "Template prototype output static area corA was not found."
    $rooCStatic = Convert-ToArray $report.outputMap.areas | Where-Object { $_.areaId -eq "rooC" } | Select-Object -First 1
    Assert-True ($null -ne $rooCStatic) "Template prototype output static area rooC was not found."
    $rooCTile0 = Convert-ToArray $rooCStatic.tileSamples | Where-Object { $_.tileId -eq "tile0" } | Select-Object -First 1
    Assert-True ($null -ne $rooCTile0) "Template prototype output static tile rooC.tile0 was not found."
    Assert-True (($rooCTile0.mapPosition -join ",") -eq "20,2") "Template prototype output static tile rooC.tile0 mapPosition was not updated."
    $door0 = Convert-ToArray $rooCStatic.doors | Where-Object { $_.slotId -eq "door0" } | Select-Object -First 1
    Assert-True ($null -ne $door0) "Template prototype output static door rooC.door0 was not found."
    Assert-True ($door0.targetAreaId -eq "corA") "Template prototype output static door rooC.door0 target area changed unexpectedly."
    Assert-True ($door0.targetTileIndex -eq 27) "Template prototype output static door rooC.door0 tile_to was not updated."
    Assert-True ($door0.doorType -eq 0) "Template prototype output static door rooC.door0 type was not updated."
    Assert-True ([bool]$door0.implied) "Template prototype output static door rooC.door0 implied was not updated."
    $corATile17 = Convert-ToArray $corA.tileSamples | Where-Object { $_.tileId -eq "tile17" } | Select-Object -First 1
    Assert-True ($null -ne $corATile17) "Template prototype output static tile corA.tile17 was not found."
    Assert-True ($null -eq $corATile17.doorTo) "Template prototype output static tile corA.tile17 door_to was not disabled."
    $corATile27 = Convert-ToArray $corA.tileSamples | Where-Object { $_.tileId -eq "tile27" } | Select-Object -First 1
    Assert-True ($null -ne $corATile27) "Template prototype output static tile corA.tile27 was not found."
    Assert-True ($null -ne $corATile27.doorTo) "Template prototype output static tile corA.tile27 door_to was not found."
    Assert-True ($corATile27.doorTo.targetAreaId -eq "rooC") "Template prototype output static tile door target area was not updated."
    Assert-True ($corATile27.doorTo.targetTileIndex -eq 0) "Template prototype output static tile door target tile was not updated."
    Assert-True ($corATile27.doorTo.doorType -eq 2) "Template prototype output static tile door type was not updated."
    Assert-True ([bool]$corATile27.doorTo.implied) "Template prototype output static tile door implied was not updated."

    $script:PrototypeMapPath = $outputMapPath
}

function Test-MapVirtualSourceOverlay {
    Assert-True (-not [string]::IsNullOrWhiteSpace($script:PrototypeMapPath)) "Prototype map path was not captured."
    Assert-True (Test-Path -LiteralPath $script:PrototypeMapPath -PathType Leaf) "Prototype map was not found: $script:PrototypeMapPath"

    $overlayConfigPath = Join-Path $projectRoot.Path "config\_map_source_overlay_test_$sessionId.json"
    $previewRoot = Join-Path $testRoot "map_source_overlay_preview"
    $stateRoot = Join-Path $testRoot "state"

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
        fileIoLogExtensions = @(".dm")
        fileIoMaxLogEntries = 20
        fileIoDeduplicate = $true
        eventProbeEnabled = $false
        pluginDirectories = @()
        pluginPatchManifestName = "patches.json"
        virtualFileEnabled = $true
        virtualFileTarget = ""
        virtualFileFind = ""
        virtualFileReplace = ""
        virtualFileRules = @(
            [ordered]@{
                target = "maps/DD_map4.dm"
                sourcePath = $script:PrototypeMapPath
            }
        )
    }

    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $overlayConfigPath -Encoding UTF8

    Invoke-Loader @(
        "--config", $overlayConfigPath,
        "--validate-only",
        "--preview-patches",
        "--preview-output", $previewRoot,
        "--no-inject"
    )

    $previewPath = Join-Path $previewRoot "maps_DD_map4.dm.preview.bin"
    $diffPath = Join-Path $previewRoot "maps_DD_map4.dm.diff.txt"
    $summaryPath = Join-Path $previewRoot "summary.txt"
    Assert-True (Test-Path -LiteralPath $previewPath -PathType Leaf) "Binary source overlay preview was not written: $previewPath"
    Assert-True (Test-Path -LiteralPath $diffPath -PathType Leaf) "Binary source overlay diff was not written: $diffPath"
    Assert-True (Test-Path -LiteralPath $summaryPath -PathType Leaf) "Binary source overlay summary was not written: $summaryPath"

    $prototypeHash = (Get-FileHash -LiteralPath $script:PrototypeMapPath -Algorithm SHA256).Hash
    $previewHash = (Get-FileHash -LiteralPath $previewPath -Algorithm SHA256).Hash
    Assert-True ($previewHash -eq $prototypeHash) "Binary source overlay preview bytes did not match the prototype map."

    $diffText = Get-Content -LiteralPath $diffPath -Raw
    Assert-True ($diffText.Contains("Mode: sourcePath")) "Binary source overlay diff did not record sourcePath mode."
    Assert-True ($diffText.Contains("Binary source overlay")) "Binary source overlay diff did not record binary overlay details."
}

function Test-PluginMapTemplateOverlay {
    $pluginRoot = Join-Path $testRoot "plugins\map_template_overlay"
    $stateRoot = Join-Path $testRoot "plugin_map_template_state"
    $previewRoot = Join-Path $testRoot "plugin_map_template_preview"
    $overlayConfigPath = Join-Path $projectRoot.Path "config\_map_template_plugin_test_$sessionId.json"
    New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null

    $pluginSourcePath = Join-Path $pluginRoot "DD_map4_plugin_source.dm"
    Copy-Item -LiteralPath (Join-Path $GameDirectory "maps\DD_map4.dm") -Destination $pluginSourcePath -Force

    $specPath = Join-Path $pluginRoot "DD_map4_plugin_template_spec.json"
    $spec = [ordered]@{
        version = 1
        name = "dd4_plugin_map_template_probe"
        finalRoomId = "rooC"
        staticTiles = @(
            [ordered]@{
                areaId = "rooC"
                tileId = "tile0"
                mapPosition = @(20, 2)
            }
        )
        staticDoors = @(
            [ordered]@{
                areaId = "rooB"
                doorSlot = "door4"
                disabled = $true
            },
            [ordered]@{
                areaId = "rooC"
                doorSlot = "door0"
                targetTileId = "tile27"
                doorType = 0
                implied = $true
            }
        )
        staticTileDoors = @(
            [ordered]@{
                areaId = "corA"
                tileId = "tile17"
                disabled = $true
            },
            [ordered]@{
                areaId = "corA"
                tileId = "tile27"
                targetAreaId = "rooC"
                targetTileIndex = 0
                doorType = 0
                implied = $false
            }
        )
    }
    $spec | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $specPath -Encoding UTF8

    $manifest = [ordered]@{
        id = "validation.map_template_overlay"
        name = "Validation - Map Template Overlay"
        version = "0.1.0"
        enabled = $true
        capabilities = @("map.template.fixed")
        virtualFileRules = @()
        mapTemplates = @(
            [ordered]@{
                id = "dd4_rooC_plugin"
                target = "maps/DD_map4.dm"
                source = "DD_map4_plugin_source.dm"
                specPath = "DD_map4_plugin_template_spec.json"
            }
        )
        eventRules = @()
        factEventRules = @()
        stateSchema = [ordered]@{}
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $pluginRoot "patches.json") -Encoding UTF8

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
        fileIoLogExtensions = @(".dm")
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
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $overlayConfigPath -Encoding UTF8

    Invoke-Loader @(
        "--config", $overlayConfigPath,
        "--validate-only",
        "--preview-patches",
        "--preview-output", $previewRoot,
        "--no-inject"
    )

    $artifactPath = Join-Path $stateRoot "_map_templates\validation.map_template_overlay\001_dd4_rooC_plugin.dm"
    $artifactReportPath = Join-Path $stateRoot "_map_templates\validation.map_template_overlay\001_dd4_rooC_plugin.report.json"
    Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Plugin map template artifact was not created: $artifactPath"
    Assert-True (Test-Path -LiteralPath $artifactReportPath -PathType Leaf) "Plugin map template report was not created: $artifactReportPath"

    $artifactReport = Get-Content -LiteralPath $artifactReportPath -Raw | ConvertFrom-Json
    Assert-True ([bool]$artifactReport.succeeded) "Plugin map template report did not succeed."
    Assert-True ($artifactReport.outputMap.finalRoomId -eq "rooC") "Plugin map template output final room was not updated."
    Assert-MapTopology -MapFacts $artifactReport.outputMap -MapName "plugin map template output" -ExpectedAreas 4 -ExpectFinalRoom $true -ExpectedReachableAreas 3
    $corA = Convert-ToArray $artifactReport.outputMap.areas | Where-Object { $_.areaId -eq "corA" } | Select-Object -First 1
    Assert-True ($null -ne $corA) "Plugin map template output static area corA was not found."
    $corATile17 = Convert-ToArray $corA.tileSamples | Where-Object { $_.tileId -eq "tile17" } | Select-Object -First 1
    Assert-True ($null -ne $corATile17) "Plugin map template output static tile corA.tile17 was not found."
    Assert-True ($null -eq $corATile17.doorTo) "Plugin map template output static tile corA.tile17 door_to was not disabled."

    $previewPath = Join-Path $previewRoot "maps_DD_map4.dm.preview.bin"
    $diffPath = Join-Path $previewRoot "maps_DD_map4.dm.diff.txt"
    Assert-True (Test-Path -LiteralPath $previewPath -PathType Leaf) "Plugin map template overlay preview was not written: $previewPath"
    Assert-True (Test-Path -LiteralPath $diffPath -PathType Leaf) "Plugin map template overlay diff was not written: $diffPath"

    $artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
    $previewHash = (Get-FileHash -LiteralPath $previewPath -Algorithm SHA256).Hash
    Assert-True ($previewHash -eq $artifactHash) "Plugin map template overlay preview bytes did not match the generated artifact."
}

function Test-PluginMapLayoutTemplateValidation {
    $pluginRoot = Join-Path $testRoot "plugins\map_layout_template_validation"
    $stateRoot = Join-Path $testRoot "plugin_map_layout_template_state"
    $previewRoot = Join-Path $testRoot "plugin_map_layout_template_preview"
    $overlayConfigPath = Join-Path $projectRoot.Path "config\_map_layout_plugin_test_$sessionId.json"
    New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null

    $manifest = [ordered]@{
        id = "validation.map_layout_template"
        name = "Validation - Map Layout Template"
        version = "0.1.0"
        enabled = $true
        capabilities = @("map.layout.template", "quest.chain.define", "quest_board.replace_with_fixed_set")
        virtualFileRules = @()
        mapTemplates = @()
        mapLayoutTemplates = @(
            [ordered]@{
                id = "dd4_high_level_layout_probe"
                target = "maps/DD_map4.dm"
                source = "maps/DD_map4.dm"
                layout = [ordered]@{
                    entrance = "start"
                    finalRoom = "boss"
                    rooms = @(
                        [ordered]@{
                            id = "start"
                            templateAreaId = "rooA"
                            position = @(1, 2)
                        },
                        [ordered]@{
                            id = "boss"
                            templateAreaId = "rooC"
                            position = @(20, 2)
                        }
                    )
                    corridors = @(
                        [ordered]@{
                            id = "main_path"
                            templateAreaId = "corA"
                            route = @(
                                @(2, 2),
                                @(3, 2),
                                @(4, 2)
                            )
                        }
                    )
                    links = @(
                        [ordered]@{
                            from = "start"
                            to = "main_path"
                            tile = 0
                        },
                        [ordered]@{
                            from = "main_path"
                            to = "boss"
                            tile = 27
                        }
                    )
                }
                tiles = @(
                    [ordered]@{
                        area = "boss"
                        tile = 0
                        content = 8
                        knowledge = 1
                        critScout = $true
                    }
                )
                encounters = @()
            }
        )
        questChains = @(
            [ordered]@{
                id = "post_ancestor_probe_chain"
                name = "Post Ancestor Probe Chain"
                mode = "fixed_order"
                unlock = [ordered]@{
                    type = "afterQuest"
                    questId = "plot_final_boss"
                }
                questBoard = [ordered]@{
                    enabled = $true
                    mode = "replaceWithFixedSet"
                    questIdSource = "sourceQuestId"
                    removeCompleted = $false
                }
                stages = @(
                    [ordered]@{
                        id = "stage_01_layout_probe"
                        name = "Layout Probe"
                        order = 0
                        sourceQuestId = "plot_dd_4"
                        targetQuestId = "probe_stage_01"
                        mapLayoutTemplateId = "dd4_high_level_layout_probe"
                        region = "darkestdungeon"
                        difficulty = 6
                        tags = @("boss", "post_ancestor")
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
        fileIoLogExtensions = @(".dm")
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
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $overlayConfigPath -Encoding UTF8

    Invoke-Loader @(
        "--config", $overlayConfigPath,
        "--validate-only",
        "--preview-patches",
        "--preview-output", $previewRoot,
        "--explain-patches",
        "--no-inject"
    )

    $artifactRoot = Join-Path $stateRoot "_map_layout_templates\validation.map_layout_template"
    $reportPath = Join-Path $artifactRoot "001_dd4_high_level_layout_probe.layout.validation.json"
    $specPath = Join-Path $artifactRoot "001_dd4_high_level_layout_probe.compiled.spec.json"
    $artifactPath = Join-Path $artifactRoot "001_dd4_high_level_layout_probe.dm"
    $templateReportPath = Join-Path $artifactRoot "001_dd4_high_level_layout_probe.template.report.json"
    $questChainReportPath = Join-Path $stateRoot "_quest_chains\validation.map_layout_template\001_post_ancestor_probe_chain.validation.json"
    $questChainManagedReportPath = Join-Path $stateRoot "_quest_chains\validation.map_layout_template\001_post_ancestor_probe_chain.managed.quest_board.json"
    $questChainManagedArtifactPath = Join-Path $stateRoot "_managed_actions\static_validation.map_layout_template_001_post_ancestor_probe_chain_questBoard.replaceWithFixedSet.json"
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) "Plugin map layout validation report was not created: $reportPath"
    Assert-True (Test-Path -LiteralPath $specPath -PathType Leaf) "Plugin map layout compiled spec was not created: $specPath"
    Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Plugin map layout artifact was not created: $artifactPath"
    Assert-True (Test-Path -LiteralPath $templateReportPath -PathType Leaf) "Plugin map layout template report was not created: $templateReportPath"
    Assert-True (Test-Path -LiteralPath $questChainReportPath -PathType Leaf) "Plugin quest chain validation report was not created: $questChainReportPath"
    Assert-True (Test-Path -LiteralPath $questChainManagedReportPath -PathType Leaf) "Plugin quest chain managed report was not created: $questChainManagedReportPath"
    Assert-True (Test-Path -LiteralPath $questChainManagedArtifactPath -PathType Leaf) "Plugin quest chain managed artifact was not created: $questChainManagedArtifactPath"

    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Assert-True ([bool]$report.succeeded) "Plugin map layout validation report did not succeed."
    Assert-True ([bool]$report.compileReady) "Plugin map layout validation should claim compile readiness after artifact generation."
    Assert-True ($report.phase -eq "compiledToMapTemplate") "Plugin map layout validation phase mismatch."
    Assert-True ((Convert-ToArray $report.issues).Count -eq 0) "Plugin map layout validation reported issues."
    Assert-True ($report.layout.nodeCount -eq 3) "Plugin map layout node count mismatch."
    Assert-True ($report.layout.roomCount -eq 2) "Plugin map layout room count mismatch."
    Assert-True ($report.layout.corridorCount -eq 1) "Plugin map layout corridor count mismatch."
    Assert-True ($report.layout.linkCount -eq 2) "Plugin map layout link count mismatch."
    Assert-True ($report.layout.tileRuleCount -eq 1) "Plugin map layout tile rule count mismatch."
    Assert-True ($report.layout.reachableNodeCount -eq 3) "Plugin map layout reachable node count mismatch."
    Assert-True ([bool]$report.layout.entranceCanReachFinal) "Plugin map layout final room was not reachable."
    Assert-True ((Convert-ToArray $report.layout.unreachableNodeIds).Count -eq 0) "Plugin map layout reported unreachable nodes."

    $spec = Get-Content -LiteralPath $specPath -Raw | ConvertFrom-Json
    Assert-True ($spec.entranceAreaId -eq "rooA") "Plugin map layout compiled entrance mismatch."
    Assert-True ($spec.finalRoomId -eq "rooC") "Plugin map layout compiled final room mismatch."
    Assert-True ((Convert-ToArray $spec.staticTiles).Count -eq 5) "Plugin map layout compiled static tile count mismatch."
    Assert-True ((Convert-ToArray $spec.staticTileDoors).Count -ge 3) "Plugin map layout compiled static tile doors did not include retarget and disable entries."

    $templateReport = Get-Content -LiteralPath $templateReportPath -Raw | ConvertFrom-Json
    Assert-True ([bool]$templateReport.succeeded) "Plugin map layout template report did not succeed."
    Assert-True ($templateReport.outputMap.finalRoomId -eq "rooC") "Plugin map layout output final room was not updated."
    Assert-MapTopology -MapFacts $templateReport.outputMap -MapName "plugin map layout output" -ExpectedAreas 4 -ExpectFinalRoom $true -ExpectedReachableAreas 3

    $rooC = Convert-ToArray $templateReport.outputMap.dynamicAreas | Where-Object { $_.areaId -eq "rooC" } | Select-Object -First 1
    Assert-True ($null -ne $rooC) "Plugin map layout output dynamic area rooC was not found."
    $tile0 = Convert-ToArray $rooC.tileSamples | Where-Object { $_.tileId -eq "tile0" } | Select-Object -First 1
    Assert-True ($null -ne $tile0) "Plugin map layout output dynamic tile rooC.tile0 was not found."
    Assert-True ($tile0.content -eq 8) "Plugin map layout output dynamic tile content was not updated."
    Assert-True ($tile0.knowledge -eq 1) "Plugin map layout output dynamic tile knowledge was not updated."
    Assert-True ([bool]$tile0.critScout) "Plugin map layout output dynamic tile critScout was not updated."

    $questChainReport = Get-Content -LiteralPath $questChainReportPath -Raw | ConvertFrom-Json
    Assert-True ([bool]$questChainReport.succeeded) "Plugin quest chain validation report did not succeed."
    Assert-True ($questChainReport.stageCount -eq 1) "Plugin quest chain stage count mismatch."
    Assert-True ([bool]$questChainReport.questBoard.enabled) "Plugin quest chain questBoard facts should be enabled."
    Assert-True ($questChainReport.questBoard.mode -eq "replaceWithFixedSet") "Plugin quest chain questBoard mode mismatch."
    Assert-True ($questChainReport.questBoard.questIds[0] -eq "plot_dd_4") "Plugin quest chain questBoard quest id mismatch."
    $stage = Convert-ToArray $questChainReport.orderedStages | Select-Object -First 1
    Assert-True ($stage.id -eq "stage_01_layout_probe") "Plugin quest chain stage id mismatch."
    Assert-True ($stage.sourceQuestId -eq "plot_dd_4") "Plugin quest chain source quest mismatch."
    Assert-True ($stage.mapReference.type -eq "mapLayoutTemplate") "Plugin quest chain map reference type mismatch."
    Assert-True ($stage.mapReference.id -eq "dd4_high_level_layout_probe") "Plugin quest chain map layout reference mismatch."
    Assert-True ($stage.mapReference.tileRuleCount -eq 1) "Plugin quest chain map reference tile rule count mismatch."

    $questChainManagedReport = Get-Content -LiteralPath $questChainManagedReportPath -Raw | ConvertFrom-Json
    Assert-True ($questChainManagedReport.status -eq "materialized") "Plugin quest chain managed report should be materialized."
    Assert-True ($questChainManagedReport.questIds[0] -eq "plot_dd_4") "Plugin quest chain managed report quest id mismatch."

    $questChainManagedArtifact = Get-Content -LiteralPath $questChainManagedArtifactPath -Raw | ConvertFrom-Json
    Assert-True ($questChainManagedArtifact.status -eq "materialized") "Plugin quest chain managed artifact should be materialized."
    Assert-True ($questChainManagedArtifact.action.type -eq "questBoard.replaceWithFixedSet") "Plugin quest chain managed action type mismatch."
    Assert-True ($questChainManagedArtifact.plan.arguments.questIds[0] -eq "plot_dd_4") "Plugin quest chain managed artifact quest id mismatch."
    $artifactStage = Convert-ToArray $questChainManagedArtifact.plan.arguments.stages | Select-Object -First 1
    Assert-True ($artifactStage.id -eq "stage_01_layout_probe") "Plugin quest chain managed artifact stage id mismatch."
    Assert-True ($artifactStage.mapReference.id -eq "dd4_high_level_layout_probe") "Plugin quest chain managed artifact map reference mismatch."

    $previewPath = Join-Path $previewRoot "maps_DD_map4.dm.preview.bin"
    $diffPath = Join-Path $previewRoot "maps_DD_map4.dm.diff.txt"
    Assert-True (Test-Path -LiteralPath $previewPath -PathType Leaf) "Plugin map layout overlay preview was not written: $previewPath"
    Assert-True (Test-Path -LiteralPath $diffPath -PathType Leaf) "Plugin map layout overlay diff was not written: $diffPath"

    $artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
    $previewHash = (Get-FileHash -LiteralPath $previewPath -Algorithm SHA256).Hash
    Assert-True ($previewHash -eq $artifactHash) "Plugin map layout overlay preview bytes did not match the generated artifact."
}

Test-MapReport -MapName "tutorial_crypts" -ExpectedAreas 16 -ExpectedRooms 8 -ExpectedCorridors 8 -ExpectedTiles 56 -ExpectedEntrance "rooH" -ExpectedFinal $null
Test-MapReport -MapName "DD_map4" -ExpectedAreas 4 -ExpectedRooms 3 -ExpectedCorridors 1 -ExpectedTiles 31 -ExpectedEntrance "rooA" -ExpectedFinal "rooB"
Test-MapFinalRoomPrototype
Test-MapTemplatePrototype
Test-MapVirtualSourceOverlay
Test-PluginMapTemplateOverlay
Test-PluginMapLayoutTemplateValidation

Write-Host "Map file inspector test passed. Output: $testRoot"
