param(
    [string]$ResourceRoot = ".research\DarkestDungeonSaveEditor-0.0.70\src\test\resources",
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string]$OutputDirectory = "logs\research_save_samples",
    [string]$LocalResearchRoot = ".research",
    [switch]$NoLocalProfiles
)

$ErrorActionPreference = "Stop"

$resourceRootPath = Resolve-Path -LiteralPath $ResourceRoot
$assemblyFullPath = Resolve-Path -LiteralPath $AssemblyPath
$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyFullPath)
$exporter = $assembly.GetType("DDRuntimeLoader.SaveDirectoryWatcher+SaveStateExporter", $true)
$inspect = $exporter.GetMethod("InspectFile", [System.Reflection.BindingFlags]"NonPublic,Static")
$propertyFlags = [System.Reflection.BindingFlags]"Public,Instance"

function Get-ReportProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($null -eq $Value) {
        return $null
    }

    $property = $Value.GetType().GetProperty($Name, $propertyFlags)
    if ($null -eq $property) {
        return $null
    }

    return $property.GetValue($Value)
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

$sampleDirectories = @(Get-ChildItem -LiteralPath $resourceRootPath -Directory | ForEach-Object {
    [pscustomobject]@{
        Name = $_.Name
        FullName = $_.FullName
    }
})
if (-not $NoLocalProfiles -and (Test-Path -LiteralPath $LocalResearchRoot)) {
    $localResearchPath = (Resolve-Path -LiteralPath $LocalResearchRoot).Path
    foreach ($profileDirectory in Get-ChildItem -LiteralPath $localResearchPath -Directory -Filter "profile_*") {
        $sampleDirectories += [pscustomobject]@{
            Name = $profileDirectory.Name
            FullName = $profileDirectory.FullName
        }

        $backupPath = Join-Path $profileDirectory.FullName "backup"
        if (Test-Path -LiteralPath $backupPath) {
            $sampleDirectories += [pscustomobject]@{
                Name = "$($profileDirectory.Name)_backup"
                FullName = $backupPath
            }
        }
    }
}

$sampleDirectories = $sampleDirectories |
    Group-Object FullName |
    ForEach-Object { $_.Group[0] } |
    Sort-Object Name
$summary = foreach ($directory in $sampleDirectories) {
    $files = Get-ChildItem -LiteralPath $directory.FullName -File -Filter "*.json" | Sort-Object Name
    $fileRows = foreach ($file in $files) {
        $report = $inspect.Invoke($null, @($file.FullName, $file.Name))
        $allScalars = Convert-ToArray (Get-ReportProperty $report "AllDsonScalars")
        if ($allScalars.Count -eq 0) {
            $allScalars = Convert-ToArray (Get-ReportProperty $report "DsonScalars")
        }

        $objectPaths = Convert-ToArray (Get-ReportProperty $report "DsonObjectPaths")
        $scalarTypes = [ordered]@{}
        foreach ($scalar in $allScalars) {
            $type = [string](Get-ReportProperty $scalar "Type")
            if ([string]::IsNullOrWhiteSpace($type)) {
                $type = "<none>"
            }

            if (-not $scalarTypes.Contains($type)) {
                $scalarTypes[$type] = 0
            }

            $scalarTypes[$type]++
        }

        $interesting = $allScalars |
            Where-Object {
                $path = [string](Get-ReportProperty $_ "Path")
                $type = [string](Get-ReportProperty $_ "Type")
                $type -in @("intVector", "stringVector", "floatArray", "intPair") -or
                    $type -eq "embeddedDson" -or
                    $path -like "*dead_hero_entries*" -or
                    $path -like "*skill_cooldown*" -or
                    $path -like "*background*" -or
                    $path -like "*backer_heroes*" -or
                    $path -like "*narration_audio_event_queue_tags*" -or
                    $path -like "*valid_additional_mash_entry_indexes*" -or
                    $path -like "*raid_finish_quirk_monster_class_ids*" -or
                    $path -like "*use_default_progression_goals*" -or
                    $path -like "*bounds*" -or
                    $path -like "*mappos*" -or
                    $path -like "*sidepos*"
            } |
            Select-Object -First 80 |
            ForEach-Object {
                [pscustomobject]@{
                    path = Get-ReportProperty $_ "Path"
                    type = Get-ReportProperty $_ "Type"
                    value = Get-ReportProperty $_ "Value"
                }
            }

        $rawScalarPaths = $allScalars |
            Where-Object { [string](Get-ReportProperty $_ "Type") -eq "raw" } |
            Select-Object -First 40 |
            ForEach-Object { Get-ReportProperty $_ "Path" }

        $rawScalarSamples = $allScalars |
            Where-Object { [string](Get-ReportProperty $_ "Type") -eq "raw" } |
            Select-Object -First 40 |
            ForEach-Object {
                [pscustomobject]@{
                    path = Get-ReportProperty $_ "Path"
                    size = Get-ReportProperty $_ "Size"
                    rawHex = Get-ReportProperty $_ "RawHex"
                }
            }

        $embeddedDsonScalars = $allScalars |
            Where-Object { [string](Get-ReportProperty $_ "Type") -eq "embeddedDson" } |
            Select-Object -First 40 |
            ForEach-Object {
                $embedded = Get-ReportProperty $_ "EmbeddedDson"
                $dsonSummary = Get-ReportProperty $embedded "DsonSummary"
                [pscustomobject]@{
                    path = Get-ReportProperty $_ "Path"
                    length = Get-ReportProperty $embedded "Length"
                    objectCount = Get-ReportProperty $dsonSummary "ObjectCount"
                    fieldCount = Get-ReportProperty $dsonSummary "FieldCount"
                    parsedScalarCount = Get-ReportProperty $dsonSummary "ParsedScalarCount"
                    rawScalarCount = Get-ReportProperty $dsonSummary "RawScalarCount"
                    rootChildIds = Convert-ToArray (Get-ReportProperty $embedded "RootChildIds")
                }
            }

        [pscustomobject]@{
            fileName = $file.Name
            parseStatus = Get-ReportProperty $report "ParseStatus"
            format = Get-ReportProperty $report "Format"
            scalarCount = $allScalars.Count
            objectPathCount = $objectPaths.Count
            typedScalarCounts = $scalarTypes
            rawScalarPaths = @($rawScalarPaths)
            rawScalarSamples = @($rawScalarSamples)
            embeddedDsonScalars = @($embeddedDsonScalars)
            accessIssues = Convert-ToArray (Get-ReportProperty $report "AccessIssues")
            interestingScalars = @($interesting)
        }
    }

    [pscustomobject]@{
        sample = $directory.Name
        fileCount = $fileRows.Count
        files = @($fileRows)
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputPath = Join-Path $OutputDirectory ("saveeditor_samples_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".json")
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $outputPath -Encoding UTF8

$fileRows = @($summary | ForEach-Object { $_.files })
$scalarTypes = $fileRows |
    ForEach-Object { $_.typedScalarCounts.GetEnumerator() } |
    Group-Object Name |
    Sort-Object Name |
    ForEach-Object {
        [pscustomobject]@{
            type = $_.Name
            count = ($_.Group | Measure-Object Value -Sum).Sum
        }
    }

[pscustomobject]@{
    output = (Resolve-Path -LiteralPath $outputPath).Path
    sampleCount = @($summary).Count
    fileCount = $fileRows.Count
    filesWithAccessIssues = @($fileRows | Where-Object { $_.accessIssues.Count -gt 0 }).Count
    filesByParseStatus = @($fileRows | Group-Object parseStatus | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            parseStatus = $_.Name
            count = $_.Count
        }
    })
    scalarTypes = @($scalarTypes)
} | ConvertTo-Json -Depth 8
