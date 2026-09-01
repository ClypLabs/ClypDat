[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LogPath,
    [int]$TargetFrameRate = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Metric([string]$line, [string]$name) {
    $match = [regex]::Match($line, "(?:^|, )$name=(?<value>-?[0-9]+(?:\.[0-9]+)?)")
    if (!$match.Success) { throw "Missing $name in diagnostic window." }
    [double]::Parse($match.Groups['value'].Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Median([double[]]$values) {
    $ordered = $values | Sort-Object
    $middle = [int]($ordered.Count / 2)
    if ($ordered.Count % 2) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

$allLines = Get-Content -LiteralPath $LogPath
$lines = $allLines | Where-Object { $_ -match 'Native capture throughput:' }
if (!$lines) { throw "No Native capture throughput windows found in $LogPath." }

$windows = foreach ($line in $lines) {
    [pscustomobject]@{
        Path = [regex]::Match($line, 'encodePath=(?<value>[^,]+)').Groups['value'].Value
        Input = Metric $line 'inputFps'
        Fresh = Metric $line 'freshFps'
        Output = Metric $line 'outputFps'
        Drops = Metric $line 'droppedFrames'
        Queue = Metric $line 'queueDepth'
        Capacity = Metric $line 'queueCapacity'
    }
}

$failures = [Collections.Generic.List[string]]::new()
if ($windows.Path | Where-Object { $_ -ne 'D3D11 zero-copy' }) { $failures.Add('encodePath is not D3D11 zero-copy') }
$sourceAtTarget = $windows | Where-Object { $_.Input -ge $TargetFrameRate }
if ($sourceAtTarget) {
    if ((Median @($sourceAtTarget.Output)) -lt ($TargetFrameRate * 0.99)) { $failures.Add('median output FPS below 99% target') }
    if ((Median @($sourceAtTarget.Fresh)) -lt ($TargetFrameRate * 0.99)) { $failures.Add('median fresh FPS below 99% target') }
}
if ($windows | Where-Object { $_.Output -lt ($TargetFrameRate * 0.95) }) { $failures.Add('output FPS below 95% target') }
if ($windows | Where-Object { $_.Drops -ne 0 }) { $failures.Add('encoder drops detected') }

$saturated = 0
foreach ($window in $windows) {
    $saturated = if ($window.Capacity -gt 0 -and $window.Queue * 4 -ge $window.Capacity * 3) { $saturated + 1 } else { 0 }
    if ($saturated -ge 3) { $failures.Add('three consecutive saturated queues'); break }
}
if ($allLines | Where-Object { $_ -match 'D3D11 encoder rebind failed; requested supervised worker restart|PipelineRecoveryAction=RestartWorker' }) {
    $failures.Add('worker restart requested')
}

$medianOutput = Median @($windows.Output)
$medianFresh = Median @($windows.Fresh)
if ($failures.Count) {
    "RED: medianOutputFps=$('{0:0.0}' -f $medianOutput), medianFreshFps=$('{0:0.0}' -f $medianFresh); $($failures -join '; ')."
    exit 1
}

"GREEN: medianOutputFps=$('{0:0.0}' -f $medianOutput), medianFreshFps=$('{0:0.0}' -f $medianFresh), windows=$($windows.Count)."
