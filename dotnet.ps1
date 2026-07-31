$ErrorActionPreference = 'Stop'
$dotnetArguments = $args
$dotnetExecutable = & (Join-Path $PSScriptRoot 'eng\Ensure-DotNet.ps1')

& $dotnetExecutable $dotnetArguments
exit $LASTEXITCODE
