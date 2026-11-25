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

    Also writes `formContainment`: which form control types each parent control accepts, read
    from the rule tables the compiler itself judges forms against
    (Microsoft.Dynamics.AX.Metadata.Validation.dll). The script then asks the compiler's own
    method about every parent/child pair and fails if the emitted tables answer differently.

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
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.Serialization

# Not a parameter default: under `powershell -File`, Windows PowerShell 5.1 evaluates those
# before $PSScriptRoot is set.
if (-not $OutFile) { $OutFile = Join-Path $PSScriptRoot '..\src\D365FO.Core\Metadata\metadata-contracts.json' }

if (-not $BinPath) { throw "Set -BinPath or D365FO_BIN_PATH to the folder holding Microsoft.Dynamics.AX.Metadata.dll." }
$dll = Join-Path $BinPath 'Microsoft.Dynamics.AX.Metadata.dll'
if (-not (Test-Path $dll)) { throw "Not found: $dll" }

$asm = [Reflection.Assembly]::LoadFrom($dll)
$version = $asm.GetName().Version.ToString()

# Enums and a handful of contract structs live in the Core assembly, not the MetaModel one.
# AxSecurityEntryPointReference.Grant is one of them: an AccessGrant with six permission
# members, invisible to a scan of Microsoft.Dynamics.AX.Metadata.dll alone — which is why
# nothing could tell that generated privileges wrote <AccessLevel> into a type that has none.
$coreDll = Join-Path $BinPath 'Microsoft.Dynamics.AX.Metadata.Core.dll'
$coreAsm = if (Test-Path $coreDll) { [Reflection.Assembly]::LoadFrom($coreDll) } else { $null }

# Namespaces whose DataContract types are part of the on-disk AOT vocabulary.
$contractNamespaces = @(
    'Microsoft.Dynamics.AX.Metadata.MetaModel',
    'Microsoft.Dynamics.AX.Metadata.Core.MetaModel'
)

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

# The contract type a member holds — the property type itself, or a collection's element type.
function Get-ValueContractType($propertyType) {
    $t = $propertyType
    if ($t.IsGenericType) {
        $args = $t.GetGenericArguments()
        if ($args.Count -eq 1) { $t = $args[0] }
    }
    if (-not $t) { return $null }
    if ($t.IsEnum) { return $null }
    if ($contractNamespaces -notcontains $t.Namespace) { return $null }
    if (-not $t.GetCustomAttributes([System.Runtime.Serialization.DataContractAttribute], $false)) { return $null }
    return $t
}

function Get-ContractMembers($type, $ownNamespace) {
    # Base class first, then derived — DataContractSerializer's own ordering.
    $chain = @()
    $t = $type
    while ($t -and $t.FullName -ne 'System.Object') { $chain = , $t + $chain; $t = $t.BaseType }

    $ordered = @()
    $enumOf = [ordered]@{}
    $typeOf = [ordered]@{}
    foreach ($level in $chain) {
        $members = @()
        foreach ($p in $level.GetProperties('Public,NonPublic,Instance,DeclaredOnly')) {
            $attr = $p.GetCustomAttributes([System.Runtime.Serialization.DataMemberAttribute], $false)
            if (-not $attr) { continue }
            $name = if ($attr[0].Name) { $attr[0].Name } else { $p.Name -replace '^___serialize_', '' }
            $members += [pscustomobject]@{ Name = $name; Order = $attr[0].Order }

            $enumName = Get-EnumName $p.PropertyType
            if ($enumName) { $enumOf[$name] = $enumName }

            # The contract a member holds. Without this, everything inside a member-typed
            # sub-object is invisible: <Grant> under an entry point is an AccessGrant, but its
            # element is named after the member, so nothing could look up what belongs in it —
            # which is how <AccessLevel> was written into a type that has no such member. It
            # also fixes the namespace: a member whose contract declares a different namespace
            # starts a subtree in that namespace, and an AxReport's <DefaultParameterGroup>
            # written in the report's own V2 loses every parameter.
            $valueType = Get-ValueContractType $p.PropertyType
            if ($valueType) { $typeOf[$name] = (Get-ContractName $valueType) }
        }

        foreach ($group in ($members | Group-Object Order | Sort-Object { [int]$_.Name })) {
            $names = [System.Collections.Generic.List[string]]::new()
            foreach ($m in $group.Group) { $names.Add($m.Name) }
            $names.Sort([System.StringComparer]::Ordinal)
            $ordered += $names
        }
    }
    return [pscustomobject]@{ Members = $ordered; Enums = $enumOf; TypeOf = $typeOf }
}

$candidates = @($asm.GetTypes())
if ($coreAsm) { $candidates += @($coreAsm.GetTypes()) }

$types = $candidates |
    Where-Object { $contractNamespaces -contains $_.Namespace } |
    Where-Object { -not $_.IsEnum } |
    Where-Object { $_.GetCustomAttributes([System.Runtime.Serialization.DataContractAttribute], $false) } |
    Sort-Object Name

# Contract name, which is what appears in XML — NOT the CLR name. Eighteen types differ, and
# the difference is not cosmetic: AxFormDataSourceRoot serializes as <AxFormDataSource>, the
# same name as a real abstract CLR type. A catalog keyed by CLR name answers that lookup with
# the abstract base's five members instead of the root's thirty, and every property past the
# fifth looks unknown. AxMethodPropertyCollection writes as <Method>; AxMethodsContainer as
# <Methods>.
function Get-ContractName($type) {
    $dc = $type.GetCustomAttributes([System.Runtime.Serialization.DataContractAttribute], $false)
    if ($dc -and $dc[0].Name) { return $dc[0].Name }
    return $type.Name
}

$known = @{}
foreach ($t in $types) { $known[$t.Name] = $true }

$map = [ordered]@{}
$claimedBy = @{}
foreach ($t in $types) {
    $contract = $t.GetCustomAttributes([System.Runtime.Serialization.DataContractAttribute], $false)[0]
    $walked = Get-ContractMembers $t ([string]$contract.Namespace)
    $members = $walked.Members
    if (-not $members) { continue }

    $contractName = Get-ContractName $t

    # Two CLR types can claim one contract name (AxFormDataSource, abstract, and
    # AxFormDataSourceRoot, which contracts to the same name). The reader instantiates the
    # concrete one, so that is the one the name must resolve to.
    if ($claimedBy.ContainsKey($contractName)) {
        $incumbent = $claimedBy[$contractName]
        if ($incumbent.IsAbstract -and -not $t.IsAbstract) { $map.Remove($contractName) }
        else { continue }
    }
    $claimedBy[$contractName] = $t

    # The base type matters at read time for subtype resolution; it too is recorded under its
    # contract name so the chain is walkable in the vocabulary the files actually use.
    $baseName = $null
    if ($t.BaseType -and $known.ContainsKey($t.BaseType.Name)) { $baseName = Get-ContractName $t.BaseType }

    $entry = [ordered]@{
        ns       = [string]$contract.Namespace
        abstract = $t.IsAbstract
        base     = $baseName
        members  = @($members)
    }
    if ($contractName -ne $t.Name) { $entry['clr'] = $t.Name }
    if ($walked.Enums.Count -gt 0) { $entry['enumOf'] = $walked.Enums }
    if ($walked.TypeOf.Count -gt 0) { $entry['typeOf'] = $walked.TypeOf }
    $map[$contractName] = $entry
}

$enumMap = [ordered]@{}
foreach ($key in ($enums.Keys | Sort-Object)) { $enumMap[$key] = $enums[$key] }

# --- Form control containment ---------------------------------------------------------------
# AxFormValidator rejects a child control its parent does not allow ("Child form control of type
# '{0}' is not allowed for parent control of type '{1}'"). The rule lives in two static tables,
# so it is read from there rather than restated by hand.
$validationDll = Join-Path $BinPath 'Microsoft.Dynamics.AX.Metadata.Validation.dll'
if (-not (Test-Path $validationDll)) { throw "Not found: $validationDll" }
$validationAsm = [Reflection.Assembly]::LoadFrom($validationDll)
$constraint = $validationAsm.GetType('Microsoft.Dynamics.AX.Metadata.Validation.Extensions.FormParentToChildColntrolConstraint', $true)
$anyStatic = [Reflection.BindingFlags]'Public,NonPublic,Static'

function Get-ContainmentTable($fieldName) {
    $table = $constraint.GetField($fieldName, $anyStatic).GetValue($null)
    $byName = [System.Collections.Generic.SortedDictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($parent in $table.Keys) {
        $children = [System.Collections.Generic.List[string]]::new()
        foreach ($child in $table[$parent]) { $children.Add((Get-ContractName $child)) }
        $children.Sort([System.StringComparer]::Ordinal)
        $byName[(Get-ContractName $parent)] = $children.ToArray()
    }
    $result = [ordered]@{}
    foreach ($key in $byName.Keys) { $result[$key] = $byName[$key] }
    return $result
}

$allowed = Get-ContainmentTable 'ParentToAllowableChildControlTypes'
$disallowed = Get-ContainmentTable 'ParentToDisallowedChildControlTypes'

# The tables are only half the rule; how they are combined is the other half. Ask the real
# method about every pair and require the same answer from the reading D365FO.Core applies:
# an allow-list decides alone, otherwise a deny-list, otherwise anything goes. Exact types.
$isValid = $constraint.GetMethods($anyStatic) |
    Where-Object { $_.Name -eq 'IsValidChildControl' -and $_.GetParameters()[0].ParameterType -eq [type] }
$controlBase = $asm.GetType('Microsoft.Dynamics.AX.Metadata.MetaModel.AxFormControl', $true)
$controls = @($asm.GetTypes() | Where-Object {
    $_.Namespace -eq 'Microsoft.Dynamics.AX.Metadata.MetaModel' -and -not $_.IsAbstract -and $controlBase.IsAssignableFrom($_)
})
$parents = $controls + $asm.GetType('Microsoft.Dynamics.AX.Metadata.MetaModel.AxFormDesign', $true)
$checked = 0
foreach ($p in $parents) {
    $pn = Get-ContractName $p
    foreach ($c in $controls) {
        $cn = Get-ContractName $c
        $mine = if ($allowed.Contains($pn)) { $allowed[$pn] -ccontains $cn }
                elseif ($disallowed.Contains($pn)) { $disallowed[$pn] -cnotcontains $cn }
                else { $true }
        # BaseObject: reflection will not take a Type that PowerShell has wrapped in a PSObject.
        $theirs = $isValid.Invoke($null, [object[]]@($p.PSObject.BaseObject, $c.PSObject.BaseObject))
        if ($mine -ne $theirs) { throw "Containment rule disagrees with the compiler for $cn under ${pn}: tables say $mine, IsValidChildControl says $theirs." }
        $checked++
    }
}

$payload = [ordered]@{
    '$comment'    = 'Generated by scripts/emit-metadata-contracts.ps1 — do not hand-edit. Member order is the order DataContractSerializer reads and writes; anything out of order is silently dropped. enumOf maps a member to its enum; an out-of-range value fails the whole read.'
    assembly      = 'Microsoft.Dynamics.AX.Metadata.dll'
    version       = $version
    types         = $map
    enums         = $enumMap
    formContainment = [ordered]@{
        assembly   = 'Microsoft.Dynamics.AX.Metadata.Validation.dll'
        version    = $validationAsm.GetName().Version.ToString()
        allowed    = $allowed
        disallowed = $disallowed
    }
}

$dir = Split-Path $OutFile -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$json = $payload | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $dir).Path + '\' + (Split-Path $OutFile -Leaf), $json, (New-Object System.Text.UTF8Encoding($false)))

"{0} types, {1} members, {2} containment pairs verified -> {3}" -f $map.Count, ($map.Values | ForEach-Object { $_.members.Count } | Measure-Object -Sum).Sum, $checked, $OutFile
