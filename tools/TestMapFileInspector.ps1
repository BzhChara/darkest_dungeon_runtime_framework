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
    Assert-True ((Convert-ToArray $report.mutations).Count -eq 4) "Template prototype mutation count mismatch."
    Assert-True ($report.outputMap.finalRoomId -eq "rooC") "Template prototype output final room was not updated."
    Assert-True ($report.outputMap.areaCount -eq 4) "Template prototype output area count changed."
    Assert-True ($report.outputMap.roomCount -eq 3) "Template prototype output room count changed."
    Assert-True ($report.outputMap.corridorCount -eq 1) "Template prototype output corridor count changed."
    Assert-True ($report.outputMap.tileCount -eq 31) "Template prototype output tile count changed."

    $rooC = Convert-ToArray $report.outputMap.dynamicAreas | Where-Object { $_.areaId -eq "rooC" } | Select-Object -First 1
    Assert-True ($null -ne $rooC) "Template prototype output dynamic area rooC was not found."
    $tile0 = Convert-ToArray $rooC.tileSamples | Where-Object { $_.tileId -eq "tile0" } | Select-Object -First 1
    Assert-True ($null -ne $tile0) "Template prototype output dynamic tile rooC.tile0 was not found."
    Assert-True ($tile0.content -eq 8) "Template prototype output dynamic tile content was not updated."
    Assert-True ($tile0.knowledge -eq 1) "Template prototype output dynamic tile knowledge was not updated."
    Assert-True ([bool]$tile0.critScout) "Template prototype output dynamic tile critScout was not updated."

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

Test-MapReport -MapName "tutorial_crypts" -ExpectedAreas 16 -ExpectedRooms 8 -ExpectedCorridors 8 -ExpectedTiles 56 -ExpectedEntrance "rooH" -ExpectedFinal $null
Test-MapReport -MapName "DD_map4" -ExpectedAreas 4 -ExpectedRooms 3 -ExpectedCorridors 1 -ExpectedTiles 31 -ExpectedEntrance "rooA" -ExpectedFinal "rooB"
Test-MapFinalRoomPrototype
Test-MapTemplatePrototype
Test-MapVirtualSourceOverlay

Write-Host "Map file inspector test passed. Output: $testRoot"
