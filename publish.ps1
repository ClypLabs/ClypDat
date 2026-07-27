param(
    [Parameter(Position = 0, HelpMessage = 'Optional target: local, branch, or commit hash.')]
    [string]$Target,

    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$nativeRoot = Join-Path $repoRoot 'native'
$bridgeProject = Join-Path $nativeRoot 'src\ClypDat.ObsBridge\ClypDat.ObsBridge.vcxproj'
$bridgeDll = Join-Path $nativeRoot 'src\ClypDat.ObsBridge\bin\x64\Release\ClypDat.ObsBridge.dll'
$appProject = Join-Path $nativeRoot 'src\ClypDat.App\ClypDat.App.csproj'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\ClypDat'

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

function Find-MSBuild {
    $onPath = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $vswherePaths = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
    )

    foreach ($vswhere in $vswherePaths) {
        if (-not (Test-Path -LiteralPath $vswhere)) {
            continue
        }

        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
        if ($LASTEXITCODE -eq 0 -and $found -and (Test-Path -LiteralPath $found)) {
            return $found
        }
    }

    return $null
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
                $commit = (Invoke-Git rev-parse --verify "refs/heads/$Target^{commit}" | Select-Object -First 1).Trim()
                Write-Host "Switching to branch $Target at commit $commit."
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

    $msbuild = Find-MSBuild
    if ($msbuild) {
        Write-Host "Building OBS bridge with $msbuild"
        $solutionDirectory = "$($nativeRoot.Replace('\', '/'))/"
        & $msbuild $bridgeProject "/p:SolutionDir=$solutionDirectory" /p:Configuration=Release /p:Platform=x64 /nologo /v:minimal
        if ($LASTEXITCODE -ne 0) {
            throw "MSBuild failed with exit code $LASTEXITCODE."
        }
    }
    elseif (Test-Path -LiteralPath $bridgeDll) {
        Write-Warning 'MSBuild is unavailable; using the existing local ClypDat.ObsBridge.dll.'
    }
    else {
        throw 'MSBuild is unavailable and no local ClypDat.ObsBridge.dll exists. Install Visual Studio Build Tools with the C++ x64 workload, then rerun this script.'
    }

    if (-not (Test-Path -LiteralPath $bridgeDll)) {
        throw 'ClypDat.ObsBridge.dll was not found after the bridge build.'
    }

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    $installPathPrefix = "$([IO.Path]::GetFullPath($installDirectory).TrimEnd('\'))\"
    $processesToStop = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installPathPrefix, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($process in $processesToStop) {
        Write-Host "Stopping process $($process.Name) (PID $($process.ProcessId))"
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $installDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $stagedBridgeDirectory = Join-Path $installDirectory 'obs'
    New-Item -ItemType Directory -Path $stagedBridgeDirectory -Force | Out-Null
    Copy-Item -LiteralPath $bridgeDll -Destination (Join-Path $stagedBridgeDirectory 'ClypDat.ObsBridge.dll') -Force

    $obsRuntime = Join-Path $nativeRoot 'vendor\obs-runtime'
    if (-not (Test-Path -LiteralPath $obsRuntime)) {
        Write-Warning 'OBS runtime is not staged locally; the published app will not have OBS capture support.'
    }

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
