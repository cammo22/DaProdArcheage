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

dotnet publish "$AAEmu\AAEmu.Login\AAEmu.Login.csproj" -c Release -o "$srv\server\bin\login"
dotnet publish "$AAEmu\AAEmu.Game\AAEmu.Game.csproj"   -c Release -o "$srv\server\bin\game"
New-Item -ItemType Directory -Force "$srv\server\sql" | Out-Null
Copy-Item "$AAEmu\SQL\*" "$srv\server\sql" -Recurse -Force

# database statico del gioco richiesto dal Game server
$compact = Join-Path $root "..\Multilingual compact.sqlite3\compact.sqlite3"
New-Item -ItemType Directory -Force "$srv\server\bin\game\Data" | Out-Null
$decrypted = Join-Path $root "..\game_decrypted.sqlite3\game_decrypted.sqlite3"
if (Test-Path $compact) {
    Copy-Item $compact "$srv\server\bin\game\Data\compact.sqlite3" -Force
    # completa i dati mancanti del compact multilingue (altrimenti il Game server non parte)
    if ((Test-Path $decrypted) -and (Get-Command python -ErrorAction SilentlyContinue)) {
        python "$root\tools\repair-compact.py" "$srv\server\bin\game\Data\compact.sqlite3" $decrypted
    }
}
else { Write-Warning "compact.sqlite3 non trovato: copialo in $srv\server\bin\game\Data" }

Write-Host "Fatto. Server: $srv\DaProdServer.exe  Launcher: $dist\DaProdLauncher\DaProdLauncher.exe"
