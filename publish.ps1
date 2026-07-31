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
