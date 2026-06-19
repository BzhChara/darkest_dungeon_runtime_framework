param(
    [switch]$FailOnKnownIssues
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "TestSupport.psm1") -Force

$projectRoot = Get-DdrtProjectRoot
$coreRoots = @(
    (Join-Path $projectRoot "launcher"),
    (Join-Path $projectRoot "runtime")
)

function Get-ArchitectureScanFiles {
    foreach ($root in $coreRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }

        Get-ChildItem -LiteralPath $root -Recurse -File |
            Where-Object {
                $_.FullName -notmatch "\\bin\\" -and
                $_.FullName -notmatch "\\obj\\" -and
                $_.Extension -in @(".cs", ".cpp", ".h", ".hpp", ".c", ".json", ".ps1", ".props", ".targets", ".vcxproj", ".csproj")
            }
    }
}

function Add-Match {
    param(
        [System.Collections.Generic.List[object]]$Target,
        [string]$Severity,
        [string]$Rule,
        [string]$Path,
        [int]$Line,
        [string]$Text
    )

    $relative = [System.IO.Path]::GetRelativePath($projectRoot, $Path)
    $Target.Add([pscustomobject]@{
        severity = $Severity
        rule = $Rule
        path = $relative
        line = $Line
        text = $Text.Trim()
    }) | Out-Null
}

$blockingRules = @(
    [pscustomobject]@{
        Name = "core-specific-test-profile"
        Pattern = "\bprofile_3\b"
        Message = "Core code should not hardcode the user's live validation profile."
    },
    [pscustomobject]@{
        Name = "core-boss-gauntlet-branch"
        Pattern = "(?i)\bboss[_\. -]?gauntlet\b|\bbossGauntlet\b|\bBossGauntlet\b"
        Message = "Core code should not contain a boss-gauntlet-specific branch."
    },
    [pscustomobject]@{
        Name = "core-specific-plot-kill-quest"
        Pattern = "(?i)\bplot_kill_[a-z0-9_]+\b"
        Message = "Core code should not hardcode a concrete plot-kill quest id."
    },
    [pscustomobject]@{
        Name = "core-challenge-action-branch"
        Pattern = "(?i)\bchallenge\.[A-Za-z0-9_]+\b"
        Message = "Core code should not contain challenge.* executor branches; use generic primitives composed by plugins."
    }
)

$knownIssueRules = @()

$blocking = [System.Collections.Generic.List[object]]::new()
$knownIssues = [System.Collections.Generic.List[object]]::new()

foreach ($file in (Get-ArchitectureScanFiles)) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $lineNumber++

        foreach ($rule in $blockingRules) {
            if ($line -match $rule.Pattern) {
                Add-Match -Target $blocking -Severity "error" -Rule $rule.Name -Path $file.FullName -Line $lineNumber -Text $line
            }
        }

        foreach ($rule in $knownIssueRules) {
            if ($line -match $rule.Pattern) {
                Add-Match -Target $knownIssues -Severity "known-issue" -Rule $rule.Name -Path $file.FullName -Line $lineNumber -Text $line
            }
        }
    }
}

if ($blocking.Count -gt 0) {
    Write-Host "FAIL: architecture red flags found in launcher/runtime core code."
    foreach ($item in $blocking) {
        Write-Host ("{0}:{1}: {2}: {3}" -f $item.path, $item.line, $item.rule, $item.text)
    }

    throw "Architecture red flag check failed with $($blocking.Count) blocking issue(s)."
}

if ($knownIssues.Count -gt 0) {
    Write-Host "WARN: architecture known issues reported."
    foreach ($item in $knownIssues) {
        Write-Host ("{0}:{1}: {2}: {3}" -f $item.path, $item.line, $item.rule, $item.text)
    }

    if ($FailOnKnownIssues) {
        throw "Architecture red flag check failed because -FailOnKnownIssues was set."
    }
}

Write-Host "PASS: no blocking architecture red flags found."
