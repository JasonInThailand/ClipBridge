<#
  ClipBridge installer.
  Usage (from the folder containing ClipBridge.exe):
     powershell -ExecutionPolicy Bypass -File .\install.ps1 [-SharedKey "my secret"] [-Peer 192.168.0.236]

  What it does:
   1. Stops any running ClipBridge.
   2. Copies ClipBridge.exe to %LocalAppData%\Programs\ClipBridge.
   3. Writes the shared key / peer address into settings.json (only if provided).
   4. Adds Windows Firewall rules for the sync ports (asks for admin once).
   5. Creates a Start Menu shortcut and starts the app (it registers itself to start with Windows).
#>
param(
    [string]$SharedKey = "",
    [string]$Peer = "",
    [switch]$NoFirewall
)
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "ClipBridge.exe"
if (-not (Test-Path $src)) { Write-Error "ClipBridge.exe not found next to install.ps1"; exit 1 }

$dest = Join-Path $env:LOCALAPPDATA "Programs\ClipBridge"
$dataDir = Join-Path $env:LOCALAPPDATA "ClipBridge"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

Write-Host "Stopping any running ClipBridge..."
Get-Process ClipBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Host "Copying to $dest"
Copy-Item $src (Join-Path $dest "ClipBridge.exe") -Force

# settings
$settingsPath = Join-Path $dataDir "settings.json"
if ($SharedKey -eq "" -and -not (Test-Path $settingsPath)) {
    Write-Host ""
    Write-Host "Sync key: both machines must use the same key. If ClipBridge is already running on your other"
    Write-Host "machine, open its Settings (tray icon > Settings) and type that key here. Leave empty to generate a new one."
    $SharedKey = Read-Host "Shared key"
}
if ($SharedKey -ne "" -or $Peer -ne "") {
    $settings = @{}
    if (Test-Path $settingsPath) {
        $obj = Get-Content $settingsPath -Raw | ConvertFrom-Json
        foreach ($p in $obj.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    }
    if ($SharedKey -ne "") { $settings["SharedKey"] = $SharedKey }
    if ($Peer -ne "") { $settings["Peers"] = @($Peer) }
    ($settings | ConvertTo-Json -Depth 5) | Out-File -Encoding utf8 $settingsPath
    Write-Host "Settings written to $settingsPath"
}

# firewall (elevated)
if (-not $NoFirewall) {
    $exe = Join-Path $dest "ClipBridge.exe"
    $fw = @"
`$ErrorActionPreference='SilentlyContinue'
Remove-NetFirewallRule -DisplayName 'ClipBridge sync (TCP)'
Remove-NetFirewallRule -DisplayName 'ClipBridge discovery (UDP)'
Remove-NetFirewallRule -DisplayName 'ClipBridge app'
# Windows creates a BLOCK rule named after the app if the "allow access?" prompt was cancelled; remove those.
Get-NetFirewallRule | Where-Object { `$_.DisplayName -like 'ClipBridge*' -or `$_.DisplayName -eq 'ClipBridge' } | Remove-NetFirewallRule
Get-NetFirewallApplicationFilter | Where-Object { `$_.Program -like '*ClipBridge.exe' } | ForEach-Object { Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter `$_ } | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'ClipBridge sync (TCP)' -Direction Inbound -Protocol TCP -LocalPort 47821 -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'ClipBridge discovery (UDP)' -Direction Inbound -Protocol UDP -LocalPort 47820 -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'ClipBridge app' -Direction Inbound -Program '$exe' -Action Allow -Profile Any | Out-Null
"@
    $tmp = Join-Path $env:TEMP "clipbridge-fw.ps1"
    $fw | Out-File -Encoding utf8 $tmp
    Write-Host "Adding firewall rules (Windows will ask for permission)..."
    try {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$tmp`""
        Write-Host "Firewall rules added."
    } catch {
        Write-Warning "Firewall rules were not added (declined?). Sync may only work in one direction. Re-run install.ps1 as admin to add them."
    }
}

# start menu shortcut
try {
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Programs")) "ClipBridge.lnk"))
    $lnk.TargetPath = Join-Path $dest "ClipBridge.exe"
    $lnk.WorkingDirectory = $dest
    $lnk.Save()
} catch { Write-Warning "Could not create Start Menu shortcut: $_" }

Write-Host "Starting ClipBridge..."
Start-Process (Join-Path $dest "ClipBridge.exe")
Write-Host ""
Write-Host "Installed. Ctrl+Alt+V opens the list, Ctrl+Alt+1..9 pastes a slot, hold Ctrl+Alt to see the HUD."
Write-Host "Data + settings live in $dataDir"
