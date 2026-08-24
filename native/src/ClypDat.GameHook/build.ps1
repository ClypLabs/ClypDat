[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Configuration
)

$ErrorActionPreference = 'Stop'
$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vsWhere)) { throw 'Visual Studio Build Tools locator was not found.' }

$installRoot = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($installRoot)) { throw 'MSVC x64/x86 build tools were not found.' }

$devCmd = Join-Path $installRoot 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $devCmd)) { throw "Visual Studio developer command prompt was not found at $devCmd." }

$projectRoot = Split-Path -Parent $PSCommandPath
$outputDirectory = Join-Path $projectRoot "bin\$Configuration"
$output = Join-Path $outputDirectory 'ClypDat.GameHook.dll'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$source = Join-Path $projectRoot 'ClypDat.GameHook.cpp'
$object = Join-Path $outputDirectory 'ClypDat.GameHook.obj'
$command = "`"$devCmd`" -arch=x64 -host_arch=x64 && cl.exe /nologo /std:c++20 /EHsc /O2 /LD /DUNICODE /D_UNICODE /Fo:`"$object`" `"$source`" /Fe:`"$output`" /link d3d11.lib dxgi.lib user32.lib"
& cmd.exe /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "ClypDat.GameHook build failed with exit code $LASTEXITCODE." }
