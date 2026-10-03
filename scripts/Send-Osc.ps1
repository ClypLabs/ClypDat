param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Clip', 'Duration')]
    [string]$Action,

    [int]$Seconds,

    [string]$TargetHost = '127.0.0.1',

    [ValidateRange(1, 65535)]
    [int]$Port = 9001
)

$ErrorActionPreference = 'Stop'
if ($Action -eq 'Duration' -and $Seconds -notin @(30, 60, 120, 180, 240, 300)) {
    throw 'Duration requires -Seconds 30, 60, 120, 180, 240, or 300.'
}
if ($Action -eq 'Clip' -and $PSBoundParameters.ContainsKey('Seconds')) {
    throw '-Seconds changes saved replay length. Use -Action Duration, then send -Action Clip later.'
}

function ConvertTo-OscString {
    param([string]$Text)
    $encoded = [Text.Encoding]::ASCII.GetBytes($Text)
    $padded = New-Object byte[] (($encoded.Length + 4) -band -4)
    [Array]::Copy($encoded, $padded, $encoded.Length)
    return ,$padded
}

$stream = New-Object IO.MemoryStream
$client = $null
try {
    if ($Action -eq 'Clip') {
        $address = ConvertTo-OscString '/clypdat/clip'
        $tags = ConvertTo-OscString ','
    }
    else {
        $address = ConvertTo-OscString '/clypdat/replay-duration'
        $tags = ConvertTo-OscString ',i'
    }
    $stream.Write($address, 0, $address.Length)
    $stream.Write($tags, 0, $tags.Length)
    if ($Action -eq 'Duration') {
        $number = [BitConverter]::GetBytes([int]$Seconds)
        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($number) }
        $stream.Write($number, 0, $number.Length)
    }
    $destination = [Net.Dns]::GetHostAddresses($TargetHost) | Select-Object -First 1
    if ($null -eq $destination) { throw "Cannot resolve target host: $TargetHost" }
    $client = New-Object Net.Sockets.UdpClient -ArgumentList $destination.AddressFamily
    $endpoint = New-Object Net.IPEndPoint -ArgumentList $destination, $Port
    $packet = $stream.ToArray()
    $sent = $client.Send($packet, $packet.Length, $endpoint)
    Write-Output "Sent $sent OSC bytes to ${TargetHost}:$Port. Check ClypDat logs for the command result."
}
finally {
    if ($null -ne $client) { $client.Close() }
    $stream.Dispose()
}
