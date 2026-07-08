$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$source = Join-Path $repoRoot "native\src\Eve.ObsBridge\EveObsBridge.cpp"
$outDir = Join-Path $repoRoot "native\src\Eve.ObsBridge\bin\x64\Release"
$objDir = Join-Path $repoRoot "native\src\Eve.ObsBridge\obj\x64\Release"
$dll = Join-Path $outDir "Eve.ObsBridge.dll"
$obj = Join-Path $objDir "EveObsBridge.obj"

$cl = Get-ChildItem -LiteralPath "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter cl.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\bin\Hostx64\x64\cl.exe" } |
    Select-Object -First 1

if (-not $cl) {
    throw "MSVC x64 cl.exe not found."
}

$vcvars = Get-ChildItem -LiteralPath "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter vcvars64.bat -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $vcvars) {
    throw "vcvars64.bat not found."
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

$cmd = "`"$($vcvars.FullName)`" && cl.exe /nologo /std:c++20 /EHsc /O2 /LD `"$source`" /Fo`"$obj`" /Fe`"$dll`" user32.lib"
cmd.exe /c $cmd

if (-not (Test-Path $dll)) {
    throw "Bridge build failed: $dll"
}

$vendorBridge = Join-Path $repoRoot "native\vendor\obs-runtime\Eve.ObsBridge.dll"
if (Test-Path (Split-Path $vendorBridge)) {
    Copy-Item -LiteralPath $dll -Destination $vendorBridge -Force
}

Write-Host "Built $dll"
