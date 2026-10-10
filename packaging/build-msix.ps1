<#
  Builds dist\ClipBridge.msix for Microsoft Store upload (the Store signs it).
  Usage:  powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1 [-Version 1.0.5.0]

  Identity comes from environment variables (set as GitHub repo variables in CI):
    STORE_IDENTITY_NAME, STORE_PUBLISHER, STORE_PUBLISHER_DISPLAY_NAME
  Without them it uses the Partner Center identity for ClipBridgePro (Store ID 9NFP2B3V0RTH).
#>
param([string]$Version = "1.0.0.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$name = if ($env:STORE_IDENTITY_NAME) { $env:STORE_IDENTITY_NAME } else { "ClipBridge.ClipBridgePro" }
$pub  = if ($env:STORE_PUBLISHER) { $env:STORE_PUBLISHER } else { "CN=BEDBD01C-E72A-47D9-9006-B7B6AC07182D" }
$pubDisplay = if ($env:STORE_PUBLISHER_DISPLAY_NAME) { $env:STORE_PUBLISHER_DISPLAY_NAME } else { "ClipBridge" }

$layout = Join-Path $root "dist\msix-layout"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null

# Plain folder publish (no single-file self-extract) is the right shape inside an MSIX.
dotnet publish -c Release -p:PublishSingleFile=false -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false -o $layout
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $layout -Filter *.pdb | Remove-Item -Force

Copy-Item (Join-Path $PSScriptRoot "Images") (Join-Path $layout "Images") -Recurse
(Get-Content (Join-Path $PSScriptRoot "AppxManifest.xml") -Raw) `
  -replace '\{\{IDENTITY_NAME\}\}', $name `
  -replace '\{\{PUBLISHER\}\}', [System.Security.SecurityElement]::Escape($pub) `
  -replace '\{\{PUBLISHER_DISPLAY_NAME\}\}', [System.Security.SecurityElement]::Escape($pubDisplay) `
  -replace '\{\{VERSION\}\}', $Version |
  Set-Content (Join-Path $layout "AppxManifest.xml") -Encoding utf8

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
  Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found. Install the Windows 10/11 SDK." }

$out = Join-Path $root "dist\ClipBridge.msix"
if (Test-Path $out) { Remove-Item $out -Force }
& $makeappx.FullName pack /d $layout /p $out /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Write-Host "Built $out (version $Version, identity $name)"
