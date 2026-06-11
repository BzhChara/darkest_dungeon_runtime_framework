param(
    [string]$LiveProfileDirectory = "E:\Steam\userdata\1097809614\262060\remote\profile_3",
    [string]$ExpectedQuestId = "plot_kill_necromancer_1",
    [string]$ExpectedDungeon = "crypts",
    [string]$ExpectedQuestType = "kill_boss",
    [int]$ExpectedDifficulty = 1,
    [int]$ExpectedLength = 2,
    [string]$ExpectedGoalId = "kill_necromancer_A",
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon",
    [string]$ExportScriptPath = "tools\ExportSaveSampleFacts.ps1",
    [string]$RuntimeLogPath = "logs\runtime_hook.log",
    [switch]$NoLogAssertions
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$propertyFlags = [System.Reflection.BindingFlags]"Public,Instance,IgnoreCase"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Resolve-ProjectPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $projectRoot.Path $Path)
}

function Get-ObjectProperty {
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

function Test-ValueContains {
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

    foreach ($item in Convert-ToArray $Actual) {
        if ($item -eq $Expected) {
            return $true
        }
    }

    return $false
}

function Get-LatestRuntimeHookSegment {
    param([string]$Path)

    $raw = Get-Content -Raw -LiteralPath $Path
    $marker = "RuntimeHook.dll loaded"
    $index = $raw.LastIndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase)
    if ($index -lt 0) {
        return $raw
    }

    return $raw.Substring($index)
}

function Assert-ContainsText {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    Assert-True ($Text.Contains($Needle)) $Message
}

function Assert-NotContainsText {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    Assert-True (-not $Text.Contains($Needle)) $Message
}

Push-Location $projectRoot.Path
try {
    $profilePath = Resolve-Path -LiteralPath $LiveProfileDirectory -ErrorAction SilentlyContinue
    Assert-True ($null -ne $profilePath) "Live profile directory was not found: $LiveProfileDirectory"

    $assemblyFullPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $AssemblyPath) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $assemblyFullPath) "Built launcher assembly was not found at '$AssemblyPath'. Run: dotnet build launcher/DDRuntimeLoader.csproj -c Release"

    $exportScriptFullPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $ExportScriptPath) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $exportScriptFullPath) "Export script was not found: $ExportScriptPath"

    $gameDirectoryPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $GameDirectory) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $gameDirectoryPath) "Game directory was not found: $GameDirectory"

    $exportScript = $exportScriptFullPath.Path
    $exportOutput = & $exportScript `
        -SampleDirectory $profilePath.Path `
        -AssemblyPath $assemblyFullPath.Path `
        -GameDirectory $gameDirectoryPath.Path `
        -SessionPrefix "live_profile_quest_overlay"

    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Save fact export failed with exit code $LASTEXITCODE"
    }

    $exportReport = ($exportOutput | Out-String) | ConvertFrom-Json
    Assert-True ([int]$exportReport.accessIssueCount -eq 0) "Save fact export reported access issues: $($exportReport.accessIssueCount)"

    $reportPath = Resolve-Path -LiteralPath ([string]$exportReport.output) -ErrorAction SilentlyContinue
    Assert-True ($null -ne $reportPath) "Save fact export report was not written: $($exportReport.output)"

    $payload = Get-Content -Raw -LiteralPath $reportPath.Path | ConvertFrom-Json
    Assert-True ((Convert-ToArray (Get-ObjectProperty $payload "accessIssues")).Count -eq 0) "Save fact export report contains access issues."

    $facts = Get-ObjectProperty $payload "facts"
    Assert-True ($null -ne $facts) "Save fact export report does not contain facts."

    $campaign = Get-ObjectProperty $facts "campaign"
    Assert-True ($null -ne $campaign) "Save facts do not contain campaign facts."
    Assert-True ((Get-ObjectProperty $campaign "inRaid") -eq $false) "Live test profile is not in town; open profile_3 in town before validating quest overlay."

    $questFacts = Get-ObjectProperty $facts "quest"
    Assert-True ($null -ne $questFacts) "Save facts do not contain quest facts."
    Assert-True ([int](Get-ObjectProperty $questFacts "questCount") -gt 0) "Save facts should contain at least one available quest."

    $quests = Convert-ToArray (Get-ObjectProperty $questFacts "quests")
    $matchingQuests = @($quests | Where-Object { (Get-ObjectProperty $_ "id") -eq $ExpectedQuestId })
    Assert-True ($matchingQuests.Count -eq 1) "Expected quest '$ExpectedQuestId' to appear exactly once in the live profile quest pool, found $($matchingQuests.Count)."

    $quest = $matchingQuests[0]
    Assert-True ((Get-ObjectProperty $quest "dungeon") -eq $ExpectedDungeon) "Expected quest dungeon '$ExpectedDungeon', found '$((Get-ObjectProperty $quest "dungeon"))'."
    Assert-True ((Get-ObjectProperty $quest "type") -eq $ExpectedQuestType) "Expected quest type '$ExpectedQuestType', found '$((Get-ObjectProperty $quest "type"))'."
    Assert-True ([int](Get-ObjectProperty $quest "difficulty") -eq $ExpectedDifficulty) "Expected quest difficulty $ExpectedDifficulty, found $((Get-ObjectProperty $quest "difficulty"))."
    Assert-True ([int](Get-ObjectProperty $quest "length") -eq $ExpectedLength) "Expected quest length $ExpectedLength, found $((Get-ObjectProperty $quest "length"))."
    Assert-True ((Get-ObjectProperty $quest "isPlotQuest") -eq $true) "Expected quest '$ExpectedQuestId' to be a plot quest."
    Assert-True (Test-ValueContains (Get-ObjectProperty $quest "goalIds") $ExpectedGoalId) "Expected quest '$ExpectedQuestId' to include goal id '$ExpectedGoalId'."

    if (-not $NoLogAssertions) {
        $runtimeLogFullPath = Resolve-Path -LiteralPath (Resolve-ProjectPath $RuntimeLogPath) -ErrorAction SilentlyContinue
        Assert-True ($null -ne $runtimeLogFullPath) "Runtime hook log was not found: $RuntimeLogPath"

        $latestLogSegment = Get-LatestRuntimeHookSegment -Path $runtimeLogFullPath.Path
        Assert-ContainsText $latestLogSegment "virtual-file served path=campaign\quest\quest.plot_quests.json" "Latest RuntimeHook run did not serve the quest plot virtual file."
        Assert-ContainsText $latestLogSegment "campaign\town\quest_select\quest_select.layout.darkest" "Latest RuntimeHook run did not reach the quest selection UI."
        Assert-ContainsText $latestLogSegment "campaign\town\embark_party\embark_party.layout.darkest" "Latest RuntimeHook run did not reach the embark party UI."
        Assert-NotContainsText $latestLogSegment "virtual-file failed to read original" "Latest RuntimeHook run reported a virtual file original-read failure."
        Assert-NotContainsText $latestLogSegment "Asset Failed" "Latest RuntimeHook run contains an asset failure."
        Assert-NotContainsText $latestLogSegment "Assert Failed" "Latest RuntimeHook run contains an assertion failure."
    }

    Write-Host "PASS: live profile quest overlay is present and runtime log evidence is valid."
    Write-Host "Report: $($reportPath.Path)"
    Write-Host "Quest: id=$ExpectedQuestId dungeon=$((Get-ObjectProperty $quest "dungeon")) type=$((Get-ObjectProperty $quest "type")) difficulty=$((Get-ObjectProperty $quest "difficulty")) length=$((Get-ObjectProperty $quest "length"))"
    if ($NoLogAssertions) {
        Write-Host "Runtime log assertions: skipped"
    } else {
        Write-Host "Runtime log assertions: passed"
    }
}
finally {
    Pop-Location
}
