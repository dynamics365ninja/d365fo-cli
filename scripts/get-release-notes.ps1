<#
.SYNOPSIS
    Prints the CHANGELOG.md section of one release, for the GitHub release notes.

.DESCRIPTION
    Returns everything between the "## [<Version>]" heading and the next "## " heading.
    Fails when the section is missing or empty, so a release cannot be published without
    notes, and when it is longer than a GitHub release body can be. See docs/RELEASING.md.

.EXAMPLE
    ./scripts/get-release-notes.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Changelog = (Join-Path $PSScriptRoot '..' | Join-Path -ChildPath 'CHANGELOG.md')
)

$ErrorActionPreference = 'Stop'

$lines = Get-Content -LiteralPath $Changelog -Encoding UTF8
$heading = '^## \[' + [regex]::Escape($Version) + '\]'

$section = New-Object System.Collections.Generic.List[string]
$inside = $false
foreach ($line in $lines) {
    if ($inside -and $line -match '^## ') { break }
    if ($inside) { $section.Add($line) }
    elseif ($line -match $heading) { $inside = $true }
}

$notes = ($section -join "`n").Trim() -replace '(\r?\n)+---$', ''
if (-not $inside) { throw "CHANGELOG.md has no '## [$Version]' section." }
if (-not $notes.Trim()) { throw "The '## [$Version]' section of CHANGELOG.md is empty." }
if ($notes.Length -gt 125000) {
    throw "The '## [$Version]' section of CHANGELOG.md is $($notes.Length) characters; a GitHub release body holds 125000. Summarise it and move the detail under another '## ' heading."
}

$notes
