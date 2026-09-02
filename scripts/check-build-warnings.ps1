<#
.SYNOPSIS
    Fail the build when the compiler-warning count rises above the agreed baseline.

.DESCRIPTION
    The solution carries a backlog of warnings (mostly CS1573 missing <param> tags and
    CS8625 nullability). Turning on /warnaserror today would fail every build, and leaving
    the count unwatched is how a backlog grows quietly - a genuinely new warning is
    invisible among a hundred old ones.

    This is the middle path: the count may go DOWN freely, and any rise fails the run with
    the new warnings named. When you fix a batch, lower -Baseline in the same commit so the
    ratchet cannot slip back.

    NU* warnings are counted too, deliberately: NU1903 (a known-vulnerable transitive
    package) sat in the build output for weeks and nothing was watching.

.PARAMETER Baseline
    The highest warning count that passes. Lower it as warnings are fixed; never raise it
    to make a run go green.
#>
[CmdletBinding()]
param(
    [int]$Baseline = 114,
    [string]$Solution = "d365fo-cli.slnx",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "Building $Solution ($Configuration) to count warnings..."
# Windows PowerShell 5.1 wraps a native command's stderr in ErrorRecords, which
# $ErrorActionPreference='Stop' then turns into a terminating error even on exit 0.
# Relax it just for the build call and judge the result by $LASTEXITCODE.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$output = & dotnet build $Solution -c $Configuration --no-incremental -v q --nologo 2>&1
$buildExit = $LASTEXITCODE
$ErrorActionPreference = $previousPreference

$text = $output -join "`n"

if ($buildExit -ne 0) {
    Write-Host $text
    Write-Error "Build failed (exit $buildExit) - warning count not meaningful."
    exit 1
}

# MSBuild prints its own deduplicated total; trust that over counting lines, which
# double-counts a warning reported once per referencing project.
$match = [regex]::Match($text, '(?m)^\s*(\d+)\s+Warning\(s\)\s*$')
if (-not $match.Success) {
    Write-Error "Could not find the 'N Warning(s)' summary in the build output."
    exit 1
}

$count = [int]$match.Groups[1].Value
Write-Host "Warnings: $count (baseline $Baseline)"

if ($count -gt $Baseline) {
    Write-Host ""
    Write-Host "Warnings by code:"
    [regex]::Matches($text, 'warning ([A-Z]+\d+)') |
        ForEach-Object { $_.Groups[1].Value } |
        Group-Object |
        Sort-Object Count -Descending |
        ForEach-Object { "  {0,4}  {1}" -f $_.Count, $_.Name } |
        Write-Host

    Write-Error ("Warning count rose from the baseline of $Baseline to $count. " +
        "Fix the new warnings, or - if they are genuinely acceptable - say so in the PR " +
        "and raise -Baseline deliberately rather than as a reflex.")
    exit 1
}

if ($count -lt $Baseline) {
    Write-Host ""
    Write-Host "Warning count is BELOW the baseline. Lower -Baseline to $count in scripts/check-build-warnings.ps1 and in .github/workflows/ci.yml so the ratchet holds."
}

exit 0
