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

Test-MapReport -MapName "tutorial_crypts" -ExpectedAreas 16 -ExpectedRooms 8 -ExpectedCorridors 8 -ExpectedTiles 56 -ExpectedEntrance "rooH" -ExpectedFinal $null
Test-MapReport -MapName "DD_map4" -ExpectedAreas 4 -ExpectedRooms 3 -ExpectedCorridors 1 -ExpectedTiles 31 -ExpectedEntrance "rooA" -ExpectedFinal "rooB"

Write-Host "Map file inspector test passed. Output: $testRoot"
