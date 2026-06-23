$script:DdrtProjectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$script:DdrtPropertyFlags = [System.Reflection.BindingFlags]"Public,Instance,IgnoreCase"

function Get-DdrtProjectRoot {
    return $script:DdrtProjectRoot.Path
}

function Assert-DdrtTrue {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Resolve-DdrtProjectPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $script:DdrtProjectRoot.Path $Path)
}

function Get-DdrtResolvedPath {
    param(
        [string]$Path,
        [string]$MissingMessage,
        [switch]$Leaf,
        [switch]$Container
    )

    $resolved = Resolve-Path -LiteralPath (Resolve-DdrtProjectPath $Path) -ErrorAction SilentlyContinue
    Assert-DdrtTrue ($null -ne $resolved) $MissingMessage

    if ($Leaf) {
        Assert-DdrtTrue (Test-Path -LiteralPath $resolved.Path -PathType Leaf) $MissingMessage
    }

    if ($Container) {
        Assert-DdrtTrue (Test-Path -LiteralPath $resolved.Path -PathType Container) $MissingMessage
    }

    return $resolved.Path
}

function Get-DdrtObjectProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $property = $Value.PSObject.Properties |
            Where-Object { $_.Name -ieq $Name } |
            Select-Object -First 1
        if ($null -eq $property) {
            return $null
        }

        return $property.Value
    }

    $propertyInfo = $Value.GetType().GetProperty($Name, $script:DdrtPropertyFlags)
    if ($null -eq $propertyInfo) {
        return $null
    }

    return $propertyInfo.GetValue($Value)
}

function ConvertTo-DdrtArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Get-DdrtPathValue {
    param(
        [object]$Root,
        [string]$Path
    )

    $current = $Root
    foreach ($part in $Path -split "\.") {
        if ($null -eq $current) {
            return $null
        }

        $current = Get-DdrtObjectProperty $current $part
    }

    return $current
}

function Test-DdrtContainsValue {
    param(
        [object]$Actual,
        [object]$Expected
    )

    if ($null -eq $Actual) {
        return $false
    }

    if ($Actual -is [string]) {
        return $Actual -eq $Expected
    }

    foreach ($item in ConvertTo-DdrtArray $Actual) {
        if ([string]$item -eq [string]$Expected) {
            return $true
        }
    }

    return $false
}

function Get-DdrtLatestRuntimeHookSegment {
    param([string]$Path)

    $raw = Get-Content -Raw -LiteralPath $Path
    $marker = "RuntimeHook.dll loaded"
    $index = $raw.LastIndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase)
    if ($index -lt 0) {
        return $raw
    }

    return $raw.Substring($index)
}

function Assert-DdrtContainsText {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    Assert-DdrtTrue ($Text.Contains($Needle)) $Message
}

function Assert-DdrtNotContainsText {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    Assert-DdrtTrue (-not $Text.Contains($Needle)) $Message
}

function Invoke-DdrtLoader {
    param([string[]]$LoaderArgs)

    & dotnet run --project "launcher/DDRuntimeLoader.csproj" -c Release --no-build -- @LoaderArgs
    if ($LASTEXITCODE -ne 0) {
        throw "DDRuntimeLoader failed with exit code $LASTEXITCODE"
    }
}

function Export-DdrtLiveSaveFacts {
    param(
        [string]$LiveProfileDirectory,
        [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
        [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon",
        [string]$ExportScriptPath = "tools\ExportSaveSampleFacts.ps1",
        [string]$SessionPrefix
    )

    $profilePath = Resolve-Path -LiteralPath $LiveProfileDirectory -ErrorAction SilentlyContinue
    Assert-DdrtTrue ($null -ne $profilePath) "Live profile directory was not found: $LiveProfileDirectory"

    $assemblyFullPath = Resolve-Path -LiteralPath (Resolve-DdrtProjectPath $AssemblyPath) -ErrorAction SilentlyContinue
    Assert-DdrtTrue ($null -ne $assemblyFullPath) "Built launcher assembly was not found at '$AssemblyPath'. Run: dotnet build launcher/DDRuntimeLoader.csproj -c Release"

    $exportScriptFullPath = Resolve-Path -LiteralPath (Resolve-DdrtProjectPath $ExportScriptPath) -ErrorAction SilentlyContinue
    Assert-DdrtTrue ($null -ne $exportScriptFullPath) "Export script was not found: $ExportScriptPath"

    $gameDirectoryPath = Resolve-Path -LiteralPath (Resolve-DdrtProjectPath $GameDirectory) -ErrorAction SilentlyContinue
    Assert-DdrtTrue ($null -ne $gameDirectoryPath) "Game directory was not found: $GameDirectory"

    $exportScript = $exportScriptFullPath.Path
    $exportOutput = & $exportScript `
        -SampleDirectory $profilePath.Path `
        -AssemblyPath $assemblyFullPath.Path `
        -GameDirectory $gameDirectoryPath.Path `
        -SessionPrefix $SessionPrefix

    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Save fact export failed with exit code $LASTEXITCODE"
    }

    $exportReport = ($exportOutput | Out-String) | ConvertFrom-Json
    Assert-DdrtTrue ([int]$exportReport.accessIssueCount -eq 0) "Save fact export reported access issues: $($exportReport.accessIssueCount)"

    $reportPath = Resolve-Path -LiteralPath ([string]$exportReport.output) -ErrorAction SilentlyContinue
    Assert-DdrtTrue ($null -ne $reportPath) "Save fact export report was not written: $($exportReport.output)"

    $payload = Get-Content -Raw -LiteralPath $reportPath.Path | ConvertFrom-Json
    Assert-DdrtTrue ((ConvertTo-DdrtArray (Get-DdrtObjectProperty $payload "accessIssues")).Count -eq 0) "Save fact export report contains access issues."

    $facts = Get-DdrtObjectProperty $payload "facts"
    Assert-DdrtTrue ($null -ne $facts) "Save fact export report does not contain facts."

    return [pscustomobject]@{
        exportReport = $exportReport
        reportPath = $reportPath.Path
        payload = $payload
        facts = $facts
    }
}

function Read-DdrtSaveEventBridgeReport {
    param([string]$Path = "logs\save_event_bridge_report.json")

    $reportPath = Resolve-DdrtProjectPath $Path
    Assert-DdrtTrue (Test-Path -LiteralPath $reportPath -PathType Leaf) "Save event bridge report was not written: $reportPath"
    return [pscustomobject]@{
        path = $reportPath
        report = (Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json)
    }
}

function Get-DdrtExecutedEventIds {
    param([object]$BridgeReport)

    return @(
        ConvertTo-DdrtArray (Get-DdrtObjectProperty $BridgeReport "plugins") |
            Where-Object { (Get-DdrtObjectProperty $_ "status") -eq "event-executed" } |
            ForEach-Object { Get-DdrtObjectProperty $_ "eventId" }
    )
}

Export-ModuleMember -Function `
    Get-DdrtProjectRoot, `
    Assert-DdrtTrue, `
    Resolve-DdrtProjectPath, `
    Get-DdrtResolvedPath, `
    Get-DdrtObjectProperty, `
    ConvertTo-DdrtArray, `
    Get-DdrtPathValue, `
    Test-DdrtContainsValue, `
    Get-DdrtLatestRuntimeHookSegment, `
    Assert-DdrtContainsText, `
    Assert-DdrtNotContainsText, `
    Invoke-DdrtLoader, `
    Export-DdrtLiveSaveFacts, `
    Read-DdrtSaveEventBridgeReport, `
    Get-DdrtExecutedEventIds
