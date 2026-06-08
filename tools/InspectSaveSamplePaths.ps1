param(
    [Parameter(Mandatory = $true)]
    [string]$SampleDirectory,
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string[]]$Files = @("persist.raid.json", "persist.curio_tracker.json", "novelty_tracker.json", "persist.loading_screen.json"),
    [int]$MaxScalarRows = 120
)

$ErrorActionPreference = "Stop"

$samplePath = (Resolve-Path -LiteralPath $SampleDirectory).Path
$assemblyFullPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
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

$summary = foreach ($fileName in $Files) {
    $path = [string](Join-Path $samplePath $fileName)
    $report = $inspect.Invoke($null, @([string]$path, [string]$fileName))
    $allScalars = Convert-ToArray (Get-ReportProperty $report "AllDsonScalars")
    if ($allScalars.Count -eq 0) {
        $allScalars = Convert-ToArray (Get-ReportProperty $report "DsonScalars")
    }

    $objectPaths = Convert-ToArray (Get-ReportProperty $report "DsonObjectPaths")
    $allPaths = @($objectPaths + ($allScalars | ForEach-Object { Get-ReportProperty $_ "Path" }))
    $rootChildIds = $allPaths |
        Where-Object { $_ -like "base_root.*" } |
        ForEach-Object { ($_ -replace "^base_root\.", "").Split(".")[0] } |
        Sort-Object -Unique

    [pscustomobject]@{
        fileName = $fileName
        parseStatus = Get-ReportProperty $report "ParseStatus"
        format = Get-ReportProperty $report "Format"
        scalarCount = $allScalars.Count
        objectPathCount = $objectPaths.Count
        rootChildIds = @($rootChildIds)
        scalarRows = @($allScalars |
            Select-Object -First $MaxScalarRows |
            ForEach-Object {
                [pscustomobject]@{
                    path = Get-ReportProperty $_ "Path"
                    type = Get-ReportProperty $_ "Type"
                    value = Get-ReportProperty $_ "Value"
                }
            })
    }
}

$summary | ConvertTo-Json -Depth 12
