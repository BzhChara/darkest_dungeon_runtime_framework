function Read-ManagedActionProducerCatalog {
    param([string]$ProjectRoot)

    $path = Join-Path $ProjectRoot "logs\managed_action_producer_catalog.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Managed action producer catalog was not created: $path"
    }

    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Get-ManagedActionTestProducer {
    param(
        [string]$ProjectRoot,
        [string]$ActionType = "",
        [string]$Kind = "runtimeEventAction",
        [string]$PluginId = ""
    )

    $catalog = Read-ManagedActionProducerCatalog -ProjectRoot $ProjectRoot
    $matches = @($catalog.producers | Where-Object {
        ([string]$_.kind).Equals($Kind, [System.StringComparison]::OrdinalIgnoreCase) -and
        ([string]::IsNullOrWhiteSpace($ActionType) -or
            ([string]$_.actionType).Equals($ActionType, [System.StringComparison]::OrdinalIgnoreCase)) -and
        ([string]::IsNullOrWhiteSpace($PluginId) -or
            ([string]$_.pluginId).Equals($PluginId, [System.StringComparison]::OrdinalIgnoreCase))
    })
    if ($matches.Count -ne 1) {
        throw "Expected one managed action producer for kind=$Kind action=$ActionType plugin=$PluginId, found $($matches.Count)."
    }

    return $matches[0]
}

function Add-ManagedActionTestProducer {
    param(
        [System.Collections.IDictionary]$Artifact,
        [object]$Producer
    )

    $producerCopy = $Producer | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
    $Artifact["version"] = 2
    $Artifact["pluginId"] = [string]$Producer.pluginId
    $Artifact["sourceName"] = [string]$Producer.sourceName
    $Artifact["loadOrder"] = [int]$Producer.loadOrder
    $Artifact["ruleIndex"] = [int]$Producer.ruleIndex
    $Artifact["ruleId"] = [string]$Producer.ruleId
    $Artifact["actionIndex"] = [int]$Producer.actionIndex
    $Artifact["producer"] = $producerCopy
    $Artifact["action"] = [ordered]@{
        type = [string]$Producer.actionType
        capability = [string]$Producer.capability
        risk = [string]$Producer.risk
        required = [bool]$Producer.required
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$Producer.sourcePath)) {
        $Artifact["sourcePath"] = [string]$Producer.sourcePath
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$Producer.eventId)) {
        $Artifact["eventId"] = [string]$Producer.eventId
    }

    return $Artifact
}
