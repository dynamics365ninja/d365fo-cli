#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Emit Copilot, Anthropic, and d365fo-cli skill variants from skills/_source/*.md.

.DESCRIPTION
  Reads every Markdown file under skills/_source/ containing a YAML frontmatter
  block and emits three parallel artifacts:

    skills/copilot/<id>.instructions.md   (GitHub Copilot format: applyTo glob)
    skills/anthropic/<id>/SKILL.md        (Anthropic format: YAML description)
    skills/d365fo-cli/references/<id>.md  (Agent-skill resource: body only)

  All outputs share the exact same body. Only the frontmatter is adapted to
  the target's semantics. The source file is the single source of truth.

  Runs on Windows PowerShell 5.1 and PowerShell 7+. All files are written as
  UTF-8 without BOM so the output is byte-identical to scripts/emit-skills.py.

.PARAMETER Source
  Path to the source directory. Defaults to ./skills/_source.

.PARAMETER OutRoot
  Root of the emitted artifacts. Defaults to ./skills.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$OutRoot
)

# Windows PowerShell 5.1 evaluates param() defaults in the caller's scope, where
# $PSScriptRoot is empty. Resolve the defaults in the script body instead.
$repoRoot = Join-Path $PSScriptRoot '..'
if (-not $Source)  { $Source  = Join-Path (Join-Path $repoRoot 'skills') '_source' }
if (-not $OutRoot) { $OutRoot = Join-Path $repoRoot 'skills' }

$ErrorActionPreference = 'Stop'
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

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

# Kept in lock-step with needs_quoting()/yaml_scalar() in scripts/emit-skills.py.
$script:YamlLeaders = '-?:,[]{}#&*!|>''"%@`'.ToCharArray()
$script:YamlReserved = @('true', 'false', 'yes', 'no', 'on', 'off', 'null', 'none', '~', '')

<#
.SYNOPSIS
    Renders a value as a YAML scalar, quoting only when a bare one would misparse.
.DESCRIPTION
    The failure this guards against is a description containing ": " — YAML reads
    that as a nested mapping key and the whole frontmatter block stops parsing
    (issue #172). Deliberately conservative: quoting a value that would have been
    fine costs nothing, missing one breaks every consumer of the generated file.
#>
function ConvertTo-YamlScalar {
    param([string]$Value)

    $text = [string]$Value
    $needsQuoting =
        $text -ne $text.Trim() -or
        $script:YamlReserved -contains $text.ToLowerInvariant() -or
        ($text.Length -gt 0 -and $script:YamlLeaders -contains $text[0]) -or
        $text.Contains(': ') -or
        $text.EndsWith(':') -or
        $text.Contains(' #')

    if (-not $needsQuoting) { return $text }
    return '"' + ($text -replace '\\', '\\' -replace '"', '\"') + '"'
}

function Emit-Copilot {
    param($Meta, [string]$Body, [string]$OutDir)
    $id = $Meta.id
    $desc = ConvertTo-YamlScalar $Meta.description
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
    [System.IO.File]::WriteAllText($path, "$fm`n$Body", $Utf8NoBom)
    Write-Host "  [copilot]   $path"
}

function Emit-Anthropic {
    param($Meta, [string]$Body, [string]$OutDir)
    $id = $Meta.id
    $desc = ConvertTo-YamlScalar $Meta.description
    $appliesWhen = if ($Meta.appliesWhen) { ConvertTo-YamlScalar $Meta.appliesWhen } else { $null }

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
    [System.IO.File]::WriteAllText($path, "$fm`n$Body", $Utf8NoBom)
    Write-Host "  [anthropic] $path"
}

function Emit-CopilotSkill {
    # Writes body-only (no frontmatter) to skills/d365fo-cli/references/<id>.md
    # so Copilot can lazily load topic guidance from the bundled d365fo-cli skill.
    param($Meta, [string]$Body, [string]$OutDir)
    $id = $Meta.id
    $path = Join-Path $OutDir "$id.md"
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $Body, $Utf8NoBom)
    Write-Host "  [d365fo-cli] $path"
}

Write-Host "Source: $Source"
$copilotOut      = Join-Path $OutRoot 'copilot'
$anthropicOut    = Join-Path $OutRoot 'anthropic'
$copilotSkillOut = Join-Path (Join-Path $OutRoot 'd365fo-cli') 'references'

if (Test-Path $copilotOut)  { Remove-Item -Recurse -Force $copilotOut }
if (Test-Path $anthropicOut) { Remove-Item -Recurse -Force $anthropicOut }
# Note: d365fo-cli/references is regenerated (not fully removed) so SKILL.md is preserved.
if (Test-Path $copilotSkillOut) { Remove-Item -Recurse -Force $copilotSkillOut }

$files = Get-ChildItem -Path $Source -Filter '*.md' -File
if ($files.Count -eq 0) { Write-Warning "No source skills found."; exit 0 }

foreach ($f in $files) {
    # ASCII only: these .ps1 files have no BOM, so Windows PowerShell 5.1 reads
    # them as ANSI and would mangle non-ASCII output. (A BOM is not an option --
    # it would break the #!/usr/bin/env pwsh shebang on Linux/macOS.)
    Write-Host "-> $($f.Name)"
    $raw = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    $split = Split-Frontmatter -Content $raw
    $meta = Parse-Yaml -Text $split.Frontmatter
    if (-not $meta.id)          { throw "Missing 'id' in $($f.Name)." }
    if (-not $meta.description) { throw "Missing 'description' in $($f.Name)." }

    Emit-Copilot      -Meta $meta -Body $split.Body -OutDir $copilotOut
    Emit-Anthropic    -Meta $meta -Body $split.Body -OutDir $anthropicOut
    Emit-CopilotSkill -Meta $meta -Body $split.Body -OutDir $copilotSkillOut
}

Write-Host "`nDone. $($files.Count) skill(s) emitted to all three targets (copilot, anthropic, d365fo-cli)."
