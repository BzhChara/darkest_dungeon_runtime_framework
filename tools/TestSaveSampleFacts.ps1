param(
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon",
    [string]$ResearchRoot = ".research",
    [switch]$WriteSnapshots,
    [string]$OutputDirectory = "logs\save_state_tests"
)

$ErrorActionPreference = "Stop"

$assemblyFullPath = Resolve-Path -LiteralPath $AssemblyPath -ErrorAction SilentlyContinue
if ($null -eq $assemblyFullPath) {
    throw "Built launcher assembly was not found at '$AssemblyPath'. Run: dotnet build launcher/DDRuntimeLoader.csproj -c Release"
}

$researchRootPath = Resolve-Path -LiteralPath $ResearchRoot -ErrorAction SilentlyContinue
if ($null -eq $researchRootPath) {
    throw "Research root was not found at '$ResearchRoot'."
}

$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyFullPath.Path)
$exporter = $assembly.GetType("DDRuntimeLoader.SaveDirectoryWatcher+SaveStateExporter", $true)
$fileReportType = $assembly.GetType("DDRuntimeLoader.SaveDirectoryWatcher+SaveStateFileReport", $true)
$inspect = $exporter.GetMethod("InspectFile", [System.Reflection.BindingFlags]"NonPublic,Static")
$buildFacts = $exporter.GetMethod("BuildSaveStateFacts", [System.Reflection.BindingFlags]"NonPublic,Static")
$buildHeroDefinitions = $exporter.GetMethod("BuildHeroDefinitionFacts", [System.Reflection.BindingFlags]"NonPublic,Static")
$candidateFiles = [string[]]$exporter.GetField("CandidateFiles", [System.Reflection.BindingFlags]"NonPublic,Static").GetValue($null)
$optionalCandidateFiles = [string[]]$exporter.GetField("OptionalCandidateFiles", [System.Reflection.BindingFlags]"NonPublic,Static").GetValue($null)
$upgradeCatalogType = $exporter.GetNestedType("UpgradeDefinitionCatalog", [System.Reflection.BindingFlags]"NonPublic")
$contentHashCatalogType = $exporter.GetNestedType("ContentHashCatalog", [System.Reflection.BindingFlags]"NonPublic")
$propertyFlags = [System.Reflection.BindingFlags]"Public,Instance"

$fileNames = @($candidateFiles + $optionalCandidateFiles) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique

$catalogCache = @{}
$contentHashCatalog = $null
$checks = 0
$failures = [System.Collections.Generic.List[object]]::new()

function Get-ObjectProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $property = $Value.PSObject.Properties[$Name]
        if ($null -eq $property) {
            return $null
        }

        return $property.Value
    }

    $propertyInfo = $Value.GetType().GetProperty($Name, $propertyFlags)
    if ($null -eq $propertyInfo) {
        return $null
    }

    return $propertyInfo.GetValue($Value)
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Get-ScalarValue {
    param(
        [object]$FileReport,
        [string]$Path
    )

    foreach ($scalar in Convert-ToArray (Get-ObjectProperty $FileReport "AllDsonScalars")) {
        if ((Get-ObjectProperty $scalar "Path") -eq $Path) {
            return Get-ObjectProperty $scalar "Value"
        }
    }

    foreach ($scalar in Convert-ToArray (Get-ObjectProperty $FileReport "DsonScalars")) {
        if ((Get-ObjectProperty $scalar "Path") -eq $Path) {
            return Get-ObjectProperty $scalar "Value"
        }
    }

    return $null
}

function Get-FactPathValue {
    param(
        [object]$Root,
        [string]$Path
    )

    $current = $Root
    foreach ($part in $Path -split "\.") {
        if ($null -eq $current) {
            return $null
        }

        if ($part -match "^(?<name>[^\[]+)\[(?<index>\d+)\]$") {
            $current = Get-ObjectProperty $current $Matches.name
            $items = Convert-ToArray $current
            $index = [int]$Matches.index
            if ($index -ge $items.Count) {
                return $null
            }

            $current = $items[$index]
            continue
        }

        $current = Get-ObjectProperty $current $part
    }

    return $current
}

function Format-Value {
    param([object]$Value)

    if ($null -eq $Value) {
        return "<null>"
    }

    if ($Value -is [System.Array]) {
        return "[" + (($Value | ForEach-Object { Format-Value $_ }) -join ",") + "]"
    }

    return [string]$Value
}

function Add-Failure {
    param(
        [string]$Sample,
        [string]$Path,
        [object]$Expected,
        [object]$Actual
    )

    $script:failures.Add([pscustomobject]@{
        sample = $Sample
        path = $Path
        expected = Format-Value $Expected
        actual = Format-Value $Actual
    }) | Out-Null
}

function Assert-FactEqual {
    param(
        [string]$Sample,
        [object]$Facts,
        [string]$Path,
        [object]$Expected
    )

    $script:checks++
    $actual = Get-FactPathValue $Facts $Path
    if ($actual -ne $Expected) {
        Add-Failure $Sample $Path $Expected $actual
    }
}

function Assert-FactCount {
    param(
        [string]$Sample,
        [object]$Facts,
        [string]$Path,
        [int]$Expected
    )

    $script:checks++
    $actual = Convert-ToArray (Get-FactPathValue $Facts $Path)
    if ($actual.Count -ne $Expected) {
        Add-Failure $Sample "$Path.Count" $Expected $actual.Count
    }
}

function Assert-FactSequence {
    param(
        [string]$Sample,
        [object]$Facts,
        [string]$Path,
        [object[]]$Expected
    )

    $script:checks++
    $actual = Convert-ToArray (Get-FactPathValue $Facts $Path)
    $matches = $actual.Count -eq $Expected.Count
    if ($matches) {
        for ($i = 0; $i -lt $Expected.Count; $i++) {
            if ($actual[$i] -ne $Expected[$i]) {
                $matches = $false
                break
            }
        }
    }

    if (-not $matches) {
        Add-Failure $Sample $Path $Expected $actual
    }
}

function Assert-FactContains {
    param(
        [string]$Sample,
        [object]$Facts,
        [string]$Path,
        [object]$Expected
    )

    $script:checks++
    $actual = Convert-ToArray (Get-FactPathValue $Facts $Path)
    if ($Expected -notin $actual) {
        Add-Failure $Sample "$Path contains" $Expected $actual
    }
}

function Build-SampleFacts {
    param(
        [string]$Sample,
        [string]$RelativePath
    )

    $samplePath = Join-Path $researchRootPath.Path $RelativePath
    $resolvedSamplePath = Resolve-Path -LiteralPath $samplePath -ErrorAction SilentlyContinue
    if ($null -eq $resolvedSamplePath) {
        Add-Failure $Sample "sampleDirectory" $samplePath "<missing>"
        return $null
    }

    $fileReports = [System.Array]::CreateInstance($fileReportType, $fileNames.Count)
    for ($i = 0; $i -lt $fileNames.Count; $i++) {
        $fileName = [string]$fileNames[$i]
        $path = [string](Join-Path $resolvedSamplePath.Path $fileName)
        $fileReports.SetValue($inspect.Invoke($null, @($path, $fileName)), $i)
    }

    $accessIssues = [System.Collections.Generic.List[string]]::new()
    $gameReport = $fileReports |
        Where-Object { (Get-ObjectProperty $_ "FileName") -eq "persist.game.json" } |
        Select-Object -First 1
    $gameMode = Get-ScalarValue $gameReport "base_root.game_mode"
    if ([string]::IsNullOrWhiteSpace($gameMode)) {
        $gameMode = "base"
    }

    if ($null -eq $script:contentHashCatalog) {
        $script:contentHashCatalog = $contentHashCatalogType.GetMethod("Load", [System.Reflection.BindingFlags]"Public,Static").Invoke($null, @($GameDirectory, $accessIssues))
    }

    if (-not $script:catalogCache.ContainsKey($gameMode)) {
        $sampleAccessIssues = [System.Collections.Generic.List[string]]::new()
        $script:catalogCache[$gameMode] = [pscustomobject]@{
            UpgradeCatalog = $upgradeCatalogType.GetMethod("Load", [System.Reflection.BindingFlags]"Public,Static").Invoke($null, @($GameDirectory, $gameMode, $sampleAccessIssues))
            HeroDefinitions = $buildHeroDefinitions.Invoke($null, @($GameDirectory, $gameMode, $sampleAccessIssues))
            AccessIssues = $sampleAccessIssues
        }
    }

    $catalogs = $script:catalogCache[$gameMode]
    foreach ($issue in Convert-ToArray $catalogs.AccessIssues) {
        $accessIssues.Add($issue) | Out-Null
    }

    $facts = $buildFacts.Invoke($null, @($fileReports, $catalogs.UpgradeCatalog, $catalogs.HeroDefinitions, $script:contentHashCatalog))

    if ($WriteSnapshots) {
        New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
        $safeName = $Sample -replace "[^A-Za-z0-9_.-]", "_"
        $outputPath = Join-Path $OutputDirectory ("save_facts_test_" + $safeName + "_" + (Get-Date -Format "yyyyMMdd_HHmmss_fff") + ".json")
        [pscustomobject]@{
            version = 1
            generatedAt = [DateTimeOffset]::Now
            sample = $Sample
            sampleDirectory = $resolvedSamplePath.Path
            facts = $facts
            accessIssues = @($accessIssues)
        } | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $outputPath -Encoding UTF8
    }

    return [pscustomobject]@{
        Sample = $Sample
        Path = $resolvedSamplePath.Path
        Facts = $facts
        FileCount = $fileReports.Length
        AccessIssues = @($accessIssues)
    }
}

function Assert-CommonSample {
    param([object]$SampleFacts)

    if ($null -eq $SampleFacts) {
        return
    }

    $sample = $SampleFacts.Sample
    $script:checks++
    if ($SampleFacts.AccessIssues.Count -ne 0) {
        Add-Failure $sample "accessIssues.Count" 0 $SampleFacts.AccessIssues.Count
    }

    Assert-FactCount $sample $SampleFacts.Facts "PersistFiles" 23
}

function Test-Profile0Town {
    $sampleFacts = Build-SampleFacts "profile_0" "profile_0"
    Assert-CommonSample $sampleFacts
    if ($null -eq $sampleFacts) {
        return
    }

    $facts = $sampleFacts.Facts
    Assert-FactEqual "profile_0" $facts "Campaign.InRaid" $false
    Assert-FactEqual "profile_0" $facts "Campaign.EstateName" "极暗"
    Assert-FactEqual "profile_0" $facts "Campaign.GameMode" "base"
    Assert-FactEqual "profile_0" $facts "Estate.TrinketItemCount" 74
    Assert-FactEqual "profile_0" $facts "Estate.DarkestDungeonTrinketUnlocks.DirectChildCount" 0
    Assert-FactEqual "profile_0" $facts "Progression.CompletedPlotQuestDataCount" 12
    Assert-FactEqual "profile_0" $facts "Progression.FlashbackCompletionCount" 4
    Assert-FactEqual "profile_0" $facts "CurioTracker.TrackedResultCount" 40
    Assert-FactEqual "profile_0" $facts "NoveltyTracker.SeenEntryCount" 201
    Assert-FactCount "profile_0" $facts "Heroes" 25
    Assert-FactEqual "profile_0" $facts "Map.Exists" $false
    Assert-FactEqual "profile_0" $facts "Raid.Battle.Exists" $false
}

function Test-Profile0Backup {
    $sampleFacts = Build-SampleFacts "profile_0_backup" "profile_0\backup"
    Assert-CommonSample $sampleFacts
    if ($null -eq $sampleFacts) {
        return
    }

    $facts = $sampleFacts.Facts
    # This backup directory carries active raid/battle files even though persist.game keeps inraid=false.
    Assert-FactEqual "profile_0_backup" $facts "Campaign.InRaid" $false
    Assert-FactEqual "profile_0_backup" $facts "Raid.Instance.Id" "plot_kill_prophet_1"
    Assert-FactEqual "profile_0_backup" $facts "Raid.Instance.Dungeon" "crypts"
    Assert-FactEqual "profile_0_backup" $facts "Raid.Instance.Type" "kill_boss"
    Assert-FactEqual "profile_0_backup" $facts "Raid.Instance.IsPlotQuest" $true
    Assert-FactContains "profile_0_backup" $facts "Raid.Instance.GoalIds" "kill_prophet_A"
    Assert-FactEqual "profile_0_backup" $facts "Raid.Location.InBattle" $true
    Assert-FactEqual "profile_0_backup" $facts "Raid.Battle.Exists" $true
    Assert-FactEqual "profile_0_backup" $facts "Raid.Battle.Round" 2
    Assert-FactEqual "profile_0_backup" $facts "Raid.Battle.EnemyCount" 4
    Assert-FactEqual "profile_0_backup" $facts "Raid.Battle.HeroInitiativeCount" 4
    Assert-FactEqual "profile_0_backup" $facts "Raid.Battle.MonsterInitiativeCount" 3
    Assert-FactEqual "profile_0_backup" $facts "Raid.Battle.Enemies[0].MonsterClass" "skeleton_defender_A"
    Assert-FactEqual "profile_0_backup" $facts "Raid.Battle.Enemies[0].CurrentHp" 15
    Assert-FactEqual "profile_0_backup" $facts "Raid.Party.HeroCount" 4
    Assert-FactSequence "profile_0_backup" $facts "Raid.Party.HeroGuids" @(638, 562, 532, 586)
    Assert-FactEqual "profile_0_backup" $facts "Raid.Party.InventoryItemCount" 16
    Assert-FactEqual "profile_0_backup" $facts "LoadingScreen.BackgroundTexturePath" "loading_screen/loading_screen.plot_kill_prophet_1.png"
    Assert-FactEqual "profile_0_backup" $facts "Map.Exists" $true
    Assert-FactEqual "profile_0_backup" $facts "Map.HasStaticSave" $true
    Assert-FactEqual "profile_0_backup" $facts "Map.StaticSave.RawScalarCount" 0
    Assert-FactEqual "profile_0_backup" $facts "Map.AreaCount" 24
    Assert-FactEqual "profile_0_backup" $facts "Map.DynamicAreaCount" 24
    Assert-FactEqual "profile_0_backup" $facts "Map.DynamicTileCount" 85
    Assert-FactEqual "profile_0_backup" $facts "Map.Populated" $true
    Assert-FactEqual "profile_0_backup" $facts "Map.EntranceAreaId" "rooQ"
    Assert-FactEqual "profile_0_backup" $facts "Map.FinalRoomId" "rooT"
    Assert-FactCount "profile_0_backup" $facts "Heroes" 25
}

function Test-Profile1Raid {
    $sampleFacts = Build-SampleFacts "profile_1" "profile_1"
    Assert-CommonSample $sampleFacts
    if ($null -eq $sampleFacts) {
        return
    }

    $facts = $sampleFacts.Facts
    Assert-FactEqual "profile_1" $facts "Campaign.InRaid" $true
    Assert-FactEqual "profile_1" $facts "Raid.Instance.Id" "generated_0"
    Assert-FactEqual "profile_1" $facts "Raid.Instance.Dungeon" "cove"
    Assert-FactEqual "profile_1" $facts "Raid.Instance.Type" "cleanse"
    Assert-FactContains "profile_1" $facts "Raid.Instance.GoalIds" "battle_all_rooms"
    Assert-FactEqual "profile_1" $facts "Raid.Location.InBattle" $false
    Assert-FactEqual "profile_1" $facts "Raid.Battle.Exists" $false
    Assert-FactEqual "profile_1" $facts "Raid.Party.HeroCount" 4
    Assert-FactSequence "profile_1" $facts "Raid.Party.HeroGuids" @(356, 339, 200, 25)
    Assert-FactEqual "profile_1" $facts "Raid.Party.InventoryItemCount" 16
    Assert-FactEqual "profile_1" $facts "LoadingScreen.BackgroundTexturePath" "loading_screen/loading_screen.cove_0.png"
    Assert-FactEqual "profile_1" $facts "Map.Exists" $true
    Assert-FactEqual "profile_1" $facts "Map.HasStaticSave" $true
    Assert-FactEqual "profile_1" $facts "Map.StaticSave.RawScalarCount" 0
    Assert-FactEqual "profile_1" $facts "Map.AreaCount" 21
    Assert-FactEqual "profile_1" $facts "Map.DynamicAreaCount" 21
    Assert-FactEqual "profile_1" $facts "Map.DynamicTileCount" 74
    Assert-FactEqual "profile_1" $facts "Map.Populated" $true
    Assert-FactEqual "profile_1" $facts "Map.EntranceAreaId" "rooR"
    Assert-FactEqual "profile_1" $facts "CurioTracker.TrackedResultCount" 52
    Assert-FactEqual "profile_1" $facts "NoveltyTracker.SeenEntryCount" 148
    Assert-FactCount "profile_1" $facts "Heroes" 21
}

Write-Host "Running save sample fact regression tests..."
Test-Profile0Town
Test-Profile0Backup
Test-Profile1Raid

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "FAILED: $($failures.Count) assertion(s) failed out of $checks."
    $failures | Format-Table -AutoSize
    exit 1
}

Write-Host "PASS: $checks save fact assertions passed."
