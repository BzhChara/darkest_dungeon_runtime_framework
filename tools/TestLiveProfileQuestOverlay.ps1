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

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot

Push-Location $projectRoot
try {
    $factsResult = Export-DdrtLiveSaveFacts `
        -LiveProfileDirectory $LiveProfileDirectory `
        -AssemblyPath $AssemblyPath `
        -GameDirectory $GameDirectory `
        -ExportScriptPath $ExportScriptPath `
        -SessionPrefix "live_profile_quest_overlay"
    $facts = $factsResult.facts

    $campaign = Get-DdrtObjectProperty $facts "campaign"
    Assert-DdrtTrue ($null -ne $campaign) "Save facts do not contain campaign facts."
    Assert-DdrtTrue ((Get-DdrtObjectProperty $campaign "inRaid") -eq $false) "Live test profile is not in town; open profile_3 in town before validating quest overlay."

    $questFacts = Get-DdrtObjectProperty $facts "quest"
    Assert-DdrtTrue ($null -ne $questFacts) "Save facts do not contain quest facts."
    Assert-DdrtTrue ([int](Get-DdrtObjectProperty $questFacts "questCount") -gt 0) "Save facts should contain at least one available quest."

    $quests = ConvertTo-DdrtArray (Get-DdrtObjectProperty $questFacts "quests")
    $matchingQuests = @($quests | Where-Object { (Get-DdrtObjectProperty $_ "id") -eq $ExpectedQuestId })
    Assert-DdrtTrue ($matchingQuests.Count -eq 1) "Expected quest '$ExpectedQuestId' to appear exactly once in the live profile quest pool, found $($matchingQuests.Count)."

    $quest = $matchingQuests[0]
    Assert-DdrtTrue ((Get-DdrtObjectProperty $quest "dungeon") -eq $ExpectedDungeon) "Expected quest dungeon '$ExpectedDungeon', found '$((Get-DdrtObjectProperty $quest "dungeon"))'."
    Assert-DdrtTrue ((Get-DdrtObjectProperty $quest "type") -eq $ExpectedQuestType) "Expected quest type '$ExpectedQuestType', found '$((Get-DdrtObjectProperty $quest "type"))'."
    Assert-DdrtTrue ([int](Get-DdrtObjectProperty $quest "difficulty") -eq $ExpectedDifficulty) "Expected quest difficulty $ExpectedDifficulty, found $((Get-DdrtObjectProperty $quest "difficulty"))."
    Assert-DdrtTrue ([int](Get-DdrtObjectProperty $quest "length") -eq $ExpectedLength) "Expected quest length $ExpectedLength, found $((Get-DdrtObjectProperty $quest "length"))."
    Assert-DdrtTrue ((Get-DdrtObjectProperty $quest "isPlotQuest") -eq $true) "Expected quest '$ExpectedQuestId' to be a plot quest."
    Assert-DdrtTrue (Test-DdrtContainsValue (Get-DdrtObjectProperty $quest "goalIds") $ExpectedGoalId) "Expected quest '$ExpectedQuestId' to include goal id '$ExpectedGoalId'."

    if (-not $NoLogAssertions) {
        $runtimeLogFullPath = Get-DdrtResolvedPath `
            -Path $RuntimeLogPath `
            -Leaf `
            -MissingMessage "Runtime hook log was not found: $RuntimeLogPath"

        $latestLogSegment = Get-DdrtLatestRuntimeHookSegment -Path $runtimeLogFullPath
        Assert-DdrtContainsText $latestLogSegment "virtual-file served path=campaign\quest\quest.plot_quests.json" "Latest RuntimeHook run did not serve the quest plot virtual file."
        Assert-DdrtContainsText $latestLogSegment "campaign\town\quest_select\quest_select.layout.darkest" "Latest RuntimeHook run did not reach the quest selection UI."
        Assert-DdrtContainsText $latestLogSegment "campaign\town\embark_party\embark_party.layout.darkest" "Latest RuntimeHook run did not reach the embark party UI."
        Assert-DdrtNotContainsText $latestLogSegment "virtual-file failed to read original" "Latest RuntimeHook run reported a virtual file original-read failure."
        Assert-DdrtNotContainsText $latestLogSegment "Asset Failed" "Latest RuntimeHook run contains an asset failure."
        Assert-DdrtNotContainsText $latestLogSegment "Assert Failed" "Latest RuntimeHook run contains an assertion failure."
    }

    Write-Host "PASS: live profile quest overlay is present and runtime log evidence is valid."
    Write-Host "Report: $($factsResult.reportPath)"
    Write-Host "Quest: id=$ExpectedQuestId dungeon=$((Get-DdrtObjectProperty $quest "dungeon")) type=$((Get-DdrtObjectProperty $quest "type")) difficulty=$((Get-DdrtObjectProperty $quest "difficulty")) length=$((Get-DdrtObjectProperty $quest "length"))"
    if ($NoLogAssertions) {
        Write-Host "Runtime log assertions: skipped"
    } else {
        Write-Host "Runtime log assertions: passed"
    }
}
finally {
    Pop-Location
}
