param(
    [Parameter(Position = 0, HelpMessage = 'Optional target: local, branch, or commit hash.')]
    [string]$Target,

    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$nativeRoot = Join-Path $repoRoot 'native'
$appProject = Join-Path $nativeRoot 'src\ClypDat.App\ClypDat.App.csproj'
$installDirectory = Join-Path $env:LOCALAPPDATA 'ClypDat.LocalBuild'
$programInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\ClypDat'
$dotnetExecutable = & (Join-Path $repoRoot 'eng\Ensure-DotNet.ps1')
$selfContainedVerifier = Join-Path $repoRoot 'eng\Test-SelfContainedPublish.ps1'
$globalJson = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
$localMsBuild = Join-Path (Split-Path $dotnetExecutable -Parent) "sdk\$sdkVersion\MSBuild.exe"
$systemMsBuild = Join-Path ${env:ProgramFiles} "dotnet\sdk\$sdkVersion\MSBuild.exe"
$msbuildExecutable = @($localMsBuild, $systemMsBuild) |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $msbuildExecutable) {
    $msbuildExecutable = (Get-Command msbuild.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
}
if (-not $msbuildExecutable) {
    $msbuildExecutable = $dotnetExecutable
}
$requiredAvaloniaPackageIds = @(
    'Avalonia', 'Avalonia.Base', 'Avalonia.Controls', 'Avalonia.Controls.Media', 'Avalonia.DesignerSupport',
    'Avalonia.Desktop', 'Avalonia.Dialogs', 'Avalonia.Fonts.Inter',
    'Avalonia.FreeDesktop', 'Avalonia.FreeDesktop.AtSpi', 'Avalonia.HarfBuzz',
    'Avalonia.Markup', 'Avalonia.Markup.Xaml', 'Avalonia.Metal', 'Avalonia.MicroCom',
    'Avalonia.Native', 'Avalonia.OpenGL', 'Avalonia.Remote.Protocol', 'Avalonia.Skia',
    'Avalonia.Themes.Fluent', 'Avalonia.Vulkan', 'Avalonia.Win32',
    'Avalonia.Win32.Automation', 'Avalonia.X11'
)

$avaloniaPackageInputPaths = @(
    'src', 'packages', 'native', 'external', 'build',
    'Directory.Build.props', 'Directory.Build.targets',
    'Directory.Packages.props', 'global.json', '.gitmodules'
)

$requiredAvaloniaAnalyzerEntries = @(
    'analyzers/dotnet/cs/Avalonia.Generators.dll'
)

# Windows PowerShell 5.1 and PowerShell 7+ split these differently: 5.1 needs
# System.IO.Compression for ZipArchiveMode/CompressionLevel and
# System.IO.Compression.FileSystem for ZipFile, while 7+ has both in its default
# assembly set and returns "assembly already loaded" for either. Loading both,
# tolerating failure, is what makes the Avalonia repack path work on both hosts -
# without it 5.1 dies with "Unable to find type [IO.Compression.ZipArchiveMode]"
# the moment a fork rebuild is needed.
foreach ($compressionAssembly in @('System.IO.Compression', 'System.IO.Compression.FileSystem')) {
    try { Add-Type -AssemblyName $compressionAssembly -ErrorAction Stop } catch { }
}

if (-not ('System.IO.Compression.ZipFile' -as [type]) -or
    -not ('System.IO.Compression.ZipArchiveMode' -as [type])) {
    throw "This host cannot load System.IO.Compression. Run publish.ps1 under PowerShell 7 (pwsh) or Windows PowerShell 5.1."
}

function Invoke-Git {
    param(
        [Parameter(Mandatory, ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

function Get-GitPosition {
    $branch = & git symbolic-ref --quiet --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $branch) {
        return [PSCustomObject]@{
            Branch = $branch.Trim()
            Commit = $null
        }
    }

    $commit = (Invoke-Git rev-parse --verify HEAD | Select-Object -First 1).Trim()
    return [PSCustomObject]@{
        Branch = $null
        Commit = $commit
    }
}

function Assert-CleanWorktree {
    $changes = @(Invoke-Git status --porcelain=v1 --untracked-files=all)
    if ($changes.Count -gt 0) {
        throw 'Ref publishing requires a clean worktree. Commit, stash, or remove changes, then rerun. Use ./publish.ps1 local to publish current worktree without Git switching.'
    }
}

function Restore-GitPosition {
    param([Parameter(Mandatory)]$Position)

    if ($Position.Branch) {
        Invoke-Git switch $Position.Branch | Out-Null
    }
    else {
        Invoke-Git switch --detach $Position.Commit | Out-Null
    }
}

function Remove-AvaloniaWorktree {
    param(
        [Parameter(Mandatory)]
        [string]$AvaloniaRoot,

        [Parameter(Mandatory)]
        [string]$WorktreeRoot
    )

    $fullWorktreeRoot = [IO.Path]::GetFullPath($WorktreeRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $worktreeName = [IO.Path]::GetFileName($fullWorktreeRoot)
    if (-not $fullWorktreeRoot.StartsWith("$tempRoot\", [StringComparison]::OrdinalIgnoreCase) -or
        -not $worktreeName.StartsWith('ca-', [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "Refusing to remove unexpected Avalonia worktree path: $fullWorktreeRoot"
        return
    }

    & git -C $AvaloniaRoot worktree remove --force $fullWorktreeRoot 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath $fullWorktreeRoot)) {
        return
    }

    & git -C $AvaloniaRoot worktree prune 2>$null | Out-Null
    if (Test-Path -LiteralPath $fullWorktreeRoot) {
        try {
            [IO.Directory]::Delete("\\?\$fullWorktreeRoot", $true)
        }
        catch {
            Write-Warning "Could not remove temporary Avalonia worktree ${fullWorktreeRoot}: $($_.Exception.Message)"
        }
    }
}

function Read-AvaloniaNuspec {
    param([Parameter(Mandatory)][string]$PackagePath)

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $nuspec = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' }) | Select-Object -First 1
        if ($null -eq $nuspec) {
            throw "Package has no nuspec: $PackagePath"
        }

        $reader = [IO.StreamReader]::new($nuspec.Open())
        try {
            return ([xml]$reader.ReadToEnd()).package.metadata
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Add-AvaloniaBuildTaskFiles {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$BuildOutput
    )

    $taskFiles = @(Get-ChildItem -LiteralPath $BuildOutput -File)
    if ($taskFiles.Count -eq 0) {
        throw "Avalonia build task output is empty: $BuildOutput"
    }

    $archive = [IO.Compression.ZipFile]::Open($PackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($taskFile in $taskFiles) {
            $entryName = "tools/netstandard2.0/$($taskFile.Name)"
            foreach ($existingEntry in @($archive.Entries | Where-Object { $_.FullName -eq $entryName })) {
                $existingEntry.Delete()
            }

            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $input = [IO.File]::OpenRead($taskFile.FullName)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Add-AvaloniaPackageFile {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$EntryName
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Avalonia package source file was not found: $SourcePath"
    }

    $archive = [IO.Compression.ZipFile]::Open($PackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($existingEntry in @($archive.Entries | Where-Object { $_.FullName -eq $EntryName })) {
            $existingEntry.Delete()
        }

        $entry = $archive.CreateEntry($EntryName, [IO.Compression.CompressionLevel]::Optimal)
        $input = [IO.File]::OpenRead($SourcePath)
        $output = $entry.Open()
        try {
            $input.CopyTo($output)
        }
        finally {
            $output.Dispose()
            $input.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-NuGetGlobalPackagesPath {
    $lines = @(& $dotnetExecutable nuget locals global-packages --list)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not determine the NuGet global packages path."
    }

    $line = $lines | Where-Object { $_ -match '^\s*global-packages:\s*(.+?)\s*$' } | Select-Object -First 1
    if (-not $line) {
        throw 'The dotnet CLI did not report a global packages path.'
    }

    $null = $line -match '^\s*global-packages:\s*(.+?)\s*$'
    return [IO.Path]::GetFullPath($Matches[1].Trim())
}

function Remove-IncompleteAvaloniaPackageCache {
    param([Parameter(Mandatory)][string]$PackageVersion)

    $globalPackagesPath = Get-NuGetGlobalPackagesPath
    $avaloniaPackagePath = Join-Path $globalPackagesPath (Join-Path 'avalonia' $PackageVersion.ToLowerInvariant())
    $requiredFiles = @('tools\netstandard2.0\Avalonia.Build.Tasks.dll') +
        @($requiredAvaloniaAnalyzerEntries | ForEach-Object { $_ -replace '/', '\\' })
    $missingRequiredFile = $requiredFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $avaloniaPackagePath $_) -PathType Leaf)
    } | Select-Object -First 1
    if ((Test-Path -LiteralPath $avaloniaPackagePath) -and $missingRequiredFile) {
        Write-Host "Removing incomplete cached Avalonia package: $avaloniaPackagePath"
        Remove-Item -LiteralPath $avaloniaPackagePath -Recurse -Force
    }
}

function Stop-InstalledClypDatProcesses {
    param([Parameter(Mandatory)][string]$InstallDirectory)

    $installPathPrefix = "$([IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\'))\"
    $processIds = [Collections.Generic.HashSet[int]]::new()

    # ExecutablePath can be unavailable for a process owned by another session.
    # Include the application name so an old local build cannot keep its files open.
    foreach ($process in @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installPathPrefix, [StringComparison]::OrdinalIgnoreCase)
    })) {
        [void]$processIds.Add([int]$process.ProcessId)
    }
    foreach ($process in @(Get-Process -Name 'ClypDat' -ErrorAction SilentlyContinue)) {
        [void]$processIds.Add([int]$process.Id)
    }

    foreach ($processId in $processIds) {
        Write-Host "Stopping ClypDat process (PID $processId)"
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }

    if ($processIds.Count -eq 0) {
        return
    }

    $deadline = (Get-Date).AddSeconds(10)
    do {
        $remaining = @($processIds | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    throw "ClypDat process(es) did not exit: $($remaining -join ', ')."
}

function Test-DirectoryCreateAccess {
    param([Parameter(Mandatory)][string]$Directory)

    $probeDirectory = Join-Path $Directory ('.clypdat-write-test-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $probeDirectory -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
    finally {
        if (Test-Path -LiteralPath $probeDirectory) {
            Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-AvaloniaBuild {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $buildOutput = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE

    foreach ($outputChunk in $buildOutput) {
        foreach ($lineText in ("$outputChunk" -split '\r?\n')) {
            if ($lineText -match '(?i)(:\s*(?:fatal\s+)?error\s+[A-Z]{2,}\d+|^\s*(?:fatal\s+)?error\s+[A-Z]{2,}\d+|^\s*unhandled exception\b)') {
                Write-Host $lineText
            }
        }
    }

    $errorLines = @($buildOutput | ForEach-Object { "$_" -split '\r?\n' } | Where-Object {
        $_ -match '(?i)(:\s*(?:fatal\s+)?error\s+[A-Z]{2,}\d+|^\s*(?:fatal\s+)?error\s+[A-Z]{2,}\d+|^\s*unhandled exception\b)'
    })
    if ($exitCode -ne 0 -and $errorLines.Count -eq 0) {
        Write-Host 'Avalonia build failed without an error-formatted output line.'
    }

    return $exitCode
}

function Install-ClypDatDirectory {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )

    $destinationParent = Split-Path -Parent $DestinationDirectory
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    if (-not (Test-DirectoryCreateAccess -Directory $destinationParent)) {
        throw "Cannot create local publish files under $destinationParent."
    }

    $stagedDirectory = Join-Path $destinationParent ('.ClypDat.staged-' + [Guid]::NewGuid().ToString('N'))
    $previousDirectory = $null
    try {
        Copy-Item -LiteralPath $SourceDirectory -Destination $stagedDirectory -Recurse -Force

        if (Test-Path -LiteralPath $DestinationDirectory) {
            $previousDirectory = Join-Path $destinationParent ('.ClypDat.previous-' + [Guid]::NewGuid().ToString('N'))
            Move-Item -LiteralPath $DestinationDirectory -Destination $previousDirectory
        }

        try {
            Move-Item -LiteralPath $stagedDirectory -Destination $DestinationDirectory
        }
        catch {
            if ($previousDirectory -and (Test-Path -LiteralPath $previousDirectory) -and -not (Test-Path -LiteralPath $DestinationDirectory)) {
                Move-Item -LiteralPath $previousDirectory -Destination $DestinationDirectory
            }
            throw
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagedDirectory) {
            Remove-Item -LiteralPath $stagedDirectory -Recurse -Force
        }
        if ($previousDirectory -and (Test-Path -LiteralPath $previousDirectory)) {
            Remove-Item -LiteralPath $previousDirectory -Recurse -Force
        }
    }
}

function Test-AvaloniaPackageSet {
    param(
        [Parameter(Mandatory)][string]$PackageOutput,
        [Parameter(Mandatory)][string]$PackageVersion,
        [string]$ExpectedCommit,
        [switch]$RequireStamp,
        [switch]$SkipAnalyzerCheck,
        [switch]$ShowFailure,
        [string[]]$RequiredPackageIds = $requiredAvaloniaPackageIds
    )

    function Invalid-PackageSet {
        param([Parameter(Mandatory)][string]$Reason)
        if ($ShowFailure) {
            Write-Host "Avalonia package validation failed: $Reason"
        }
        return $false
    }

    try {
        $packageFiles = @(Get-ChildItem -LiteralPath $PackageOutput -Filter '*.nupkg' -File)
        $expectedNames = @($RequiredPackageIds | ForEach-Object { "$_.$PackageVersion.nupkg" } | Sort-Object)
        $actualNames = @($packageFiles.Name | Sort-Object)
        if (($actualNames -join '|') -ne ($expectedNames -join '|')) {
            $missing = @($expectedNames | Where-Object { $_ -notin $actualNames }) -join ', '
            $unexpected = @($actualNames | Where-Object { $_ -notin $expectedNames }) -join ', '
            return Invalid-PackageSet "package names differ; missing: [$missing]; unexpected: [$unexpected]"
        }

        $avaloniaPackagePath = Join-Path $PackageOutput "Avalonia.$PackageVersion.nupkg"
        $archive = [IO.Compression.ZipFile]::OpenRead($avaloniaPackagePath)
        try {
            $buildTaskEntry = @($archive.Entries | Where-Object { $_.FullName -eq 'tools/netstandard2.0/Avalonia.Build.Tasks.dll' })
            if ($buildTaskEntry.Count -ne 1) {
                return Invalid-PackageSet 'Avalonia package has no single tools/netstandard2.0/Avalonia.Build.Tasks.dll entry'
            }

            if (-not $SkipAnalyzerCheck) {
                foreach ($requiredAnalyzerEntry in $requiredAvaloniaAnalyzerEntries) {
                    if (@($archive.Entries | Where-Object { $_.FullName -eq $requiredAnalyzerEntry }).Count -ne 1) {
                        return Invalid-PackageSet "Avalonia package has no single $requiredAnalyzerEntry entry"
                    }
                }
            }
        }
        finally {
            $archive.Dispose()
        }

        $packageIds = @{}
        foreach ($packageFile in $packageFiles) {
            $metadata = Read-AvaloniaNuspec -PackagePath $packageFile.FullName
            if ($metadata.version -ne $PackageVersion -or $metadata.id -notin $RequiredPackageIds) {
                return Invalid-PackageSet "$($packageFile.Name) has id '$($metadata.id)' and version '$($metadata.version)'"
            }

            $packageIds[$metadata.id] = $true
            foreach ($dependency in (@($metadata.dependencies.group.dependency) + @($metadata.dependencies.dependency))) {
                if ($dependency.id -like 'Avalonia*' -and $dependency.version -eq $PackageVersion) {
                    $dependencyPath = Join-Path $PackageOutput "$($dependency.id).$PackageVersion.nupkg"
                    if (-not (Test-Path -LiteralPath $dependencyPath -PathType Leaf)) {
                        return Invalid-PackageSet "$($metadata.id) requires missing package $($dependency.id).$PackageVersion.nupkg"
                    }
                }
            }
        }

        foreach ($packageId in $RequiredPackageIds) {
            if (-not $packageIds.ContainsKey($packageId)) {
                return Invalid-PackageSet "package metadata is missing required id '$packageId'"
            }
        }

        if ($RequireStamp) {
            $stampPath = Join-Path $PackageOutput 'clypdat-package-stamp.json'
            if (-not (Test-Path -LiteralPath $stampPath -PathType Leaf)) {
                return Invalid-PackageSet 'package stamp is missing'
            }

            $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
            if ($stamp.schema -ne 1 -or $stamp.commit -ne $ExpectedCommit -or $stamp.packageVersion -ne $PackageVersion) {
                return Invalid-PackageSet 'package stamp schema, commit, or version does not match'
            }

            $stampPackages = @($stamp.packages | Sort-Object)
            $expectedIds = @($RequiredPackageIds | Sort-Object)
            if (($stampPackages -join '|') -ne ($expectedIds -join '|')) {
                return Invalid-PackageSet 'package stamp id list does not match expected desktop package list'
            }
        }

        return $true
    }
    catch {
        if ($ShowFailure) {
            Write-Host "Avalonia package validation failed: $($_.Exception.Message)"
        }
        return $false
    }
}

function Get-AvaloniaPackageStampCommit {
    param([Parameter(Mandatory)][string]$PackageOutput)

    $stampPath = Join-Path $PackageOutput 'clypdat-package-stamp.json'
    if (-not (Test-Path -LiteralPath $stampPath -PathType Leaf)) {
        return $null
    }

    try {
        $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
        if ($stamp.schema -ne 1 -or [string]::IsNullOrWhiteSpace($stamp.commit)) {
            return $null
        }
        return $stamp.commit.Trim()
    }
    catch {
        return $null
    }
}

function Test-AvaloniaPackageInputsChanged {
    param(
        [Parameter(Mandatory)][string]$AvaloniaRoot,
        [Parameter(Mandatory)][string]$BaseCommit
    )

    $null = & git -C $AvaloniaRoot diff --quiet "$BaseCommit" HEAD -- $avaloniaPackageInputPaths 2>$null
    $committedExitCode = $LASTEXITCODE
    if ($committedExitCode -gt 1) {
        return $true
    }

    $null = & git -C $AvaloniaRoot diff --quiet HEAD -- $avaloniaPackageInputPaths 2>$null
    $workingExitCode = $LASTEXITCODE
    if ($workingExitCode -gt 1) {
        return $true
    }

    $workingChanges = @(& git -C $AvaloniaRoot status --porcelain=v1 --untracked-files=all -- $avaloniaPackageInputPaths 2>$null)
    return $committedExitCode -ne 0 -or $workingExitCode -ne 0 -or $workingChanges.Count -gt 0
}

function Repair-CachedAvaloniaAnalyzer {
    param(
        [Parameter(Mandatory)][string]$AvaloniaRoot,
        [Parameter(Mandatory)][string]$PackageOutput,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$ExpectedCommit,
        [Parameter(Mandatory)][string[]]$RequiredPackageIds
    )

    # A pre-generator package set is otherwise complete. Repair it in place
    # rather than repacking every Avalonia project (which also invokes Bun).
    if (-not (Test-AvaloniaPackageSet `
            -PackageOutput $PackageOutput `
            -PackageVersion $PackageVersion `
            -ExpectedCommit $ExpectedCommit `
            -RequireStamp `
            -RequiredPackageIds $RequiredPackageIds `
            -SkipAnalyzerCheck)) {
        return $false
    }

    $generatorProject = Join-Path $AvaloniaRoot 'src\tools\Avalonia.Generators\Avalonia.Generators.csproj'
    $generatorOutput = Join-Path $AvaloniaRoot 'src\tools\Avalonia.Generators\bin\Release\netstandard2.0\Avalonia.Generators.dll'
    $generatorBuildExitCode = Invoke-AvaloniaBuild -FilePath $dotnetExecutable -Arguments @(
        'build', $generatorProject, '-c', 'Release', '-f', 'netstandard2.0', '--no-restore', '/nologo'
    )
    if ($generatorBuildExitCode -ne 0) {
        throw "Avalonia generator compilation failed with exit code $generatorBuildExitCode while repairing the cached package."
    }

    Add-AvaloniaPackageFile `
        -PackagePath (Join-Path $PackageOutput "Avalonia.$PackageVersion.nupkg") `
        -SourcePath $generatorOutput `
        -EntryName 'analyzers/dotnet/cs/Avalonia.Generators.dll'

    return Test-AvaloniaPackageSet `
        -PackageOutput $PackageOutput `
        -PackageVersion $PackageVersion `
        -ExpectedCommit $ExpectedCommit `
        -RequireStamp `
        -RequiredPackageIds $RequiredPackageIds
}

function Ensure-StableAvaloniaPackages {
    param([switch]$UseLocalAvalonia)

    $avaloniaRoot = Join-Path (Split-Path $repoRoot -Parent) 'clypdat-avalonia'
    $packageOutput = Join-Path $avaloniaRoot 'artifacts\nuget'
    $packageStaging = Join-Path $avaloniaRoot 'artifacts\clypdat-package-staging'
    $pinFile = Join-Path $repoRoot 'eng\AvaloniaPin.props'

    if (-not (Test-Path -LiteralPath $pinFile -PathType Leaf)) {
        throw "Avalonia pin file was not found: $pinFile"
    }

    $pin = [xml](Get-Content -LiteralPath $pinFile -Raw)
    $stableCommit = $pin.SelectSingleNode('//ClypDatAvaloniaStableCommit').InnerText
    $stableVersion = $pin.SelectSingleNode('//ClypDatAvaloniaStablePackageVersion').InnerText
    if ([string]::IsNullOrWhiteSpace($stableCommit)) {
        throw 'The stable Avalonia commit is missing from eng/AvaloniaPin.props.'
    }
    if ([string]::IsNullOrWhiteSpace($stableVersion)) {
        throw 'The stable Avalonia package version is missing from eng/AvaloniaPin.props.'
    }

    # Pinned target omits Controls.Media; local fork may include it during
    # package work. Validate each mode against its own exact package surface.
    $requiredPackageIds = if ($UseLocalAvalonia) {
        $requiredAvaloniaPackageIds
    }
    else {
        @($requiredAvaloniaPackageIds | Where-Object { $_ -ne 'Avalonia.Controls.Media' })
    }

    if (-not (Test-Path -LiteralPath (Join-Path $avaloniaRoot '.git'))) {
        throw "The sibling Avalonia fork was not found at: $avaloniaRoot"
    }

    $buildCommit = $stableCommit
    $expectedPackageCommit = $stableCommit
    if ($UseLocalAvalonia) {
        $localCommitLines = @(& git -C $avaloniaRoot rev-parse --verify HEAD 2>$null)
        $localCommitExitCode = $LASTEXITCODE
        $localCommit = $localCommitLines | Select-Object -First 1
        if ($localCommitExitCode -ne 0 -or -not $localCommit) {
            throw "Could not resolve the local Avalonia fork commit at: $avaloniaRoot"
        }
        $localCommit = $localCommit.Trim()

        $stampCommit = Get-AvaloniaPackageStampCommit -PackageOutput $packageOutput
        $uiInputsChanged = -not $stampCommit -or
            (Test-AvaloniaPackageInputsChanged -AvaloniaRoot $avaloniaRoot -BaseCommit $stampCommit)
        if (-not $uiInputsChanged) {
            # NuGet extracts a package version once and then trusts that folder.
            # Repairing the source .nupkg is not enough when an earlier extract
            # is missing the generator, so evict only that incomplete extract
            # before deciding this is a usable cache hit.
            Remove-IncompleteAvaloniaPackageCache -PackageVersion $stableVersion
            if (Test-AvaloniaPackageSet -PackageOutput $packageOutput -PackageVersion $stableVersion -ExpectedCommit $stampCommit -RequireStamp -RequiredPackageIds $requiredPackageIds) {
                Write-Host "Using cached Avalonia packages; UI/package inputs unchanged since $stampCommit."
                return
            }

            if (Repair-CachedAvaloniaAnalyzer -AvaloniaRoot $avaloniaRoot -PackageOutput $packageOutput -PackageVersion $stableVersion -ExpectedCommit $stampCommit -RequiredPackageIds $requiredPackageIds) {
                Write-Host 'Repaired the cached Avalonia generator; UI/package inputs were unchanged.'
                Remove-IncompleteAvaloniaPackageCache -PackageVersion $stableVersion
                return
            }

            throw 'The cached Avalonia package set is incomplete, but UI/package inputs are unchanged. Refusing to rebuild the full package set for an application-only publish.'
        }

        $buildCommit = $localCommit
        $expectedPackageCommit = $localCommit
    }
    else {
        Remove-IncompleteAvaloniaPackageCache -PackageVersion $stableVersion
        if (Test-AvaloniaPackageSet -PackageOutput $packageOutput -PackageVersion $stableVersion -ExpectedCommit $stableCommit -RequireStamp -RequiredPackageIds $requiredPackageIds) {
            Write-Host "Using stamped Avalonia package set for commit $stableCommit."
            return
        }
    }

    if ($UseLocalAvalonia) {
        Write-Host "Avalonia UI/package inputs changed; rebuilding package set from commit $buildCommit."
    }
    else {
        Write-Host "Stable Avalonia package stamp is missing or stale; fetching pinned commit $stableCommit and building version $stableVersion."
    }
    $worktreeRoot = Join-Path ([IO.Path]::GetTempPath()) ('ca-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    $worktreeAdded = $false
    try {
        $resolvedCommit = & git -C $avaloniaRoot rev-parse --verify "$buildCommit^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            if ($UseLocalAvalonia) {
                throw "Local Avalonia commit $buildCommit could not be resolved."
            }

            & git -C $avaloniaRoot fetch --no-tags origin main
            if ($LASTEXITCODE -ne 0) {
                throw "Could not fetch the pinned Avalonia commit $stableCommit."
            }
        }

        $resolvedCommit = & git -C $avaloniaRoot rev-parse --verify "$buildCommit^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $resolvedCommit) {
            throw "Avalonia commit $buildCommit could not be resolved. Verify the commit exists in the fork."
        }
        $resolvedCommit = ($resolvedCommit | Select-Object -First 1).Trim()

        $localSourceChanges = $UseLocalAvalonia -and
            @(& git -C $avaloniaRoot status --porcelain=v1 --untracked-files=all -- $avaloniaPackageInputPaths 2>$null).Count -gt 0
        if ($localSourceChanges) {
            $worktreeRoot = $avaloniaRoot
            Write-Host 'Building Avalonia from the current local worktree.'
        }
        else {
            & git -C $avaloniaRoot worktree add --detach $worktreeRoot $resolvedCommit
            if ($LASTEXITCODE -ne 0) {
                throw "Could not create a temporary Avalonia worktree at $worktreeRoot for commit $resolvedCommit."
            }
            $worktreeAdded = $true

            & git -C $worktreeRoot submodule update --init --recursive
            if ($LASTEXITCODE -ne 0) {
                throw "Could not initialize Avalonia submodules in the temporary worktree."
            }
        }

        $packageProject = Join-Path $worktreeRoot 'build\ClypDat.Win32Packages.proj'
        if (-not (Test-Path -LiteralPath $packageProject -PathType Leaf)) {
            throw "Avalonia commit $buildCommit does not contain the ClypDat package target."
        }

        $stagingFullPath = [IO.Path]::GetFullPath($packageStaging)
        $avaloniaFullPath = [IO.Path]::GetFullPath($avaloniaRoot).TrimEnd('\') + '\'
        if (-not $stagingFullPath.StartsWith($avaloniaFullPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to use unexpected Avalonia package staging path: $stagingFullPath"
        }
        if (Test-Path -LiteralPath $stagingFullPath) {
            Remove-Item -LiteralPath $stagingFullPath -Recurse -Force
        }
        New-Item -ItemType Directory -Path $stagingFullPath -Force | Out-Null

        Push-Location $worktreeRoot
        try {
            if ($msbuildExecutable -ne $dotnetExecutable) {
                Write-Host "Using MSBuild executable $msbuildExecutable for Avalonia package tasks."
            }
            $previousDotNetHostPath = $env:DOTNET_HOST_PATH
            $env:DOTNET_HOST_PATH = $dotnetExecutable
            try {
                $packageBuildExitCode = Invoke-AvaloniaBuild -FilePath $msbuildExecutable -Arguments @(
                    $packageProject, '/t:Pack', "/p:ClypDatPackageVersion=$stableVersion",
                    "/p:ClypDatPackageOutput=$stagingFullPath", '/nologo'
                )
            }
            finally {
                $env:DOTNET_HOST_PATH = $previousDotNetHostPath
            }
            if ($packageBuildExitCode -ne 0) {
                throw "Avalonia package build failed with exit code $packageBuildExitCode."
            }
        }
        finally {
            Pop-Location
        }

        $buildTasksProject = Join-Path $worktreeRoot 'src\Avalonia.Build.Tasks\Avalonia.Build.Tasks.csproj'
        $buildTasksOutput = Join-Path $worktreeRoot 'src\Avalonia.Build.Tasks\bin\Release\netstandard2.0'
        $buildTasksExitCode = Invoke-AvaloniaBuild -FilePath $dotnetExecutable -Arguments @(
            'build', $buildTasksProject, '-c', 'Release', '-f', 'netstandard2.0', '--no-restore', '/nologo'
        )
        if ($buildTasksExitCode -ne 0) {
            throw "Avalonia build task compilation failed with exit code $buildTasksExitCode."
        }

        Add-AvaloniaBuildTaskFiles -PackagePath (Join-Path $stagingFullPath "Avalonia.$stableVersion.nupkg") -BuildOutput $buildTasksOutput

        $generatorProject = Join-Path $worktreeRoot 'src\tools\Avalonia.Generators\Avalonia.Generators.csproj'
        $generatorOutput = Join-Path $worktreeRoot 'src\tools\Avalonia.Generators\bin\Release\netstandard2.0\Avalonia.Generators.dll'
        $generatorBuildExitCode = Invoke-AvaloniaBuild -FilePath $dotnetExecutable -Arguments @(
            'build', $generatorProject, '-c', 'Release', '-f', 'netstandard2.0', '--no-restore', '/nologo'
        )
        if ($generatorBuildExitCode -ne 0) {
            throw "Avalonia generator compilation failed with exit code $generatorBuildExitCode."
        }

        Add-AvaloniaPackageFile `
            -PackagePath (Join-Path $stagingFullPath "Avalonia.$stableVersion.nupkg") `
            -SourcePath $generatorOutput `
            -EntryName 'analyzers/dotnet/cs/Avalonia.Generators.dll'

        if (-not (Test-AvaloniaPackageSet -PackageOutput $stagingFullPath -PackageVersion $stableVersion -ShowFailure -RequiredPackageIds $requiredPackageIds)) {
            throw 'Avalonia package build did not produce the exact desktop package closure.'
        }

        $outputFullPath = [IO.Path]::GetFullPath($packageOutput)
        if (-not $outputFullPath.StartsWith($avaloniaFullPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace unexpected Avalonia package output path: $outputFullPath"
        }
        if (Test-Path -LiteralPath $outputFullPath) {
            Remove-Item -LiteralPath $outputFullPath -Recurse -Force
        }
        Move-Item -LiteralPath $stagingFullPath -Destination $outputFullPath

        [ordered]@{
            schema = 1
            commit = $resolvedCommit
            packageVersion = $stableVersion
            packages = @($requiredPackageIds | Sort-Object)
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputFullPath 'clypdat-package-stamp.json') -Encoding utf8
    }
    finally {
        if ($worktreeAdded) {
            Remove-AvaloniaWorktree -AvaloniaRoot $avaloniaRoot -WorktreeRoot $worktreeRoot
        }
    }

    Remove-IncompleteAvaloniaPackageCache -PackageVersion $stableVersion

    if (-not (Test-AvaloniaPackageSet -PackageOutput $packageOutput -PackageVersion $stableVersion -ExpectedCommit $expectedPackageCommit -RequireStamp -RequiredPackageIds $requiredPackageIds)) {
        throw 'Avalonia package build completed but its stamped package closure could not be verified.'
    }
}

Push-Location $repoRoot
$originalGitPosition = $null
$restoreGitPosition = $false
try {
    if ($Target -eq 'local') {
        Write-Host 'Publishing current worktree without Git changes.'
    }
    else {
        Assert-CleanWorktree
        $originalGitPosition = Get-GitPosition

        if ([string]::IsNullOrWhiteSpace($Target)) {
            Write-Host 'Switching to master.'
            Invoke-Git switch master | Out-Null
            $restoreGitPosition = $true

            Write-Host 'Pulling origin/master.'
            Invoke-Git pull --ff-only origin master | Out-Null
        }
        else {
            & git show-ref --verify --quiet "refs/heads/$Target"
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Fetching origin/$Target."
                Invoke-Git fetch origin "refs/heads/$Target" | Out-Null
                $commit = (Invoke-Git rev-parse --verify FETCH_HEAD | Select-Object -First 1).Trim()
                Write-Host "Switching to branch $Target at fetched commit $commit."
                Invoke-Git switch --detach $commit | Out-Null
            }
            else {
                $commit = & git rev-parse --verify --quiet "$Target^{commit}" 2>$null
                if ($LASTEXITCODE -ne 0 -or -not $commit) {
                    throw "'$Target' is not a local branch or commit."
                }

                $commit = ($commit | Select-Object -First 1).Trim()
                Write-Host "Switching to commit $commit."
                Invoke-Git switch --detach $commit | Out-Null
            }

            $restoreGitPosition = $true
        }
    }

    Ensure-StableAvaloniaPackages -UseLocalAvalonia:($Target -eq 'local')

    $installParent = Split-Path -Parent $installDirectory
    New-Item -ItemType Directory -Path $installParent -Force | Out-Null
    if (-not (Test-DirectoryCreateAccess -Directory $installParent)) {
        throw "Cannot create local publish files under $installParent."
    }
    $publishStagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ClypDat.publish-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $publishStagingDirectory -Force | Out-Null

    Write-Host "Publishing to staging directory: $publishStagingDirectory"
    & $dotnetExecutable publish $appProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $publishStagingDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    & $selfContainedVerifier -PublishDirectory $publishStagingDirectory

    Stop-InstalledClypDatProcesses -InstallDirectory $installDirectory

    $previousInstallDirectory = $null
    if (Test-Path -LiteralPath $installDirectory) {
        $previousInstallDirectory = Join-Path $installParent ('.ClypDat.previous-' + [Guid]::NewGuid().ToString('N'))
        Write-Host 'Replacing previous local ClypDat installation.'
        Move-Item -LiteralPath $installDirectory -Destination $previousInstallDirectory
    }

    try {
        Move-Item -LiteralPath $publishStagingDirectory -Destination $installDirectory
    }
    catch {
        if ($previousInstallDirectory -and (Test-Path -LiteralPath $previousInstallDirectory) -and -not (Test-Path -LiteralPath $installDirectory)) {
            Move-Item -LiteralPath $previousInstallDirectory -Destination $installDirectory
        }
        throw
    }

    if ($previousInstallDirectory -and (Test-Path -LiteralPath $previousInstallDirectory)) {
        Remove-Item -LiteralPath $previousInstallDirectory -Recurse -Force
    }

    $installedExe = Join-Path $installDirectory 'ClypDat.exe'
    Write-Host "Installed local build to: $installedExe"

    Install-ClypDatDirectory -SourceDirectory $installDirectory -DestinationDirectory $programInstallDirectory
    Write-Host "Copied local build to: $(Join-Path $programInstallDirectory 'ClypDat.exe')"

    Write-Host 'Starting updated ClypDat.'
    Start-Process -FilePath $installedExe
}
finally {
    try {
        if ($restoreGitPosition) {
            Restore-GitPosition $originalGitPosition
        }
    }
    finally {
        Pop-Location
    }
}
