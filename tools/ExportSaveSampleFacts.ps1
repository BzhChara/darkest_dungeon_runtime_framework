param(
    [Parameter(Mandatory = $true)]
    [string]$SampleDirectory,
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon",
    [string]$OutputDirectory = "logs\save_states",
    [string]$SessionPrefix = "research"
)

$ErrorActionPreference = "Stop"

$samplePath = (Resolve-Path -LiteralPath $SampleDirectory).Path
$assemblyFullPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyFullPath)
$exporter = $assembly.GetType("DDRuntimeLoader.SaveDirectoryWatcher+SaveStateExporter", $true)
$fileReportType = $assembly.GetType("DDRuntimeLoader.SaveDirectoryWatcher+SaveStateFileReport", $true)
$inspect = $exporter.GetMethod("InspectFile", [System.Reflection.BindingFlags]"NonPublic,Static")
$buildFacts = $exporter.GetMethod("BuildSaveStateFacts", [System.Reflection.BindingFlags]"NonPublic,Static")
$buildHeroDefinitions = $exporter.GetMethod("BuildHeroDefinitionFacts", [System.Reflection.BindingFlags]"NonPublic,Static")
$candidateFiles = [string[]]$exporter.GetField("CandidateFiles", [System.Reflection.BindingFlags]"NonPublic,Static").GetValue($null)
$optionalCandidateFiles = [string[]]$exporter.GetField("OptionalCandidateFiles", [System.Reflection.BindingFlags]"NonPublic,Static").GetValue($null)
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

function ConvertTo-CamelCaseObject {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $name = [string]$property.Name
            if (-not [string]::IsNullOrEmpty($name)) {
                $name = $name.Substring(0, 1).ToLowerInvariant() + $name.Substring(1)
            }

            $result[$name] = ConvertTo-CamelCaseObject $property.Value
        }

        return [pscustomobject]$result
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return @($Value | ForEach-Object { ConvertTo-CamelCaseObject $_ })
    }

    return $Value
}

function Get-ScalarValue {
    param(
        [object]$FileReport,
        [string]$Path
    )

    foreach ($scalar in Convert-ToArray (Get-ReportProperty $FileReport "AllDsonScalars")) {
        if ((Get-ReportProperty $scalar "Path") -eq $Path) {
            return Get-ReportProperty $scalar "Value"
        }
    }

    foreach ($scalar in Convert-ToArray (Get-ReportProperty $FileReport "DsonScalars")) {
        if ((Get-ReportProperty $scalar "Path") -eq $Path) {
            return Get-ReportProperty $scalar "Value"
        }
    }

    return $null
}

$fileNames = @($candidateFiles + $optionalCandidateFiles) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique

$fileReports = [System.Array]::CreateInstance($fileReportType, $fileNames.Count)
for ($i = 0; $i -lt $fileNames.Count; $i++) {
    $fileName = [string]$fileNames[$i]
    $path = [string](Join-Path $samplePath $fileName)
    $fileReports.SetValue($inspect.Invoke($null, @($path, $fileName)), $i)
}

$accessIssues = [System.Collections.Generic.List[string]]::new()
$gameReport = $fileReports | Where-Object { (Get-ReportProperty $_ "FileName") -eq "persist.game.json" } | Select-Object -First 1
$gameMode = Get-ScalarValue $gameReport "base_root.game_mode"

$upgradeCatalogType = $exporter.GetNestedType("UpgradeDefinitionCatalog", [System.Reflection.BindingFlags]"NonPublic")
$contentHashCatalogType = $exporter.GetNestedType("ContentHashCatalog", [System.Reflection.BindingFlags]"NonPublic")
$upgradeCatalog = $upgradeCatalogType.GetMethod("Load", [System.Reflection.BindingFlags]"Public,Static").Invoke($null, @($GameDirectory, $gameMode, $accessIssues))
$heroDefinitions = $buildHeroDefinitions.Invoke($null, @($GameDirectory, $gameMode, $accessIssues))
$contentHashCatalog = $contentHashCatalogType.GetMethod("Load", [System.Reflection.BindingFlags]"Public,Static").Invoke($null, @($GameDirectory, $accessIssues))
$facts = $buildFacts.Invoke($null, @($fileReports, $upgradeCatalog, $heroDefinitions, $contentHashCatalog))

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$sampleName = Split-Path -Leaf $samplePath
$sessionId = "$SessionPrefix`_$sampleName`_" + (Get-Date -Format "yyyyMMdd_HHmmss_fff")
$outputPath = Join-Path $OutputDirectory "$sessionId.json"

$payload = [pscustomobject]@{
    version = 1
    sessionId = $sessionId
    generatedAt = [DateTimeOffset]::Now
    sample = $sampleName
    sampleDirectory = $samplePath
    gameDirectory = $GameDirectory
    facts = $facts
    accessIssues = @($accessIssues)
}

$payloadJson = $payload | ConvertTo-Json -Depth 80
$camelPayload = ConvertTo-CamelCaseObject ($payloadJson | ConvertFrom-Json)
$camelPayload | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $outputPath -Encoding UTF8
[pscustomobject]@{
    output = (Resolve-Path -LiteralPath $outputPath).Path
    sample = $sampleName
    fileCount = $fileReports.Length
    accessIssueCount = $accessIssues.Count
} | ConvertTo-Json -Depth 4
