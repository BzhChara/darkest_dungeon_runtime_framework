param(
    [string]$ChallengePath = "plugins\_validation\challenge_run_contract\challenge.json",
    [string]$StatePath = "plugins\_validation\challenge_run_contract\sample_state.json",
    [string]$SampleDirectory = ".research\profile_0",
    [string]$AssemblyPath = "launcher\bin\Release\net8.0-windows\DDRuntimeLoader.dll",
    [string]$GameDirectory = "E:\Steam\steamapps\common\DarkestDungeon",
    [string[]]$SelectedHeroIds = @(),
    [string[]]$SelectedTrinketIds = @(),
    [ValidateSet("preview", "selection_confirmed", "stage_failed", "stage_completed")]
    [string]$Outcome = "preview",
    [string]$OutputPath = "",
    [switch]$AssertSample
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$exportScript = Join-Path $PSScriptRoot "ExportSaveSampleFacts.ps1"
if (-not (Test-Path -LiteralPath $exportScript)) {
    throw "Export script was not found: $exportScript"
}

function Resolve-ProjectPath {
    param([string]$Path)

    $candidatePath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $projectRoot.Path $Path }
    return (Resolve-Path -LiteralPath $candidatePath).Path
}

function Read-JsonFile {
    param([string]$Path)

    $resolved = Resolve-ProjectPath $Path
    return Get-Content -Raw -LiteralPath $resolved | ConvertFrom-Json
}

function Convert-ToArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Get-JsonProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($null -eq $Value) {
        return $null
    }

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Set-JsonProperty {
    param(
        [object]$Value,
        [string]$Name,
        [object]$PropertyValue
    )

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Value | Add-Member -NotePropertyName $Name -NotePropertyValue $PropertyValue
    } else {
        $property.Value = $PropertyValue
    }
}

function Copy-JsonObject {
    param([object]$Value)

    return $Value | ConvertTo-Json -Depth 80 | ConvertFrom-Json
}

function New-StringSet {
    param([object[]]$Values)

    $set = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($value in $Values) {
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) {
            $set.Add([string]$value) | Out-Null
        }
    }

    return ,$set
}

function Add-UniqueStrings {
    param(
        [object[]]$Existing,
        [object[]]$Additional
    )

    $set = New-StringSet $Existing
    foreach ($value in $Additional) {
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) {
            $set.Add([string]$value) | Out-Null
        }
    }

    return @($set | Sort-Object)
}

function Test-SameStringSet {
    param(
        [object[]]$Left,
        [object[]]$Right
    )

    $leftSet = New-StringSet $Left
    $rightSet = New-StringSet $Right
    if ($leftSet.Count -ne $rightSet.Count) {
        return $false
    }

    foreach ($value in $leftSet) {
        if (-not $rightSet.Contains($value)) {
            return $false
        }
    }

    return $true
}

function Add-Issue {
    param(
        [System.Collections.Generic.List[object]]$Issues,
        [string]$Code,
        [string]$Message
    )

    $Issues.Add([pscustomobject]@{
        code = $Code
        message = $Message
    }) | Out-Null
}

function Get-ChallengeRunState {
    param([object]$StateDocument)

    $challengeRun = Get-JsonProperty $StateDocument "challengeRun"
    if ($null -eq $challengeRun) {
        $challengeRun = [pscustomobject]@{}
    }

    if ($null -eq (Get-JsonProperty $challengeRun "enabled")) {
        Set-JsonProperty $challengeRun "enabled" $true
    }
    if ($null -eq (Get-JsonProperty $challengeRun "currentStageIndex")) {
        Set-JsonProperty $challengeRun "currentStageIndex" 0
    }
    if ($null -eq (Get-JsonProperty $challengeRun "completedStageIds")) {
        Set-JsonProperty $challengeRun "completedStageIds" @()
    }
    if ($null -eq (Get-JsonProperty $challengeRun "usedHeroIds")) {
        Set-JsonProperty $challengeRun "usedHeroIds" @()
    }
    if ($null -eq (Get-JsonProperty $challengeRun "usedTrinketIds")) {
        Set-JsonProperty $challengeRun "usedTrinketIds" @()
    }
    if ($null -eq (Get-JsonProperty $challengeRun "stageAttempts")) {
        Set-JsonProperty $challengeRun "stageAttempts" @()
    }

    return $challengeRun
}

function Export-SampleFacts {
    $exportResultJson = & $exportScript `
        -SampleDirectory $SampleDirectory `
        -AssemblyPath $AssemblyPath `
        -GameDirectory $GameDirectory `
        -SessionPrefix "challenge_dry_run"
    $exportResult = $exportResultJson | ConvertFrom-Json
    $payload = Get-Content -Raw -LiteralPath $exportResult.output | ConvertFrom-Json
    return [pscustomobject]@{
        output = $exportResult.output
        facts = $payload.facts
        accessIssues = Convert-ToArray $payload.accessIssues
    }
}

function Build-HeroAvailability {
    param(
        [object]$Challenge,
        [object]$Facts,
        [object]$RunState,
        [object]$CurrentStage,
        [object]$LockedSelection
    )

    $usedHeroSet = New-StringSet (Convert-ToArray (Get-JsonProperty $RunState "usedHeroIds"))
    $lockedHeroSet = New-StringSet (Convert-ToArray (Get-JsonProperty $LockedSelection "heroIds"))
    $selectionLocked = $null -ne $LockedSelection
    $factsById = @{}
    foreach ($hero in Convert-ToArray $Facts.Heroes) {
        $factsById[[string]$hero.Id] = $hero
    }

    $rows = foreach ($hero in Convert-ToArray $Challenge.heroPool) {
        $id = [string]$hero.id
        $factHero = $factsById[$id]
        $reasons = [System.Collections.Generic.List[string]]::new()
        if ($usedHeroSet.Contains($id)) {
            $reasons.Add("used_by_completed_stage") | Out-Null
        }
        if ($selectionLocked -and -not $lockedHeroSet.Contains($id)) {
            $reasons.Add("current_stage_selection_locked") | Out-Null
        }
        if ($null -eq $factHero) {
            $reasons.Add("not_present_in_sample_facts") | Out-Null
        }

        $status = if ($selectionLocked -and $lockedHeroSet.Contains($id)) {
            "locked_for_retry"
        } elseif ($reasons.Count -eq 0) {
            "available"
        } else {
            "unavailable"
        }

        [pscustomobject]@{
            id = $id
            label = [string]$hero.label
            class = [string]$hero.class
            status = $status
            available = $status -eq "available"
            reasons = @($reasons)
            sourceName = if ($null -eq $factHero) { $null } else { $factHero.Name }
            sourceResolveXp = if ($null -eq $factHero) { $null } else { $factHero.ResolveXp }
        }
    }

    return @($rows)
}

function Build-TrinketAvailability {
    param(
        [object]$Challenge,
        [object]$Facts,
        [object]$RunState,
        [object]$LockedSelection
    )

    $usedTrinketSet = New-StringSet (Convert-ToArray (Get-JsonProperty $RunState "usedTrinketIds"))
    $lockedTrinketSet = New-StringSet (Convert-ToArray (Get-JsonProperty $LockedSelection "trinketIds"))
    $selectionLocked = $null -ne $LockedSelection
    $factsById = @{}
    foreach ($trinket in Convert-ToArray $Facts.Estate.TrinketItemsList) {
        if (-not [string]::IsNullOrWhiteSpace([string]$trinket.Id)) {
            $factsById[[string]$trinket.Id] = $trinket
        }
    }

    $rows = foreach ($trinket in Convert-ToArray $Challenge.trinketPool) {
        $id = [string]$trinket.id
        $factTrinket = $factsById[$id]
        $reasons = [System.Collections.Generic.List[string]]::new()
        if ($usedTrinketSet.Contains($id)) {
            $reasons.Add("used_by_completed_stage") | Out-Null
        }
        if ($selectionLocked -and -not $lockedTrinketSet.Contains($id)) {
            $reasons.Add("current_stage_selection_locked") | Out-Null
        }
        if ($null -eq $factTrinket) {
            $reasons.Add("not_present_in_sample_facts") | Out-Null
        }

        $status = if ($selectionLocked -and $lockedTrinketSet.Contains($id)) {
            "locked_for_retry"
        } elseif ($reasons.Count -eq 0) {
            "available"
        } else {
            "unavailable"
        }

        [pscustomobject]@{
            id = $id
            type = [string]$trinket.type
            status = $status
            available = $status -eq "available"
            reasons = @($reasons)
            sourceAmount = if ($null -eq $factTrinket) { $null } else { $factTrinket.Amount }
        }
    }

    return @($rows)
}

function Test-Selection {
    param(
        [object]$Challenge,
        [object]$RunState,
        [object]$CurrentStage,
        [object]$LockedSelection,
        [object[]]$HeroRows,
        [object[]]$TrinketRows,
        [string[]]$Heroes,
        [string[]]$Trinkets
    )

    $issues = [System.Collections.Generic.List[object]]::new()
    $partySize = [int]$Challenge.partySize
    $maxTrinkets = [int]$Challenge.maxTrinketsPerHero * $partySize
    $selectedHeroSet = New-StringSet $Heroes
    $selectedTrinketSet = New-StringSet $Trinkets

    if ($Heroes.Count -gt 0 -or $Outcome -ne "preview") {
        if ($selectedHeroSet.Count -ne $partySize) {
            Add-Issue $issues "invalid_party_size" "Expected $partySize unique hero ids, got $($selectedHeroSet.Count)."
        }
    }

    if ($selectedTrinketSet.Count -gt $maxTrinkets) {
        Add-Issue $issues "too_many_trinkets" "Expected at most $maxTrinkets unique trinkets, got $($selectedTrinketSet.Count)."
    }

    if ($null -ne $LockedSelection) {
        $lockedHeroes = Convert-ToArray (Get-JsonProperty $LockedSelection "heroIds")
        $lockedTrinkets = Convert-ToArray (Get-JsonProperty $LockedSelection "trinketIds")
        if ($Heroes.Count -gt 0 -and -not (Test-SameStringSet $Heroes $lockedHeroes)) {
            Add-Issue $issues "selection_locked_heroes_mismatch" "Retry policy requires the locked hero selection for the current stage."
        }
        if ($Trinkets.Count -gt 0 -and -not (Test-SameStringSet $Trinkets $lockedTrinkets)) {
            Add-Issue $issues "selection_locked_trinkets_mismatch" "Retry policy requires the locked trinket selection for the current stage."
        }
    }

    $heroById = @{}
    foreach ($row in $HeroRows) {
        $heroById[$row.id] = $row
    }
    foreach ($id in $selectedHeroSet) {
        if (-not $heroById.ContainsKey($id)) {
            Add-Issue $issues "hero_not_in_pool" "Hero '$id' is not in the challenge hero pool."
            continue
        }
        if ($heroById[$id].status -eq "unavailable") {
            Add-Issue $issues "hero_unavailable" "Hero '$id' is unavailable: $($heroById[$id].reasons -join ',')."
        }
    }

    $trinketById = @{}
    foreach ($row in $TrinketRows) {
        $trinketById[$row.id] = $row
    }
    foreach ($id in $selectedTrinketSet) {
        if (-not $trinketById.ContainsKey($id)) {
            Add-Issue $issues "trinket_not_in_pool" "Trinket '$id' is not in the challenge trinket pool."
            continue
        }
        if ($trinketById[$id].status -eq "unavailable") {
            Add-Issue $issues "trinket_unavailable" "Trinket '$id' is unavailable: $($trinketById[$id].reasons -join ',')."
        }
    }

    return @($issues)
}

function Add-Attempt {
    param(
        [object]$RunState,
        [string]$StageId,
        [string]$Result
    )

    $attempts = Convert-ToArray (Get-JsonProperty $RunState "stageAttempts")
    $attempts += [pscustomobject]@{
        stageId = $StageId
        result = $Result
        recordedAt = [DateTimeOffset]::Now
    }
    Set-JsonProperty $RunState "stageAttempts" @($attempts)
}

function Build-PostState {
    param(
        [object]$RunState,
        [object]$CurrentStage,
        [string[]]$Heroes,
        [string[]]$Trinkets
    )

    if ($Outcome -eq "preview") {
        return $null
    }

    $postState = Copy-JsonObject $RunState
    $stageId = [string]$CurrentStage.id
    $lockedSelection = Get-JsonProperty $postState "lockedStageSelection"
    $heroIds = if ($Heroes.Count -gt 0) { @($Heroes) } else { @(Convert-ToArray (Get-JsonProperty $lockedSelection "heroIds")) }
    $trinketIds = if ($Trinkets.Count -gt 0) { @($Trinkets) } else { @(Convert-ToArray (Get-JsonProperty $lockedSelection "trinketIds")) }

    if ($Outcome -eq "selection_confirmed" -or $Outcome -eq "stage_failed") {
        Set-JsonProperty $postState "lockedStageSelection" ([pscustomobject]@{
            stageId = $stageId
            heroIds = @($heroIds)
            trinketIds = @($trinketIds)
        })
        if ($Outcome -eq "stage_failed") {
            Add-Attempt $postState $stageId "failed"
        }
        return $postState
    }

    if ($Outcome -eq "stage_completed") {
        Set-JsonProperty $postState "usedHeroIds" (Add-UniqueStrings (Convert-ToArray (Get-JsonProperty $postState "usedHeroIds")) $heroIds)
        Set-JsonProperty $postState "usedTrinketIds" (Add-UniqueStrings (Convert-ToArray (Get-JsonProperty $postState "usedTrinketIds")) $trinketIds)
        Set-JsonProperty $postState "completedStageIds" (Add-UniqueStrings (Convert-ToArray (Get-JsonProperty $postState "completedStageIds")) @($stageId))
        Set-JsonProperty $postState "lockedStageSelection" $null
        Set-JsonProperty $postState "currentStageIndex" ([int](Get-JsonProperty $postState "currentStageIndex") + 1)
        Add-Attempt $postState $stageId "completed"
        return $postState
    }
}

$challenge = Read-JsonFile $ChallengePath
$stateDocument = Read-JsonFile $StatePath
$runState = Get-ChallengeRunState $stateDocument
$factsResult = Export-SampleFacts
$facts = $factsResult.facts

$stages = Convert-ToArray $challenge.stages
$currentStageIndex = [int](Get-JsonProperty $runState "currentStageIndex")
$currentStage = if ($currentStageIndex -lt $stages.Count) { $stages[$currentStageIndex] } else { $null }
$lockedSelection = Get-JsonProperty $runState "lockedStageSelection"
if ($null -ne $lockedSelection -and $null -ne $currentStage -and [string](Get-JsonProperty $lockedSelection "stageId") -ne [string]$currentStage.id) {
    $lockedSelection = $null
}

$heroRows = if ($null -eq $currentStage) { @() } else { Build-HeroAvailability $challenge $facts $runState $currentStage $lockedSelection }
$trinketRows = if ($null -eq $currentStage) { @() } else { Build-TrinketAvailability $challenge $facts $runState $lockedSelection }
$selectionIssues = if ($null -eq $currentStage) {
    @()
} else {
    Test-Selection $challenge $runState $currentStage $lockedSelection $heroRows $trinketRows $SelectedHeroIds $SelectedTrinketIds
}
$postState = if ($selectionIssues.Count -eq 0 -and $null -ne $currentStage) {
    Build-PostState $runState $currentStage $SelectedHeroIds $SelectedTrinketIds
} else {
    $null
}

$availableHeroes = @($heroRows | Where-Object { $_.available })
$unavailableHeroes = @($heroRows | Where-Object { -not $_.available })
$availableTrinkets = @($trinketRows | Where-Object { $_.available })
$unavailableTrinkets = @($trinketRows | Where-Object { -not $_.available })

$result = [pscustomobject]@{
    version = 1
    generatedAt = [DateTimeOffset]::Now
    challengeId = $challenge.id
    challengeName = $challenge.name
    sampleDirectory = (Resolve-ProjectPath $SampleDirectory)
    factsSnapshot = $factsResult.output
    outcome = $Outcome
    run = [pscustomobject]@{
        enabled = [bool](Get-JsonProperty $runState "enabled")
        currentStageIndex = $currentStageIndex
        stageCount = $stages.Count
        complete = $null -eq $currentStage
        currentStage = $currentStage
        retryPolicy = $challenge.retryPolicy
        selectionLocked = $null -ne $lockedSelection
        lockedSelection = $lockedSelection
    }
    poolSummary = [pscustomobject]@{
        partySize = [int]$challenge.partySize
        heroPoolCount = @($challenge.heroPool).Count
        availableHeroCount = $availableHeroes.Count
        unavailableHeroCount = $unavailableHeroes.Count
        trinketPoolCount = @($challenge.trinketPool).Count
        availableTrinketCount = $availableTrinkets.Count
        unavailableTrinketCount = $unavailableTrinkets.Count
        usedHeroCount = @(Convert-ToArray (Get-JsonProperty $runState "usedHeroIds")).Count
        usedTrinketCount = @(Convert-ToArray (Get-JsonProperty $runState "usedTrinketIds")).Count
    }
    availableHeroes = $availableHeroes
    unavailableHeroes = $unavailableHeroes
    availableTrinkets = $availableTrinkets
    unavailableTrinkets = $unavailableTrinkets
    selected = [pscustomobject]@{
        heroIds = @($SelectedHeroIds)
        trinketIds = @($SelectedTrinketIds)
    }
    validationIssues = @($selectionIssues)
    postState = $postState
}

if ($AssertSample) {
    $assertFailures = [System.Collections.Generic.List[string]]::new()
    if ($result.poolSummary.heroPoolCount -ne 12) { $assertFailures.Add("heroPoolCount expected 12") | Out-Null }
    if ($result.poolSummary.availableHeroCount -ne 12 -and -not $result.run.selectionLocked) { $assertFailures.Add("availableHeroCount expected 12 for unlocked sample") | Out-Null }
    if ($result.poolSummary.trinketPoolCount -ne 24) { $assertFailures.Add("trinketPoolCount expected 24") | Out-Null }
    if ($result.poolSummary.availableTrinketCount -ne 24 -and -not $result.run.selectionLocked) { $assertFailures.Add("availableTrinketCount expected 24 for unlocked sample") | Out-Null }
    if ($result.validationIssues.Count -ne 0) { $assertFailures.Add("validationIssues expected 0") | Out-Null }
    if ($assertFailures.Count -gt 0) {
        $assertFailures | ForEach-Object { Write-Error $_ }
        exit 1
    }
}

$json = $result | ConvertTo-Json -Depth 80
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $projectRoot.Path $OutputPath }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
    $json | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
    Write-Host $resolvedOutput
} else {
    $json
}
