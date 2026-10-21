# Rebuilds Omen.CLI and copies it over the installed global tool in place -
# no pack/uninstall/install cycle, and no version bump needed. First-time
# install still goes through:  dotnet pack src/Omen.CLI -o artifacts &&
# dotnet tool install --global --add-source ./artifacts Omen.CLI
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$payload = "$env:USERPROFILE\.dotnet\tools\.store\omen.cli\1.0.0\omen.cli\1.0.0\tools\net8.0\any"
if (-not (Test-Path $payload)) { throw "Global omen tool not installed - run the dotnet tool install line above first." }

dotnet build "$repo\src\Omen.CLI" -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# The tray icon ships next to omen.exe; omen server starts it.
dotnet build "$repo\src\Omen.Tray" -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Tray build failed." }

# The GUI is launched from its Release output; a Debug-only rebuild leaves it stale.
dotnet build "$repo\src\Omen.GUI" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "GUI build failed." }

# A running omen.exe holds these files; stop it or the copy fails. Build servers idle for 30 minutes before
# exiting on their own, so ask each one to finish its builds and exit now.
$servers = Get-CimInstance Win32_Process -Filter "Name='omen.exe'" |
    Where-Object { $_.ExecutablePath -like "$env:USERPROFILE\.dotnet\tools\*" -and $_.CommandLine -match '\sserver\s+--root\s+"([^"]+)"\s+-c\s+(\S+)' }
foreach ($server in $servers) {
    if ($server.CommandLine -match '\sserver\s+--root\s+"([^"]+)"\s+-c\s+(\S+)') {
        & "$env:USERPROFILE\.dotnet\tools\omen.exe" server --stop --root $Matches[1] -c $Matches[2]
    }
}
$deadline = (Get-Date).AddMinutes(10)
while ((Get-CimInstance Win32_Process -Filter "Name='omen.exe'" | Where-Object { $_.ExecutablePath -like "$env:USERPROFILE\.dotnet\tools\*" -and $_.CommandLine -match '\sserver\s' }) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
}
# The tray is a separate process holding its own files; close it before any copy - the CLI output carries
# omen-tray.dll too (a server restarts it).
Get-Process omen-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item "$repo\src\Omen.CLI\bin\Debug\net8.0\*.dll"  $payload -Force
Copy-Item "$repo\src\Omen.CLI\bin\Debug\net8.0\*.json" $payload -Force
Copy-Item "$repo\src\Omen.Tray\bin\Debug\net8.0-windows\omen-tray.*" $payload -Force
& "$env:USERPROFILE\.dotnet\tools\omen.exe" --version
