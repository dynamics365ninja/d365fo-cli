# d365fo CLI - one-line installer (Windows PowerShell 5.1+ / PowerShell 7+)
#
#   irm https://raw.githubusercontent.com/dynamics365ninja/d365fo-cli/main/install.ps1 | iex
#
# Bootstraps a full installation from source and hands off to 'd365fo init',
# which detects your PackagesLocalDirectory, writes the config, and reports
# what's left to do. Safe to re-run — an existing checkout is updated in
# place instead of re-cloned.
#
# There is no package registry yet (see docs/MIGRATION_FROM_MCP.md) — this
# script clones the repo, builds a self-contained binary with 'dotnet
# publish', and puts it on PATH. That's the "compilation" step; 'd365fo init'
# is the setup wizard.
#
# Piped through Invoke-Expression, so configuration is env vars, not params:
#
#   $env:D365FO_CLI_DIR = 'D:\tools\d365fo-cli'   # where to clone / look for an existing checkout
#   $env:D365FO_CLI_YES = '1'                     # non-interactive: accept all defaults
#   $env:D365FO_CLI_NO_WIZARD = '1'                # install only, skip 'd365fo init'
#   $env:D365FO_CLI_RUN_EXTRACT = '1'              # also run 'index build' + 'index extract' (can take minutes)
#
# D365FO VMs are Windows Server, where winget is usually unavailable - the
# .NET SDK and Git both fall back to portable/official installers that need
# no elevation.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$MinDotnetMajor = 10
$RepoUrl = 'https://github.com/dynamics365ninja/d365fo-cli.git'
# Pinned MinGit fallback for machines without winget (Windows Server). Portable,
# extracted under %LOCALAPPDATA% - used only when git is not already installed.
$MinGitUrl = 'https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/MinGit-2.47.1-64-bit.zip'

function Write-Step([string]$msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)    { Write-Host "  + $msg" -ForegroundColor Green }
function Write-Note([string]$msg)  { Write-Host "  * $msg" -ForegroundColor Yellow }
function Fail([string]$msg) { Write-Host "  x $msg" -ForegroundColor Red; exit 1 }

$NonInteractive = $env:D365FO_CLI_YES -and $env:D365FO_CLI_YES -ne '0' -and $env:D365FO_CLI_YES -ne 'false'

# Re-read PATH from the registry so tools installed a moment ago resolve
# without opening a new shell.
function Refresh-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Test-Cmd([string]$name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

function Ensure-DotNet {
    if (Test-Cmd dotnet) {
        $major = [int]((dotnet --version) -replace '^(\d+)\..*', '$1')
        if ($major -ge $MinDotnetMajor) { Write-Ok ".NET SDK $(dotnet --version)"; return }
        Write-Note ".NET SDK $(dotnet --version) found, but $MinDotnetMajor+ is required"
    }
    if (Test-Cmd winget) {
        Write-Step 'Installing .NET SDK via winget'
        winget install --id Microsoft.DotNet.SDK.$MinDotnetMajor --accept-source-agreements --accept-package-agreements
        Refresh-Path
    } else {
        # Official install script - no elevation needed, installs under %LOCALAPPDATA%\dotnet.
        Write-Step "Downloading the .NET SDK $MinDotnetMajor install script from dot.net"
        $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
        & $installer -Channel "$MinDotnetMajor.0" -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
        $dotnetDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
        $env:Path = "$dotnetDir;$env:Path"
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -notlike "*$dotnetDir*") {
            [Environment]::SetEnvironmentVariable('Path', "$userPath;$dotnetDir", 'User')
        }
    }
    if (-not (Test-Cmd dotnet)) { Fail '.NET SDK still not on PATH - open a new PowerShell window and re-run this script.' }
    $major = [int]((dotnet --version) -replace '^(\d+)\..*', '$1')
    if ($major -lt $MinDotnetMajor) { Fail ".NET SDK $(dotnet --version) is still below $MinDotnetMajor - install it from https://dotnet.microsoft.com/download and re-run." }
    Write-Ok ".NET SDK $(dotnet --version)"
}

function Ensure-Git {
    if (Test-Cmd git) { Write-Ok "$(git --version)"; return }
    if (Test-Cmd winget) {
        Write-Step 'Installing Git via winget'
        winget install --id Git.Git --accept-source-agreements --accept-package-agreements
        Refresh-Path
        if (Test-Cmd git) { Write-Ok "$(git --version)"; return }
    }
    Write-Step 'Installing portable MinGit (no winget on this machine)'
    $minGitDir = Join-Path $env:LOCALAPPDATA 'd365fo-cli\MinGit'
    $zip = Join-Path $env:TEMP 'MinGit.zip'
    Invoke-WebRequest $MinGitUrl -OutFile $zip
    Expand-Archive $zip -DestinationPath $minGitDir -Force
    $gitCmd = Join-Path $minGitDir 'cmd'
    $env:Path = "$gitCmd;$env:Path"
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notlike "*$gitCmd*") {
        [Environment]::SetEnvironmentVariable('Path', "$userPath;$gitCmd", 'User')
    }
    if (-not (Test-Cmd git)) { Fail 'Git installation failed - install Git from https://git-scm.com and re-run.' }
    Write-Ok "$(git --version) (portable)"
}

# Probed rather than asked, same reasoning as a fresh npm install has nowhere
# to name: a directory only matters once something is already there.
function Find-Checkout {
    $candidates = @()
    if ($env:D365FO_CLI_DIR) { $candidates += $env:D365FO_CLI_DIR }
    $candidates += 'K:\d365fo-cli'
    $candidates += (Join-Path $env:USERPROFILE 'd365fo-cli')

    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir '.git')) { return $dir }
    }
    if ($env:D365FO_CLI_DIR -and (Test-Path $env:D365FO_CLI_DIR) -and (Get-ChildItem $env:D365FO_CLI_DIR -Force | Select-Object -First 1)) {
        Fail "$($env:D365FO_CLI_DIR) exists, is not empty, and is not a git checkout. Empty it or point `$env:D365FO_CLI_DIR elsewhere."
    }
    return $null
}

function Get-InstallDir {
    if ($env:D365FO_CLI_DIR) { return $env:D365FO_CLI_DIR }
    return (Join-Path $env:USERPROFILE 'd365fo-cli')
}

# Publishes a self-contained single-file binary and puts it on the user PATH,
# so 'd365fo' resolves from any shell the way a global npm/dotnet-tool install
# would - without needing a package registry.
function Build-And-Install([string]$dir) {
    Push-Location $dir
    try {
        Write-Step 'Publishing d365fo (dotnet publish -c Release)'
        $binDir = Join-Path $env:LOCALAPPDATA 'd365fo-cli\bin'
        dotnet publish src\D365FO.Cli -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -p:PublishTrimmed=true -o $binDir
        if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed - see the error above.' }

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -notlike "*$binDir*") {
            [Environment]::SetEnvironmentVariable('Path', "$userPath;$binDir", 'User')
        }
        $env:Path = "$env:Path;$binDir"
        Write-Ok "Installed to $binDir"
    } finally {
        Pop-Location
    }

    if (-not (Test-Cmd 'd365fo')) {
        Fail 'd365fo built but is not on PATH - open a new PowerShell window and run: d365fo doctor'
    }

    if ($env:D365FO_CLI_NO_WIZARD) {
        Write-Note 'Skipping the setup wizard (D365FO_CLI_NO_WIZARD set).'
        Write-Host ''
        Write-Host 'Next: d365fo init --persist-profile' -ForegroundColor Magenta
        return
    }

    Write-Step "Running 'd365fo init' (interactive wizard in a real terminal; detects PackagesLocalDirectory)"
    $initArgs = @('--persist-profile')
    if ($env:D365FO_CLI_RUN_EXTRACT) { $initArgs += '--run-extract' }
    # D365FO_CLI_YES means "accept defaults, don't ask" - 'd365fo init' has its
    # own wizard now, so honor that env var by skipping straight to it instead
    # of leaving the promise in the header comment unfulfilled.
    if ($NonInteractive) { $initArgs += '--no-wizard' }
    d365fo init @initArgs

    Write-Step "Running 'd365fo doctor'"
    d365fo doctor

    Write-Host ''
    Write-Host 'Useful commands (from anywhere):' -ForegroundColor Magenta
    Write-Host '  d365fo doctor                    health check'
    Write-Host '  d365fo index build; d365fo index extract   populate the index (skip if D365FO_CLI_RUN_EXTRACT was set)'
    Write-Host '  d365fo --help                    command list'
    Write-Host ''
    Write-Host 'Connect an AI agent: docs/SETUP.md#step-5--connect-your-ai-agent' -ForegroundColor Magenta
}

# --- main -------------------------------------------------------------------

if ($env:OS -ne 'Windows_NT') {
    Fail 'This installer targets Windows (D365FO development VMs). On other platforms: git clone, then dotnet publish - see docs/SETUP.md, or run install.sh.'
}
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

Write-Host ''
Write-Host 'd365fo CLI - installer' -ForegroundColor Magenta
Write-Host ''

Write-Step 'Checking prerequisites'
Ensure-DotNet
Ensure-Git

$checkout = Find-Checkout
if ($checkout) {
    Write-Note "Existing checkout found at $checkout - updating it in place."
    Write-Step "Updating $checkout"
    git -C $checkout pull --ff-only
    if ($LASTEXITCODE -ne 0) { Fail 'git pull failed - resolve the conflict in the install directory and re-run.' }
    Build-And-Install $checkout
} else {
    $installDir = Get-InstallDir
    Write-Step "Cloning into $installDir"
    git clone $RepoUrl $installDir
    if ($LASTEXITCODE -ne 0) { Fail 'git clone failed - see the error above.' }
    Build-And-Install $installDir
}
