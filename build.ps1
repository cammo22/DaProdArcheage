# Compila tutto in dist\ : DaProdServer.exe (+ server\) e DaProdLauncher.exe
param([string]$AAEmu = "..\AAEmu repository\AAEmu-client_version-zone-10.0.2_r575")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$srv  = Join-Path $dist "DaProdServer"

dotnet publish "$root\src\DaProd.ServerPanel" -c Release -o $srv
dotnet publish "$root\src\DaProd.Launcher"    -c Release -o (Join-Path $dist "DaProdLauncher")
# copia del launcher dentro il server, usata da "Pacchetto amici"
New-Item -ItemType Directory -Force "$srv\launcher" | Out-Null
Copy-Item "$dist\DaProdLauncher\DaProdLauncher.exe" "$srv\launcher\" -Force

# Login e Game (Game = contenuti/config caricati dal World)
dotnet publish "$AAEmu\AAEmu.Login\AAEmu.Login.csproj" -c Release -o "$srv\server\bin\login"
dotnet publish "$AAEmu\AAEmu.Game\AAEmu.Game.csproj"   -c Release -o "$srv\server\bin\game"

# World = logica di gioco + gestione Zone Host (va compilato con build, publish va in conflitto)
dotnet build "$AAEmu\AAEmu.WorldServer\AAEmu.World\AAEmu.World.csproj" -c Release --nologo
if (Test-Path "$srv\server\bin\world") { Remove-Item "$srv\server\bin\world" -Recurse -Force }
Copy-Item "$AAEmu\AAEmu.WorldServer\AAEmu.World\bin\Release\net10.0" "$srv\server\bin\world" -Recurse

# Zone Host: il pannello lo copia nel Bin64 del client all'avvio
New-Item -ItemType Directory -Force "$srv\server\zonehost", "$srv\server\sql", "$srv\server\bin\game\Data", "$srv\server\bin\world\Data" | Out-Null
Copy-Item "$root\..\AAEmu.ZoneHost.exe\AAEmu.ZoneHost.exe", "$root\..\AAEmu.ZoneHost.exe\x2game-dev_dedicate.dll" "$srv\server\zonehost" -Force
Copy-Item "$AAEmu\SQL\*" "$srv\server\sql" -Recurse -Force

# Database: come da guida, compact.sqlite3 del server = copia di game_decrypted.sqlite3
$decrypted = Join-Path $root "..\game_decrypted.sqlite3\game_decrypted.sqlite3"
foreach ($d in "$srv\server\bin\game\Data\compact.sqlite3", "$srv\server\bin\world\Data\compact.sqlite3", "$srv\server\zonehost\game_decrypted.sqlite3") {
    Copy-Item $decrypted $d -Force
}

Write-Host "Fatto. Server: $srv\DaProdServer.exe  Launcher: $dist\DaProdLauncher\DaProdLauncher.exe"
