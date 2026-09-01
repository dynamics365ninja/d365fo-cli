<#
.SYNOPSIS
    Proves a PUBLISHED d365fo binary behaves like an ordinary build.

.DESCRIPTION
    Issue #182: `dotnet publish -p:PublishSingleFile=true -p:PublishTrimmed=true` - the exact
    command install.ps1 and install.sh run - shipped a CLI that threw "The type initializer
    for 'Microsoft.Data.Sqlite.SqliteConnection' threw an exception", failed every Dapper
    query, and returned an EMPTY journal instead of erroring. The unit suite cannot see any
    of it: it runs against an untrimmed, non-single-file build, where all the reflection
    those paths depend on is still present.

    So this script publishes the CLI both ways and diffs them:

      baseline  - framework-dependent, untrimmed: reflection fully intact
      published - self-contained, single-file, trimmed: what users actually install

    Both get their own throwaway SQLite index seeded from tests/Samples/MiniAot, then run the
    same commands. Any difference in output that is not genuinely volatile (temp paths, ids,
    timings) fails the run. Three defect classes are additionally asserted head-on, because
    each of them once failed SILENTLY rather than loudly:

      * the journal must read back what a write put there (System.Text.Json reflection)
      * a grounding token must validate                   (provenance store round-trip)
      * the single binary must run with NO sibling files  (bundled native e_sqlite3)

.PARAMETER Rid
    Runtime identifier for the published build. Defaults to the host RID.

.PARAMETER WorkDir
    Scratch directory. Defaults to a new temp directory, removed on success.

.PARAMETER KeepWorkDir
    Keep the scratch directory (both publishes, both indexes) for debugging.
#>
[CmdletBinding()]
param(
    [string]$Rid,
    [string]$WorkDir,
    [switch]$KeepWorkDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# $IsWindows/$IsMacOS are PowerShell 7 automatic variables. Windows PowerShell 5.1 - still the
# default shell on a D365FO VM - does not define them, and StrictMode turns reading one into an
# error rather than a silent $null, so seed them before anything reads them.
if (-not (Test-Path 'variable:IsWindows')) {
    $IsWindows = $true
    $IsMacOS = $false
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot 'tests/Samples/MiniAot'
$project = Join-Path $repoRoot 'src/D365FO.Cli'

if (-not $Rid) {
    $Rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-x64' } else { 'linux-x64' }
}
if (-not $WorkDir) {
    $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('d365fo-smoke-' + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$exeName = if ($IsWindows) { 'd365fo.exe' } else { 'd365fo' }
$baselineDir = Join-Path $WorkDir 'baseline'
$publishedDir = Join-Path $WorkDir 'published'
$publishedExe = Join-Path $publishedDir $exeName
$failures = New-Object 'System.Collections.Generic.List[string]'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Add-Failure([string]$Text) { $failures.Add($Text); Write-Host "FAIL  $Text" -ForegroundColor Red }
function Write-Ok([string]$Text) { Write-Host "ok    $Text" }

function Get-Excerpt([string]$Text, [int]$Max = 220) {
    $flat = ($Text -replace '\s+', ' ').Trim()
    if ($flat.Length -le $Max) { return $flat }
    return $flat.Substring(0, $Max) + ' ...'
}

# --- publish both shapes -----------------------------------------------------

# A publish of this solution emits hundreds of doc-comment and trim-analysis warnings that
# would bury the diff below, so it is logged to a file and only replayed when it fails.
function Invoke-Publish {
    param([Parameter(Mandatory)][string]$What, [Parameter(Mandatory)][string[]]$PublishArgs)

    $log = Join-Path $WorkDir "publish-$What.log"
    & dotnet publish $project -c Release --nologo -v quiet @PublishArgs *>&1 | Out-File -FilePath $log -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        Get-Content $log | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
        throw "$What publish failed (full log: $log)"
    }
}

Write-Step 'Publishing the baseline (framework-dependent, untrimmed)'
Invoke-Publish 'baseline' @('-o', $baselineDir)

Write-Step "Publishing as installed (self-contained, single-file, trimmed, $Rid)"
Invoke-Publish 'published' @('-r', $Rid, '--self-contained',
    '-p:PublishSingleFile=true', '-p:PublishTrimmed=true', '-o', $publishedDir)
if (-not (Test-Path $publishedExe)) { throw "published build produced no $exeName" }

# --- running one command against one build -----------------------------------

function Invoke-Native {
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string[]]$ExeArgs)

    # The CLI reports failures as JSON on stderr. Windows PowerShell turns a native command's
    # stderr into an error record, which $ErrorActionPreference='Stop' would promote to a
    # terminating error - and a reported failure is exactly what this script is here to
    # compare, so it must come back as ordinary text instead of killing the run.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @ExeArgs 2>&1
    } finally {
        $ErrorActionPreference = $previous
    }

    # Those error records render with the script's own file and line number bolted on. Keep
    # only the message the CLI actually wrote.
    $lines = $output | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { $_ }
    }
    # ANSI escapes: written as [char]27 because Windows PowerShell 5.1 has no `e escape.
    return ((($lines | Out-String) -replace "$([char]27)\[[0-9;]*m", '')).Trim()
}

function Invoke-Cli {
    param([Parameter(Mandatory)][string]$Build, [Parameter(Mandatory)][string[]]$CliArgs)

    $env:D365FO_INDEX_DB = Join-Path (Join-Path $WorkDir $Build) 'index/index.sqlite'
    $env:D365FO_PACKAGES_PATH = $fixture
    $env:NO_COLOR = '1'

    if ($Build -eq 'baseline') {
        return Invoke-Native 'dotnet' (@((Join-Path $baselineDir 'd365fo.dll')) + $CliArgs)
    }
    return Invoke-Native $publishedExe $CliArgs
}

# Volatile by construction: the two builds live in different directories, mint fresh ids and
# measure their own wall-clock. Everything else must match, character for character.
$masks = @(
    @{ Pattern = '"(groundingToken|token|id|runId)":"[^"]*"'; Replacement = '"$1":"<id>"' }
    # Surrogate keys: SQLite hands out rowids in insert order, and the two indexes are
    # extracted by their own process, so FmVehicle can be table 1 in one and 2 in the other.
    @{ Pattern = '"([A-Za-z]+Id)":\s*\d+'; Replacement = '"$1":<id>' }
    @{ Pattern = '"(elapsedMs|bytes|durationMs|sizeBytes)":\s*-?\d+'; Replacement = '"$1":<num>' }
    @{ Pattern = '\d{4}-\d{2}-\d{2}[T ][0-9:.]+([+-][0-9:]+|Z)?'; Replacement = '<ts>' }
)

function Get-Normalized([string]$Text) {
    $normalized = $Text
    foreach ($form in @($WorkDir, $WorkDir.Replace('\', '\\'), $WorkDir.Replace('\', '/'))) {
        $normalized = $normalized -replace [Regex]::Escape($form), '<work>'
    }
    # ... and each build's own directory under it, so paths the CLI echoes back compare equal.
    $normalized = $normalized -replace '<work>[\\/]+(baseline|published)', '<work>/<build>'
    foreach ($mask in $masks) { $normalized = $normalized -replace $mask.Pattern, $mask.Replacement }
    return $normalized
}

# --- seed one throwaway index per build --------------------------------------

foreach ($build in @('baseline', 'published')) {
    Write-Step "Seeding the $build index from tests/Samples/MiniAot"
    New-Item -ItemType Directory -Force -Path (Join-Path (Join-Path $WorkDir $build) 'index') | Out-Null

    $built = Invoke-Cli $build @('index', 'build', '--output', 'json')
    if ($built -notmatch '"ok":\s*true') { Add-Failure "[$build] index build: $(Get-Excerpt $built)" }

    $extracted = Invoke-Cli $build @('index', 'extract', '--packages', $fixture, '--output', 'json')
    if ($extracted -notmatch '"ok":\s*true') { Add-Failure "[$build] index extract: $(Get-Excerpt $extracted)" }
    elseif ($extracted -notmatch '"tables":\s*[1-9]') { Add-Failure "[$build] index extract ingested no tables: $(Get-Excerpt $extracted)" }
}

# --- the diffed surface ------------------------------------------------------

# Every command here reads through Dapper (record materialization + anonymous-type
# parameters) or serializes an anonymous payload - the two things trimming broke.
$cases = @(
    @('models', 'list', '--output', 'json'),
    @('index', 'status', '--output', 'json'),
    @('search', 'table', 'Fm', '--output', 'json'),
    @('search', 'class', 'Fm', '--output', 'json'),
    @('search', 'any', 'Fm', '--output', 'json'),
    @('get', 'table', 'FmVehicle', '--output', 'json'),
    @('get', 'class', 'FmVehicleService', '--output', 'json'),
    @('get', 'edt', 'VinEdt', '--output', 'json'),
    @('get', 'enum', 'FmVehicleStatus', '--output', 'json'),
    @('get', 'query', 'FmVehicleQuery', '--output', 'json'),
    @('suggest', 'edt', 'Vin', '--output', 'json'),
    @('prepare', 'create', 'ConFmVehicleProbe', '--type', 'table', '--output', 'json'),
    @('prepare', 'change', 'FmVehicle', '--output', 'json'),
    @('report-integrations', '--output', 'json'),
    @('knowledge', 'list', '--output', 'json'),
    @('form-pattern', 'spec', '--output', 'json'),
    @('validate', 'name', 'table', 'ConFmVehicle', '--output', 'json')
)

Write-Step "Diffing $($cases.Count) commands, published vs baseline"
foreach ($case in $cases) {
    $label = 'd365fo ' + ($case -join ' ')
    $expected = Get-Normalized (Invoke-Cli 'baseline' $case)
    $actual = Get-Normalized (Invoke-Cli 'published' $case)

    if ($expected -ceq $actual) {
        Write-Ok $label
    } else {
        Add-Failure $label
        Write-Host "      baseline : $(Get-Excerpt $expected)"
        Write-Host "      published: $(Get-Excerpt $actual)"
    }
}

# --- the three that failed silently ------------------------------------------

Write-Step 'Grounding token round-trip (provenance store, System.Text.Json)'
foreach ($build in @('baseline', 'published')) {
    $prepared = Invoke-Cli $build @('prepare', 'create', 'ConFmVehicleProbe', '--type', 'class', '--output', 'json')
    $token = [Regex]::Match($prepared, '"groundingToken":"([^"]+)"').Groups[1].Value
    if (-not $token) {
        Add-Failure "[$build] prepare create minted no grounding token: $(Get-Excerpt $prepared)"
        continue
    }

    $outFile = Join-Path (Join-Path $WorkDir $build) 'ConFmVehicleProbe.xml'
    $generated = Invoke-Cli $build @('generate', 'class', 'ConFmVehicleProbe',
        '--grounding-token', $token, '--out', $outFile, '--overwrite', '--output', 'json')
    if ($generated -notmatch '"tokenValid":\s*true') {
        Add-Failure "[$build] the token prepare minted did not validate in generate: $(Get-Excerpt $generated)"
    } else {
        Write-Ok "[$build] token round-trip"
    }
}

Write-Step 'Journal reads back what the generate above wrote (System.Text.Json)'
foreach ($build in @('baseline', 'published')) {
    $journal = Invoke-Cli $build @('journal', 'list', '--output', 'json')
    $count = [Regex]::Match($journal, '"count":\s*(\d+)').Groups[1].Value
    if (-not $count -or [int]$count -lt 1) {
        Add-Failure "[$build] journal list came back empty after a write: $(Get-Excerpt $journal)"
    } else {
        Write-Ok "[$build] journal list -> $count entries"
    }
}

Write-Step 'The single-file binary runs with no sibling files (bundled native SQLite)'
$aloneDir = Join-Path $WorkDir 'alone'
New-Item -ItemType Directory -Force -Path $aloneDir | Out-Null
$aloneExe = Join-Path $aloneDir $exeName
Copy-Item $publishedExe $aloneExe
if (-not $IsWindows) { & chmod +x $aloneExe }

$env:D365FO_INDEX_DB = Join-Path (Join-Path $WorkDir 'published') 'index/index.sqlite'
$env:D365FO_PACKAGES_PATH = $fixture
$alone = Invoke-Native $aloneExe @('models', 'list', '--output', 'json')
if ($alone -notmatch '"ok":\s*true') {
    Add-Failure "the published binary copied on its own could not open SQLite: $(Get-Excerpt $alone)"
} else {
    Write-Ok 'single binary, empty directory'
}

# --- verdict -----------------------------------------------------------------

if ($KeepWorkDir -or $failures.Count -gt 0) {
    Write-Host ''
    Write-Host "work directory kept: $WorkDir"
} else {
    Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'A published build that differs from the baseline is a trimming/single-file defect,'
    Write-Host 'not a test bug. The knobs that keep the two in step are the roots in'
    Write-Host 'src/D365FO.Cli/TrimmerRootDescriptor.xml and the JsonSerializerIsReflectionEnabledByDefault'
    Write-Host '/ IncludeNativeLibrariesForSelfExtract properties in src/D365FO.Cli/D365FO.Cli.csproj.'
    exit 1
}

Write-Host 'published build matches the baseline on every checked command.' -ForegroundColor Green
exit 0
