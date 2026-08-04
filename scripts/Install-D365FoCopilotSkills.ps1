<#
.SYNOPSIS
    Deploys the d365fo-cli Copilot Skill to an X++ project repo.

.DESCRIPTION
    Copies the bundled `d365fo-cli` Copilot skill folder:
      - skills/d365fo-cli/SKILL.md            (main rule canon + tool mapping)
      - skills/d365fo-cli/references/*.md      (19 lazily-loaded X++ topic files)
    into <XppRepo>/.github/skills/d365fo-cli/ so that GitHub Copilot in
    Visual Studio 2022 / 2026 (and VS Code) automatically picks up the skill.

    If the skill folder's references/ is empty (first run or clean clone), this
    script re-runs emit-skills.ps1 to regenerate them before copying.

    Re-run after pulling updates to d365fo-cli to keep the skill current.

    Legacy note: previous versions deployed .github/copilot-instructions.md and
    .github/instructions/*.instructions.md. Those files are no longer needed. If
    they exist in your X++ repo you can safely delete them.

.PARAMETER CliRepo
    Absolute path to your d365fo-cli clone.
    Default: the parent of the directory that contains this script.

.PARAMETER XppRepo
    Absolute path to the root of your X++ project repository (the folder that
    contains — or will contain — the .github/ directory).

.EXAMPLE
    .\Install-D365FoCopilotSkills.ps1 `
        -CliRepo  "C:\source\d365fo-cli" `
        -XppRepo  "K:\D365FO\MyProject"

.EXAMPLE
    # Run from inside the d365fo-cli scripts folder — CliRepo is auto-detected
    .\Install-D365FoCopilotSkills.ps1 -XppRepo "K:\D365FO\MyProject"

.NOTES
    After running, commit .github/ in your X++ repo so teammates get the same
    Copilot context automatically:
        git add .github/
        git commit -m "chore: add d365fo Copilot skill"
#>

[CmdletBinding()]
param(
    [string] $CliRepo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [Parameter(Mandatory)][string] $XppRepo
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ──────────────────────────────────────────────────────────────
$skillSrc    = Join-Path $CliRepo 'skills\d365fo-cli'
$referenceSrc = Join-Path $skillSrc 'references'
$dstSkill    = Join-Path $XppRepo '.github\skills\d365fo-cli'

Write-Host "d365fo-cli repo : $CliRepo"
Write-Host "X++ project repo: $XppRepo"
Write-Host ""

# ── Validate source ────────────────────────────────────────────────────────────
if (-not (Test-Path $CliRepo)) {
    Write-Error "CliRepo not found: $CliRepo"
}
if (-not (Test-Path $XppRepo)) {
    Write-Error "XppRepo not found: $XppRepo"
}

# ── Regenerate references if the folder is empty (first run / clean clone) ─────
$referenceFiles = Get-ChildItem -Path $referenceSrc -Filter '*.md' -ErrorAction SilentlyContinue
if ($referenceFiles.Count -eq 0) {
    Write-Warning "No reference files found in $referenceSrc — running emit-skills.ps1 first..."
    $emitScript = Join-Path $CliRepo 'scripts\emit-skills.ps1'
    if (Test-Path $emitScript) {
        & pwsh -NoProfile -File $emitScript
        $referenceFiles = Get-ChildItem -Path $referenceSrc -Filter '*.md' -ErrorAction SilentlyContinue
    } else {
        Write-Warning "emit-skills.ps1 not found at: $emitScript — run it manually, then re-run this script."
    }
}

# ── Create target directories ─────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $dstSkill | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dstSkill 'references') | Out-Null

# ── Copy SKILL.md ─────────────────────────────────────────────────────────────
$skillMd = Join-Path $skillSrc 'SKILL.md'
if (Test-Path $skillMd) {
    Copy-Item -Path $skillMd -Destination $dstSkill -Force
    Write-Host "[OK] .github\skills\d365fo-cli\SKILL.md"
} else {
    Write-Warning "SKILL.md not found at: $skillMd"
}

# ── Copy references ────────────────────────────────────────────────────────────
$copied = 0
foreach ($f in $referenceFiles) {
    Copy-Item -Path $f.FullName -Destination (Join-Path $dstSkill 'references') -Force
    Write-Host "[OK] .github\skills\d365fo-cli\references\$($f.Name)"
    $copied++
}

# ── Migration notice ──────────────────────────────────────────────────────────
$legacyCanon   = Join-Path $XppRepo '.github\copilot-instructions.md'
$legacyInstrDir = Join-Path $XppRepo '.github\instructions'
if ((Test-Path $legacyCanon) -or (Test-Path $legacyInstrDir)) {
    Write-Host ""
    Write-Host "⚠  Legacy files detected in your X++ repo:"
    if (Test-Path $legacyCanon)   { Write-Host "     .github\copilot-instructions.md" }
    if (Test-Path $legacyInstrDir) { Write-Host "     .github\instructions\" }
    Write-Host "   These are superseded by the d365fo-cli skill and can be safely deleted:"
    Write-Host "     Remove-Item -Recurse '$legacyCanon' -ErrorAction SilentlyContinue"
    Write-Host "     Remove-Item -Recurse '$legacyInstrDir' -ErrorAction SilentlyContinue"
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Deployed SKILL.md + $copied reference(s) to:"
Write-Host "  $dstSkill"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Restart Visual Studio / VS Code to pick up the new skill."
Write-Host "  2. Commit .github/ in your X++ project:"
Write-Host "       git -C `"$XppRepo`" add .github/"
Write-Host "       git -C `"$XppRepo`" commit -m `"chore: add d365fo Copilot skill`""
