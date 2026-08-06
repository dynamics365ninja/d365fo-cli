<#
.SYNOPSIS
  Derives the AOT form-pattern registry from Microsoft.Dynamics.AX.Metadata.Patterns.dll
  into src/D365FO.Core/FormPatterns/form-patterns.json.

.DESCRIPTION
  The AOS validates every form against its own pattern registry — a different
  validator from this repo's FP001-FP010 — and reports through
  `FormPatternValidation Error`. Getting a pattern name or version wrong is not a
  soft warning: "Unable to validate pattern 'DetailsMaster 1.1'. Message: Pattern
  'DetailsMaster 1.1' not found."

  That registry ships as 77 embedded XML resources inside
  Microsoft.Dynamics.AX.Metadata.Patterns.dll, declaring for every pattern its
  version and alias, the parts it requires (with cardinality), and the property
  values each part must carry. This script distills them so the catalog, the
  templates and the offline validator can agree with the AOS instead of with a
  hand-written model of it.

  Same shape of solution as scripts/emit-metadata-contracts.ps1, which derives the
  DataContract catalog from Microsoft.Dynamics.AX.Metadata.dll: run it on a machine
  with a D365FO installation, commit the JSON, and CI needs no installation.

.PARAMETER BinPath
  PackagesLocalDirectory\Bin. Defaults to $env:D365FO_PACKAGES_PATH\Bin.

.EXAMPLE
  pwsh scripts/emit-form-patterns.ps1
#>
[CmdletBinding()]
param(
    [string] $BinPath = $(if ($env:D365FO_PACKAGES_PATH) { Join-Path $env:D365FO_PACKAGES_PATH 'Bin' } else { 'K:\AosService\PackagesLocalDirectory\Bin' }),
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'

if (-not $OutFile) {
    $root = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { (Get-Location).Path }
    $OutFile = Join-Path $root 'src\D365FO.Core\FormPatterns\form-patterns.json'
}

$dll = Join-Path $BinPath 'Microsoft.Dynamics.AX.Metadata.Patterns.dll'
if (-not (Test-Path $dll)) {
    throw "Pattern registry not found at $dll. Set D365FO_PACKAGES_PATH (or pass -BinPath) to an installation."
}

$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$resourceNames = $asm.GetManifestResourceNames() | Where-Object { $_ -like '*FormPatterns*' -and $_.EndsWith('.xml') }

# One <Control> node -> its part id, cardinality and the property values the AOS
# requires on it. Recursion mirrors the registry's own nesting, which is what the
# validator walks when it reports "is missing child 'Panel Tab'".
function ConvertTo-Part([System.Xml.XmlElement] $control) {
    $props = @{}
    foreach ($p in $control.ChildNodes) {
        if ($p.LocalName -eq 'Property' -and $p.GetAttribute('Name')) {
            $props[$p.GetAttribute('Name')] = $p.InnerText
        }
    }

    $children = @()
    $subPatterns = @()
    foreach ($c in $control.ChildNodes) {
        switch ($c.LocalName) {
            'Control' { $children += (ConvertTo-Part $c) }
            # <OneOf> is a choice slot: exactly one of the listed controls appears
            # there. Skipping it silently drops real parts — LookupGridOnly's whole
            # list control (Grid | Tree | ListView) lives inside one.
            'OneOf' {
                $alts = @()
                foreach ($alt in $c.ChildNodes) {
                    if ($alt.LocalName -eq 'Control') { $alts += (ConvertTo-Part $alt) }
                }
                if ($alts.Count) {
                    $children += [ordered]@{
                        part  = ($alts | ForEach-Object { $_.part }) -join '|'
                        alias = $alts[0].alias
                        type  = ($alts | ForEach-Object { $_.type }) -join '|'
                        count = $(if ($c.GetAttribute('Count')) { $c.GetAttribute('Count') } else { '1' })
                        extraChildrenAllowed = $true
                        oneOf = $alts
                    }
                }
            }
            # <SubPattern Name="X" /> — the container must declare that sub-pattern.
            'SubPattern' { $subPatterns += $c.GetAttribute('Name') }
        }
    }

    $node = [ordered]@{
        part  = $control.GetAttribute('Part')
        alias = $control.GetAttribute('Alias')
        type  = $control.GetAttribute('Type')
        # Absent Count means exactly one; "*" on Children means extra children are allowed.
        count = $(if ($control.GetAttribute('Count')) { $control.GetAttribute('Count') } else { '1' })
        extraChildrenAllowed = ($control.GetAttribute('Children') -eq '*')
    }
    if ($props.Count) { $node.properties = $props }
    if ($subPatterns.Count) { $node.subPatterns = $subPatterns }
    if ($children.Count) { $node.children = $children }
    return $node
}

$patterns = @()
foreach ($name in ($resourceNames | Sort-Object)) {
    $stream = $asm.GetManifestResourceStream($name)
    $reader = New-Object System.IO.StreamReader($stream)
    $text = $reader.ReadToEnd()
    $reader.Dispose()

    try { $xml = [xml] $text } catch { Write-Warning "Skipping unparsable $name"; continue }

    foreach ($p in $xml.DocumentElement.ChildNodes) {
        if ($p.LocalName -ne 'Pattern') { continue }

        $design = $null
        foreach ($c in $p.ChildNodes) {
            if ($c.LocalName -eq 'Control') { $design = ConvertTo-Part $c; break }
        }

        $patterns += [ordered]@{
            name     = $p.GetAttribute('Name')
            version  = $p.GetAttribute('Version')
            alias    = $p.GetAttribute('Alias')
            active   = ($p.GetAttribute('Active') -ne 'false')
            category = $p.GetAttribute('Category')
            resource = $name
            design   = $design
        }
    }
}

$doc = [ordered]@{
    '$comment' = 'Generated by scripts/emit-form-patterns.ps1 from Microsoft.Dynamics.AX.Metadata.Patterns.dll - do not hand-edit. A <Pattern> name+version that is not in this list does not exist on any AOS: the form fails validation with "Pattern not found". Property values under a part are required by the AOS, not advisory.'
    assembly   = 'Microsoft.Dynamics.AX.Metadata.Patterns.dll'
    version    = $asm.GetName().Version.ToString()
    patterns   = ($patterns | Sort-Object { $_.name }, { $_.version })
}

$json = $doc | ConvertTo-Json -Depth 40
$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
$full = Join-Path (Resolve-Path -LiteralPath $outDir).Path (Split-Path $OutFile -Leaf)
[System.IO.File]::WriteAllText($full, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Wrote $($patterns.Count) pattern definitions to $OutFile"
