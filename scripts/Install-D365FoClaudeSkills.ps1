<#
.SYNOPSIS
    Deploys the d365fo-cli Anthropic skill variant to an X++ project repo.

.DESCRIPTION
    Copies the bundled Anthropic skill folders:
      - skills/anthropic/<id>/SKILL.md      (one folder per topic)
    into <XppRepo>/.claude/skills/ so that Claude Code (CLI, VS Code and
    JetBrains extensions) and Claude Desktop pick them up automatically.

    This is the Anthropic-format sibling of Install-D365FoCopilotSkills.ps1.
    Both are emitted from the same single source, skills/_source/*.md, so the
    two ecosystems cannot disagree about a rule.

    If skills/anthropic/ is empty (first run or clean clone), this script
    regenerates it first, using whichever host is available: pwsh, Windows
    PowerShell, or python.

    Skill folders in the target that no longer exist upstream are removed, so a
    renamed or retired topic does not linger and keep feeding the agent guidance
    this version of the corpus has dropped. Only folders this script owns are
    considered: any other skill in .claude/skills/ is left alone.

    Re-run after pulling updates to d365fo-cli to keep the skills current.

.PARAMETER CliRepo
    Absolute path to your d365fo-cli clone.
    Default: the parent of the directory that contains this script.

.PARAMETER XppRepo
    Absolute path to the root of your X++ project repository (the folder that
    contains - or will contain - the .claude/ directory).

.EXAMPLE
    .\Install-D365FoClaudeSkills.ps1 `
        -CliRepo  "C:\source\d365fo-cli" `
        -XppRepo  "K:\D365FO\MyProject"

.EXAMPLE
    # Run from inside the d365fo-cli scripts folder - CliRepo is auto-detected
    .\Install-D365FoClaudeSkills.ps1 -XppRepo "K:\D365FO\MyProject"

.NOTES
    The skills teach the agent which `d365fo` commands to run; they do not
    replace the CLI. Install it first (see docs/SETUP.md) or the guidance has
    nothing to drive.

    After running, commit .claude/ in your X++ repo so teammates get the same
    context automatically:
        git add .claude/
        git commit -m "chore: add d365fo Claude skills"
#>

[CmdletBinding()]
param(
    [string] $CliRepo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [Parameter(Mandatory)][string] $XppRepo
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -- Resolve paths --------------------------------------------------------------
$skillSrc = Join-Path $CliRepo 'skills\anthropic'
$dstRoot  = Join-Path $XppRepo '.claude\skills'

Write-Host "d365fo-cli repo : $CliRepo"
Write-Host "X++ project repo: $XppRepo"
Write-Host ""

# -- Validate source ------------------------------------------------------------
if (-not (Test-Path $CliRepo)) {
    Write-Error "CliRepo not found: $CliRepo"
}
if (-not (Test-Path $XppRepo)) {
    Write-Error "XppRepo not found: $XppRepo"
}

# -- Regenerate skills if the folder is empty (first run / clean clone) ---------
# Note: @(...) keeps .Count valid under Set-StrictMode when the folder is absent.
$skillDirs = @(Get-ChildItem -Path $skillSrc -Directory -ErrorAction SilentlyContinue)
if ($skillDirs.Count -eq 0) {
    Write-Warning "No skills found in $skillSrc - regenerating..."
    $emitPs1 = Join-Path $CliRepo 'scripts\emit-skills.ps1'
    $emitPy  = Join-Path $CliRepo 'scripts\emit-skills.py'

    # Prefer whichever host is actually installed: pwsh 7, Windows PowerShell 5.1,
    # then python. A stock Windows / D365FO dev VM has no pwsh, so never assume it.
    $ran = $false
    if (Test-Path $emitPs1) {
        foreach ($hostExe in 'pwsh', 'powershell') {
            $cmd = Get-Command $hostExe -ErrorAction SilentlyContinue
            if ($cmd) {
                & $cmd.Source -NoProfile -File $emitPs1
                $ran = $true
                break
            }
        }
    }
    if (-not $ran -and (Test-Path $emitPy)) {
        foreach ($pyExe in 'python', 'python3') {
            $cmd = Get-Command $pyExe -ErrorAction SilentlyContinue
            if ($cmd) {
                & $cmd.Source $emitPy
                $ran = $true
                break
            }
        }
    }
    if ($ran) {
        $skillDirs = @(Get-ChildItem -Path $skillSrc -Directory -ErrorAction SilentlyContinue)
    } else {
        Write-Warning "Could not run an emitter (no PowerShell host or python found, or scripts missing under $CliRepo\scripts). Run 'scripts/emit-skills.ps1' manually, then re-run this script."
    }
}

if ($skillDirs.Count -eq 0) {
    Write-Error "No skills to install. Expected skills/anthropic/<id>/SKILL.md under $CliRepo."
}

# -- Copy each skill folder ----------------------------------------------------
New-Item -ItemType Directory -Force -Path $dstRoot | Out-Null

$copied = 0
foreach ($dir in $skillDirs) {
    $src = Join-Path $dir.FullName 'SKILL.md'
    if (-not (Test-Path $src)) {
        Write-Warning "skipping $($dir.Name): no SKILL.md"
        continue
    }
    $dst = Join-Path $dstRoot $dir.Name
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item -Path $src -Destination $dst -Force
    Write-Host "[OK] .claude\skills\$($dir.Name)\SKILL.md"
    $copied++
}

# -- Prune skills that no longer exist upstream --------------------------------
# Only folders that look like ours (they carry a SKILL.md we previously wrote and
# match an upstream naming) are considered - an unrelated skill in the same folder
# must survive.
$expected = @($skillDirs | ForEach-Object { $_.Name })
$stale = @(Get-ChildItem -Path $dstRoot -Directory -ErrorAction SilentlyContinue |
           Where-Object { $expected -notcontains $_.Name -and (Test-Path (Join-Path $_.FullName 'SKILL.md')) } |
           Where-Object { (Get-Content (Join-Path $_.FullName 'SKILL.md') -TotalCount 40) -match 'd365fo ' })
foreach ($d in $stale) {
    Remove-Item -Path $d.FullName -Recurse -Force
    Write-Host "[--] removed stale skill $($d.Name)"
}

Write-Host ""
Write-Host "$copied skill(s) installed to $dstRoot"
Write-Host "Next: install the CLI itself if you have not already - see docs/SETUP.md."
