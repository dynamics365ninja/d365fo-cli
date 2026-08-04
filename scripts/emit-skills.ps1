#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Emit Copilot and Anthropic Agent-Skills variants from skills/_source/*.md.

.DESCRIPTION
  Reads every Markdown file under skills/_source/ containing a YAML frontmatter
  block and emits two parallel artifacts:

    skills/copilot/<id>.instructions.md   (GitHub Copilot format: applyTo glob)
    skills/anthropic/<id>/SKILL.md        (Anthropic format: YAML description)

  Both outputs share the exact same body. Only the frontmatter is adapted to
  the target's semantics. The source file is the single source of truth.

.PARAMETER Source
  Path to the source directory. Defaults to ./skills/_source.

.PARAMETER OutRoot
  Root of the emitted artifacts. Defaults to ./skills.
#>
[CmdletBinding()]
param(
    [string]$Source  = (Join-Path $PSScriptRoot '..' 'skills' '_source'),
    [string]$OutRoot = (Join-Path $PSScriptRoot '..' 'skills')
)

$ErrorActionPreference = 'Stop'

function Split-Frontmatter {
    param([string]$Content)
    if ($Content -notmatch '^---\r?\n') {
        throw "Frontmatter block missing (must start with '---')."
    }
    $parts = $Content -split "(?m)^---\s*$", 3
    if ($parts.Count -lt 3) { throw "Malformed frontmatter fences." }
    return [pscustomobject]@{
        Frontmatter = $parts[1].Trim()
        Body        = $parts[2].TrimStart("`r", "`n")
    }
}

function Parse-Yaml {
    # Minimal YAML parser: only key: value, lists with "- " prefix on next lines.
    param([string]$Text)
    $map = [ordered]@{}
    $currentKey = $null
    $collecting = $null
    foreach ($line in ($Text -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^\s*-\s*(.+)$' -and $collecting) {
            $map[$collecting] += , ($Matches[1].Trim().Trim('"'))
            continue
        }
        if ($line -match '^([a-zA-Z0-9_\-]+)\s*:\s*(.*)$') {
            $key = $Matches[1]
            $value = $Matches[2].Trim()
            if ([string]::IsNullOrWhiteSpace($value)) {
                $map[$key] = @()
                $collecting = $key
            } else {
                $map[$key] = $value.Trim('"')
                $collecting = $null
            }
            continue
        }
    }
    return $map
}

function Emit-Copilot {
    param($Meta, [string]$Body, [string]$OutDir)
    $id = $Meta.id
    $desc = $Meta.description
    $applyTo = @($Meta.applyTo) | Where-Object { $_ }
    $glob = if ($applyTo.Count -gt 0) { $applyTo -join ',' } else { '**/*' }

    $fm = @"
---
description: $desc
applyTo: '$glob'
---
"@
    $path = Join-Path $OutDir "$id.instructions.md"
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    Set-Content -Path $path -Value "$fm`n$Body" -Encoding utf8 -NoNewline
    Write-Host "  [copilot]   $path"
}

function Emit-Anthropic {
    param($Meta, [string]$Body, [string]$OutDir)
    $id = $Meta.id
    $desc = $Meta.description
    $appliesWhen = $Meta.appliesWhen

    $fm = @"
---
name: $id
description: $desc
"@
    if ($appliesWhen) {
        $fm += "`napplies_when: $appliesWhen"
    }
    $fm += "`n---"

    $dir = Join-Path $OutDir $id
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir 'SKILL.md'
    Set-Content -Path $path -Value "$fm`n$Body" -Encoding utf8 -NoNewline
    Write-Host "  [anthropic] $path"
}

function Emit-CopilotSkill {
    # Writes body-only (no frontmatter) to skills/d365fo-cli/resources/<id>.md
    # so Copilot can lazily load topic guidance from the bundled d365fo-cli skill.
    param($Meta, [string]$Body, [string]$OutDir)
    $id = $Meta.id
    $path = Join-Path $OutDir "$id.md"
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    Set-Content -Path $path -Value $Body -Encoding utf8 -NoNewline
    Write-Host "  [d365fo-cli] $path"
}

Write-Host "Source: $Source"
$copilotOut      = Join-Path $OutRoot 'copilot'
$anthropicOut    = Join-Path $OutRoot 'anthropic'
$copilotSkillOut = Join-Path $OutRoot 'd365fo-cli' 'resources'

if (Test-Path $copilotOut)  { Remove-Item -Recurse -Force $copilotOut }
if (Test-Path $anthropicOut) { Remove-Item -Recurse -Force $anthropicOut }
# Note: d365fo-cli/resources is regenerated (not fully removed) so SKILL.md is preserved.
if (Test-Path $copilotSkillOut) { Remove-Item -Recurse -Force $copilotSkillOut }

$files = Get-ChildItem -Path $Source -Filter '*.md' -File
if ($files.Count -eq 0) { Write-Warning "No source skills found."; exit 0 }

foreach ($f in $files) {
    Write-Host "» $($f.Name)"
    $raw = Get-Content -Raw -Path $f.FullName
    $split = Split-Frontmatter -Content $raw
    $meta = Parse-Yaml -Text $split.Frontmatter
    if (-not $meta.id)          { throw "Missing 'id' in $($f.Name)." }
    if (-not $meta.description) { throw "Missing 'description' in $($f.Name)." }

    Emit-Copilot      -Meta $meta -Body $split.Body -OutDir $copilotOut
    Emit-Anthropic    -Meta $meta -Body $split.Body -OutDir $anthropicOut
    Emit-CopilotSkill -Meta $meta -Body $split.Body -OutDir $copilotSkillOut
}

Write-Host "`nDone. $($files.Count) skill(s) emitted to all three targets (copilot, anthropic, d365fo-cli)."
