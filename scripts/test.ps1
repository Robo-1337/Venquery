# Run everything that checks Venquery before a release:
#   1. the unit tests (dotnet test)
#   2. a fresh build of both packages (scripts\build.ps1 -NoZip)
#   3. the feature checks: drives the real app (Venquery, then Legacy) through its features with throwaway data
#      in test-output\FeatureTest and checks the results. It never installs, elevates or downloads.
#
#   powershell -ExecutionPolicy Bypass -File scripts\test.ps1 [-SkipUi]
param([switch]$SkipUi)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failed = 0

Write-Host '==> Unit tests'
dotnet test (Join-Path $root 'Venquery.Tests\Venquery.Tests.csproj')
if ($LASTEXITCODE -ne 0) { $failed++ }

Write-Host '==> Build'
& (Join-Path $PSScriptRoot 'build.ps1') -NoZip

if (-not $SkipUi) {
    $runs = @(
        @{ Name = 'Venquery';        Exe = (Join-Path $root 'dist\Venquery\Venquery.exe');               Dir = 'Venquery-Community' },
        @{ Name = 'Venquery Legacy'; Exe = (Join-Path $root 'dist\Venquery-Legacy\Venquery Legacy.exe'); Dir = 'Venquery-Legacy' })
    foreach ($r in $runs) {
        Write-Host "==> Feature checks: $($r.Name)"
        $res = & (Join-Path $PSScriptRoot 'feature-check.ps1') -Exe $r.Exe -SettingsDir $r.Dir
        $res | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
        $failed += @($res | Where-Object Status -eq 'FAIL').Count
    }
}

if ($failed) { Write-Host "FAILED: $failed problem(s)." -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.' -ForegroundColor Green
