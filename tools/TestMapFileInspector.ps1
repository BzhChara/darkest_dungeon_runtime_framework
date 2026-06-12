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
}

Test-MapReport -MapName "tutorial_crypts" -ExpectedAreas 16 -ExpectedRooms 8 -ExpectedCorridors 8 -ExpectedTiles 56 -ExpectedEntrance "rooH" -ExpectedFinal $null
Test-MapReport -MapName "DD_map4" -ExpectedAreas 4 -ExpectedRooms 3 -ExpectedCorridors 1 -ExpectedTiles 31 -ExpectedEntrance "rooA" -ExpectedFinal "rooB"
Test-MapFinalRoomPrototype

Write-Host "Map file inspector test passed. Output: $testRoot"
