param(
    [string]$ConfigPath = "config\challenge_save_event_bridge_observe_config.json",
    [string]$StateRoot = "",
    [switch]$NoBuild,
    [switch]$SkipPrepare,
    [switch]$PrepareOnly,
    [switch]$DryRun,
    [switch]$AllowExistingGameProcess
)

$ErrorActionPreference = "Stop"

$target = Join-Path $PSScriptRoot "StartLiveChallengeObserve.ps1"
& $target @PSBoundParameters
