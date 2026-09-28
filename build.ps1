# Compila tutto in dist\ : DaProdServer.exe (+ server\) e DaProdLauncher.exe
param([string]$AAEmu = "..\AAEmu repository\AAEmu-client_version-zone-10.0.2_r575")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$srv  = Join-Path $dist "DaProdServer"

dotnet publish "$root\src\DaProd.ServerPanel" -c Release -o $srv
dotnet publish "$root\src\DaProd.Launcher"    -c Release -o (Join-Path $dist "DaProdLauncher")

dotnet publish "$AAEmu\AAEmu.Login\AAEmu.Login.csproj" -c Release -o "$srv\server\bin\login"
dotnet publish "$AAEmu\AAEmu.Game\AAEmu.Game.csproj"   -c Release -o "$srv\server\bin\game"
New-Item -ItemType Directory -Force "$srv\server\sql" | Out-Null
Copy-Item "$AAEmu\SQL\*" "$srv\server\sql" -Recurse -Force

Write-Host "Fatto. Server: $srv\DaProdServer.exe  Launcher: $dist\DaProdLauncher\DaProdLauncher.exe"
