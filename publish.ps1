param(
    [Parameter(Position = 0, HelpMessage = 'Optional target: local, branch, or commit hash.')]
    [string]$Target,

    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$nativeRoot = Join-Path $repoRoot 'native'
$appProject = Join-Path $nativeRoot 'src\ClypDat.App\ClypDat.App.csproj'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\ClypDat'
$dotnetExecutable = & (Join-Path $repoRoot 'eng\Ensure-DotNet.ps1')
$selfContainedVerifier = Join-Path $repoRoot 'eng\Test-SelfContainedPublish.ps1'
$requiredAvaloniaPackageIds = @(
    'Avalonia', 'Avalonia.Base', 'Avalonia.Controls', 'Avalonia.DesignerSupport',
    'Avalonia.Desktop', 'Avalonia.Dialogs', 'Avalonia.Fonts.Inter',
    'Avalonia.FreeDesktop', 'Avalonia.FreeDesktop.AtSpi', 'Avalonia.HarfBuzz',
    'Avalonia.Markup', 'Avalonia.Markup.Xaml', 'Avalonia.Metal', 'Avalonia.MicroCom',
    'Avalonia.Native', 'Avalonia.OpenGL', 'Avalonia.Remote.Protocol', 'Avalonia.Skia',
    'Avalonia.Themes.Fluent', 'Avalonia.Vulkan', 'Avalonia.Win32',
    'Avalonia.Win32.Automation', 'Avalonia.X11'
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

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

    & git -C $AvaloniaRoot worktree remove --force $fullWorktreeRoot 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath $fullWorktreeRoot)) {
        return
    }

    & git -C $AvaloniaRoot worktree prune 2>&1 | Out-Null
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

function Test-AvaloniaPackageSet {
    param(
        [Parameter(Mandatory)][string]$PackageOutput,
        [Parameter(Mandatory)][string]$PackageVersion,
        [string]$ExpectedCommit,
        [switch]$RequireStamp
    )

    try {
        $packageFiles = @(Get-ChildItem -LiteralPath $PackageOutput -Filter '*.nupkg' -File)
        $expectedNames = @($requiredAvaloniaPackageIds | ForEach-Object { "$_.$PackageVersion.nupkg" } | Sort-Object)
        $actualNames = @($packageFiles.Name | Sort-Object)
        if (($actualNames -join '|') -ne ($expectedNames -join '|')) {
            return $false
        }

        $packageIds = @{}
        foreach ($packageFile in $packageFiles) {
            $metadata = Read-AvaloniaNuspec -PackagePath $packageFile.FullName
            if ($metadata.version -ne $PackageVersion -or $metadata.id -notin $requiredAvaloniaPackageIds) {
                return $false
            }

            $packageIds[$metadata.id] = $true
            foreach ($dependency in (@($metadata.dependencies.group.dependency) + @($metadata.dependencies.dependency))) {
                if ($dependency.id -like 'Avalonia*' -and $dependency.version -eq $PackageVersion) {
                    $dependencyPath = Join-Path $PackageOutput "$($dependency.id).$PackageVersion.nupkg"
                    if (-not (Test-Path -LiteralPath $dependencyPath -PathType Leaf)) {
                        return $false
                    }
                }
            }
        }

        foreach ($packageId in $requiredAvaloniaPackageIds) {
            if (-not $packageIds.ContainsKey($packageId)) {
                return $false
            }
        }

        if ($RequireStamp) {
            $stampPath = Join-Path $PackageOutput 'clypdat-package-stamp.json'
            if (-not (Test-Path -LiteralPath $stampPath -PathType Leaf)) {
                return $false
            }

            $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
            if ($stamp.schema -ne 1 -or $stamp.commit -ne $ExpectedCommit -or $stamp.packageVersion -ne $PackageVersion) {
                return $false
            }

            $stampPackages = @($stamp.packages | Sort-Object)
            $expectedIds = @($requiredAvaloniaPackageIds | Sort-Object)
            if (($stampPackages -join '|') -ne ($expectedIds -join '|')) {
                return $false
            }
        }

        return $true
    }
    catch {
        Write-Verbose "Avalonia package validation failed: $($_.Exception.Message)"
        return $false
    }
}

function Ensure-StableAvaloniaPackages {
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

    if (Test-AvaloniaPackageSet -PackageOutput $packageOutput -PackageVersion $stableVersion -ExpectedCommit $stableCommit -RequireStamp) {
        Write-Host "Using stamped Avalonia package set for commit $stableCommit."
        return
    }

    if (-not (Test-Path -LiteralPath (Join-Path $avaloniaRoot '.git'))) {
        throw "The sibling Avalonia fork was not found at: $avaloniaRoot"
    }

    Write-Host "Stable Avalonia package stamp is missing or stale; fetching pinned commit $stableCommit and building version $stableVersion."
    $worktreeRoot = Join-Path ([IO.Path]::GetTempPath()) ('ca-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    $worktreeAdded = $false
    try {
        $resolvedCommit = & git -C $avaloniaRoot rev-parse --verify "$stableCommit^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            & git -C $avaloniaRoot fetch --no-tags origin main
            if ($LASTEXITCODE -ne 0) {
                throw "Could not fetch the pinned Avalonia commit $stableCommit."
            }
        }

        $resolvedCommit = & git -C $avaloniaRoot rev-parse --verify "$stableCommit^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $resolvedCommit) {
            throw "Pinned Avalonia commit $stableCommit could not be resolved after fetching origin/main. Verify the commit exists in the fork."
        }
        $resolvedCommit = ($resolvedCommit | Select-Object -First 1).Trim()

        & git -C $avaloniaRoot worktree add --detach $worktreeRoot $resolvedCommit
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create a temporary Avalonia worktree at $worktreeRoot for commit $resolvedCommit."
        }
        $worktreeAdded = $true

        & git -C $worktreeRoot submodule update --init --recursive
        if ($LASTEXITCODE -ne 0) {
            throw "Could not initialize Avalonia submodules in the temporary worktree."
        }

        $packageProject = Join-Path $worktreeRoot 'build\ClypDat.Win32Packages.proj'
        if (-not (Test-Path -LiteralPath $packageProject -PathType Leaf)) {
            throw "Pinned Avalonia commit $stableCommit does not contain the ClypDat package target."
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
            & $dotnetExecutable msbuild $packageProject /t:Pack "/p:ClypDatPackageVersion=$stableVersion" "/p:ClypDatPackageOutput=$stagingFullPath" /nologo
            if ($LASTEXITCODE -ne 0) {
                throw "Avalonia package build failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
        }

        if (-not (Test-AvaloniaPackageSet -PackageOutput $stagingFullPath -PackageVersion $stableVersion)) {
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
            packages = @($requiredAvaloniaPackageIds | Sort-Object)
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputFullPath 'clypdat-package-stamp.json') -Encoding utf8
    }
    finally {
        if ($worktreeAdded) {
            Remove-AvaloniaWorktree -AvaloniaRoot $avaloniaRoot -WorktreeRoot $worktreeRoot
        }
    }

    if (-not (Test-AvaloniaPackageSet -PackageOutput $packageOutput -PackageVersion $stableVersion -ExpectedCommit $resolvedCommit -RequireStamp)) {
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

    Ensure-StableAvaloniaPackages

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    $installPathPrefix = "$([IO.Path]::GetFullPath($installDirectory).TrimEnd('\'))\"
    $processesToStop = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installPathPrefix, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($process in $processesToStop) {
        Write-Host "Stopping process $($process.Name) (PID $($process.ProcessId))"
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    & $dotnetExecutable publish $appProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $installDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    & $selfContainedVerifier -PublishDirectory $installDirectory

    $installedExe = Join-Path $installDirectory 'ClypDat.exe'
    Write-Host "Installed local build to: $installedExe"

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
