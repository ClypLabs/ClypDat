[CmdletBinding()]
param([Parameter(Mandatory)][string] $AvaloniaRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$avaloniaRoot = (Resolve-Path -LiteralPath $AvaloniaRoot).Path
$pin = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'eng\AvaloniaPin.props') -Raw)
$avaloniaVersion = $pin.SelectSingleNode('//ClypDatAvaloniaStablePackageVersion').InnerText.Trim()
$commit = $pin.SelectSingleNode('//ClypDatAvaloniaStableCommit').InnerText.Trim()
$actualCommit = git -C $avaloniaRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualCommit.Trim() -cne $commit) { throw 'Avalonia checkout does not match the stable pin.' }
& (Join-Path $repoRoot 'dotnet.ps1') --info
if ($LASTEXITCODE -ne 0) { throw 'Could not bootstrap the pinned .NET SDK.' }
git -C $avaloniaRoot submodule update --init --recursive
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize pinned Avalonia submodules.' }
# Build only the pinned package feed. Never install, launch, or stop ClypDat.
$sdkVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$msbuild = Join-Path $repoRoot ".dotnet\sdk\$sdkVersion\MSBuild.exe"
$dotnetHost = Join-Path $repoRoot '.dotnet\dotnet.exe'
if (-not (Test-Path $msbuild)) { Write-Error "MSBuild was not found: $msbuild"; exit 1 }
if (-not (Test-Path $dotnetHost)) { Write-Error "dotnet host was not found: $dotnetHost"; exit 1 }
$packageFeed = Join-Path $avaloniaRoot 'artifacts\nuget'
$previousDotNetHostPath = $env:DOTNET_HOST_PATH
$previousDotNetRoot = $env:DOTNET_ROOT
$previousDotNetRootX64 = $env:DOTNET_ROOT_X64
$previousPath = $env:PATH
$env:DOTNET_HOST_PATH = $dotnetHost
$env:DOTNET_ROOT = Join-Path $repoRoot '.dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = "$($env:DOTNET_ROOT);$previousPath"
& $msbuild (Join-Path $avaloniaRoot 'build\ClypDat.Win32Packages.proj') /t:Pack `
  /p:Configuration=Release `
  /p:AvsSkipBuildingLegacyTargetFrameworks=True `
  /p:IncludeLinuxSkia=false `
  /p:IncludeWasmSkia=false `
  /p:ForcePackAvaloniaNative=true `
  "/p:ClypDatPackageVersion=$avaloniaVersion" "/p:ClypDatPackageOutput=$packageFeed" /nologo
$packageBuildExitCode = $LASTEXITCODE
$env:DOTNET_HOST_PATH = $previousDotNetHostPath
$env:DOTNET_ROOT = $previousDotNetRoot
$env:DOTNET_ROOT_X64 = $previousDotNetRootX64
$env:PATH = $previousPath
if ($packageBuildExitCode -ne 0) { Write-Error 'Could not build pinned Avalonia packages.'; exit 1 }
$avaloniaPackagePath = Join-Path $packageFeed "Avalonia.$avaloniaVersion.nupkg"
if (-not (Test-Path -LiteralPath $avaloniaPackagePath)) { Write-Error "Avalonia package feed was not created: $packageFeed"; exit 1 }

# The pinned Avalonia pack graph does not include build-task and
# generator outputs in Avalonia.nupkg. Add them, matching build.ps1's
# stable-package repair path, before the app restore consumes the feed.
$buildTasksProject = Join-Path $avaloniaRoot 'src\Avalonia.Build.Tasks\Avalonia.Build.Tasks.csproj'
$buildTasksOutput = Join-Path $avaloniaRoot 'src\Avalonia.Build.Tasks\bin\Release\netstandard2.0'
& $dotnetHost build $buildTasksProject -c Release -f netstandard2.0 /nologo
if ($LASTEXITCODE -ne 0) { Write-Error 'Could not build Avalonia build tasks.'; exit 1 }

$generatorProject = Join-Path $avaloniaRoot 'src\tools\Avalonia.Generators\Avalonia.Generators.csproj'
$generatorOutput = Join-Path $avaloniaRoot 'src\tools\Avalonia.Generators\bin\Release\netstandard2.0\Avalonia.Generators.dll'
& $dotnetHost build $generatorProject -c Release -f netstandard2.0 /nologo
if ($LASTEXITCODE -ne 0) { Write-Error 'Could not build Avalonia generators.'; exit 1 }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Add-PackageFile {
  param([string]$PackagePath, [string]$SourcePath, [string]$EntryName)
  if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) { throw "Package source file was not found: $SourcePath" }
  $archive = [System.IO.Compression.ZipFile]::Open($PackagePath, [System.IO.Compression.ZipArchiveMode]::Update)
  try {
    foreach ($existingEntry in @($archive.Entries | Where-Object { $_.FullName -eq $EntryName })) { $existingEntry.Delete() }
    $entry = $archive.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
    $input = [System.IO.File]::OpenRead($SourcePath)
    $output = $entry.Open()
    try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
  }
  finally { $archive.Dispose() }
}

$taskFiles = @('Avalonia.Build.Tasks.dll', 'Mono.Cecil.dll', 'Mono.Cecil.Mdb.dll', 'Mono.Cecil.Pdb.dll', 'Mono.Cecil.Rocks.dll', 'System.Numerics.Vectors.dll', 'System.Threading.dll', 'System.Threading.Thread.dll')
foreach ($taskName in $taskFiles) {
  $taskFile = Get-Item -LiteralPath (Join-Path $buildTasksOutput $taskName)
  Add-PackageFile $avaloniaPackagePath $taskFile.FullName "tools/netstandard2.0/$($taskFile.Name)"
}
Add-PackageFile $avaloniaPackagePath $generatorOutput 'analyzers/dotnet/cs/Avalonia.Generators.dll'

$archive = [System.IO.Compression.ZipFile]::OpenRead($avaloniaPackagePath)
try {
  if (@($archive.Entries | Where-Object { $_.FullName -eq 'tools/netstandard2.0/Avalonia.Build.Tasks.dll' }).Count -ne 1) { throw 'Avalonia package is missing Avalonia.Build.Tasks.dll.' }
  if (@($archive.Entries | Where-Object { $_.FullName -eq 'analyzers/dotnet/cs/Avalonia.Generators.dll' }).Count -ne 1) { throw 'Avalonia package is missing Avalonia.Generators.dll.' }
}
finally { $archive.Dispose() }
