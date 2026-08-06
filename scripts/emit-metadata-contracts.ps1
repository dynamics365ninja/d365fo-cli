<#
.SYNOPSIS
    Emits the MetaModel serialization contract catalog consumed by D365FO.Core.

.DESCRIPTION
    Reads Microsoft.Dynamics.AX.Metadata.dll and writes, for every MetaModel DataContract
    type, its XML namespace and the exact order its members serialize in.

    That order is not cosmetic. The AOT's on-disk format is DataContract, and
    DataContractSerializer matches elements in contract order: an element that appears out
    of order, or one the type does not declare, is silently ignored. The file still parses,
    offline validators still pass, and the property is simply gone — a `JoinMode` that
    vanishes turns an inner join into a cross join with nothing to show for it.

    The order is: members of the base class first, then the derived class, and within one
    class by DataMember.Order, then contract name compared ORDINALLY — `CancelMenuItem`
    sorts before `CanceledEventHandler` because 'M' < 'e' in code-point order, which is what
    shipped files show and what a culture-aware sort gets backwards. Inherited members are
    private `___serialize_*` properties, invisible to a flattened GetProperties, so the
    hierarchy is walked explicitly.

.PARAMETER BinPath
    Folder holding Microsoft.Dynamics.AX.Metadata.dll — usually
    <PackagesLocalDirectory>\bin. Defaults to $env:D365FO_BIN_PATH.

.PARAMETER OutFile
    Destination JSON. Defaults to the embedded resource in D365FO.Core.

.NOTES
    Requires a machine with the D365FO metadata assemblies (Windows PowerShell, net48).
    The output is committed, so everyone else — and CI — gets the data without them.
#>
[CmdletBinding()]
param(
    [string]$BinPath = $env:D365FO_BIN_PATH,
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\D365FO.Core\Metadata\metadata-contracts.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.Serialization

if (-not $BinPath) { throw "Set -BinPath or D365FO_BIN_PATH to the folder holding Microsoft.Dynamics.AX.Metadata.dll." }
$dll = Join-Path $BinPath 'Microsoft.Dynamics.AX.Metadata.dll'
if (-not (Test-Path $dll)) { throw "Not found: $dll" }

$asm = [Reflection.Assembly]::LoadFrom($dll)
$version = $asm.GetName().Version.ToString()

# Enum name -> allowed serialized values, collected as members are walked. An out-of-range
# value is not a dropped element like an unknown member is: DataContractSerializer throws and
# the provider cannot read the file at all, so the object is invisible to the whole toolchain.
$enums = @{}

function Get-EnumName($propertyType) {
    $t = $propertyType
    $underlying = [Nullable]::GetUnderlyingType($t)
    if ($underlying) { $t = $underlying }
    if (-not $t.IsEnum) { return $null }

    if (-not $enums.ContainsKey($t.Name)) {
        # DataContract honours [EnumMember(Value=...)] over the CLR name.
        $values = [System.Collections.Generic.List[string]]::new()
        foreach ($f in $t.GetFields('Public,Static')) {
            $em = $f.GetCustomAttributes([System.Runtime.Serialization.EnumMemberAttribute], $false)
            if ($em -and $em[0].Value) { $values.Add($em[0].Value) } else { $values.Add($f.Name) }
        }
        $values.Sort([System.StringComparer]::Ordinal)
        $enums[$t.Name] = @($values)
    }
    return $t.Name
}

function Get-ContractMembers($type) {
    # Base class first, then derived — DataContractSerializer's own ordering.
    $chain = @()
    $t = $type
    while ($t -and $t.FullName -ne 'System.Object') { $chain = , $t + $chain; $t = $t.BaseType }

    $ordered = @()
    $enumOf = [ordered]@{}
    foreach ($level in $chain) {
        $members = @()
        foreach ($p in $level.GetProperties('Public,NonPublic,Instance,DeclaredOnly')) {
            $attr = $p.GetCustomAttributes([System.Runtime.Serialization.DataMemberAttribute], $false)
            if (-not $attr) { continue }
            $name = if ($attr[0].Name) { $attr[0].Name } else { $p.Name -replace '^___serialize_', '' }
            $members += [pscustomobject]@{ Name = $name; Order = $attr[0].Order }

            $enumName = Get-EnumName $p.PropertyType
            if ($enumName) { $enumOf[$name] = $enumName }
        }

        foreach ($group in ($members | Group-Object Order | Sort-Object { [int]$_.Name })) {
            $names = [System.Collections.Generic.List[string]]::new()
            foreach ($m in $group.Group) { $names.Add($m.Name) }
            $names.Sort([System.StringComparer]::Ordinal)
            $ordered += $names
        }
    }
    return [pscustomobject]@{ Members = $ordered; Enums = $enumOf }
}

$types = $asm.GetTypes() |
    Where-Object { $_.Namespace -eq 'Microsoft.Dynamics.AX.Metadata.MetaModel' } |
    Where-Object { $_.GetCustomAttributes([System.Runtime.Serialization.DataContractAttribute], $false) } |
    Sort-Object Name

$known = @{}
foreach ($t in $types) { $known[$t.Name] = $true }

$map = [ordered]@{}
foreach ($t in $types) {
    $contract = $t.GetCustomAttributes([System.Runtime.Serialization.DataContractAttribute], $false)[0]
    $walked = Get-ContractMembers $t
    $members = $walked.Members
    if (-not $members) { continue }

    # The base type matters at read time: an element named after a base (<AxFormDataSource>)
    # routinely carries a subtype's members (AxFormDataSourceRoot's), so a consumer has to be
    # able to walk the hierarchy before calling a member unknown.
    $baseName = $null
    if ($t.BaseType -and $known.ContainsKey($t.BaseType.Name)) { $baseName = $t.BaseType.Name }

    $entry = [ordered]@{
        ns       = [string]$contract.Namespace
        abstract = $t.IsAbstract
        base     = $baseName
        members  = @($members)
    }
    if ($walked.Enums.Count -gt 0) { $entry['enumOf'] = $walked.Enums }
    $map[$t.Name] = $entry
}

$enumMap = [ordered]@{}
foreach ($key in ($enums.Keys | Sort-Object)) { $enumMap[$key] = $enums[$key] }

$payload = [ordered]@{
    '$comment'    = 'Generated by scripts/emit-metadata-contracts.ps1 — do not hand-edit. Member order is the order DataContractSerializer reads and writes; anything out of order is silently dropped. enumOf maps a member to its enum; an out-of-range value fails the whole read.'
    assembly      = 'Microsoft.Dynamics.AX.Metadata.dll'
    version       = $version
    types         = $map
    enums         = $enumMap
}

$dir = Split-Path $OutFile -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$json = $payload | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $dir).Path + '\' + (Split-Path $OutFile -Leaf), $json, (New-Object System.Text.UTF8Encoding($false)))

"{0} types, {1} members -> {2}" -f $map.Count, ($map.Values | ForEach-Object { $_.members.Count } | Measure-Object -Sum).Sum, $OutFile
