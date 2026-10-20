# Build both release packages into dist\ :
#   dist\Venquery\         Venquery.exe (self-contained single file, no .NET install needed) + definitions.json
#   dist\Venquery-Legacy\  Venquery Legacy.exe (.NET Framework 4.8, for Windows 7 / 8.1) + its DLLs
# and zip each one, with a SHA256SUMS.txt next to the zips.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1
param([switch]$NoZip)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }

Write-Host '==> Venquery (.NET 8, Windows 10/11)'
dotnet publish (Join-Path $root 'Venquery\Venquery.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o (Join-Path $dist 'Venquery')
if ($LASTEXITCODE -ne 0) { throw 'Venquery publish failed' }
Get-ChildItem (Join-Path $dist 'Venquery') -Filter *.pdb | Remove-Item -Force

Write-Host '==> Venquery Legacy (.NET Framework 4.8, Windows 7 / 8.1)'
dotnet publish (Join-Path $root 'legacy\Venquery.Legacy.csproj') -c Release -o (Join-Path $dist 'Venquery-Legacy')
if ($LASTEXITCODE -ne 0) { throw 'Legacy publish failed' }
Get-ChildItem (Join-Path $dist 'Venquery-Legacy') -Filter *.pdb | Remove-Item -Force

if ($NoZip) { return }
$ver = (Get-Item (Join-Path $dist 'Venquery\Venquery.exe')).VersionInfo.ProductVersion
foreach ($pkg in @(@('Venquery', "Venquery-$ver-win-x64.zip"), @('Venquery-Legacy', "Venquery-Legacy-$ver.zip"))) {
    $src = Join-Path $dist $pkg[0]
    Copy-Item (Join-Path $root 'LICENSE') $src
    Compress-Archive -Path (Join-Path $src '*') -DestinationPath (Join-Path $dist $pkg[1]) -Force
}
Get-ChildItem $dist -Filter *.zip | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Out-File (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Done: $dist"
