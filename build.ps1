# Compila tutto in dist\ : DaProdServer.exe (+ server\) e DaProdLauncher.exe
# Requisiti: .NET SDK 10, git. Le cartelle del gioco/database si trovano accanto alla repo (vedi README).
param([string]$AAEmu = "..\AAEmu repository\AAEmu-client_version-zone-10.0.2_r575")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$AAEmu = (Resolve-Path (Join-Path $root $AAEmu)).Path
$dist = Join-Path $root "dist"
$srv  = Join-Path $dist "DaProdServer"

# --- 1) patch ad AAEmu (correzione dei crash delle zone + accesso con token firmato). Si applicano una volta sola.
$ErrorActionPreference = "Continue"
$patch = Join-Path $root "patches\daprod-aaemu.patch"
Push-Location $AAEmu
& git apply --check --reverse $patch 2>$null
if ($LASTEXITCODE -eq 0) { Write-Host "Patch AAEmu gia' applicate." }
else {
    & git apply $patch
    if ($LASTEXITCODE -ne 0) { throw "Impossibile applicare $patch (versione di AAEmu diversa?)" }
    Write-Host "Patch AAEmu applicate."
}
Pop-Location
$ErrorActionPreference = "Stop"

# --- 2) pannello e launcher
dotnet publish "$root\src\DaProd.ServerPanel" -c Release -o $srv
dotnet publish "$root\src\DaProd.Launcher"    -c Release -o (Join-Path $dist "DaProdLauncher")
New-Item -ItemType Directory -Force "$srv\launcher" | Out-Null
Copy-Item "$dist\DaProdLauncher\DaProdLauncher.exe" "$srv\launcher\" -Force   # copia usata da "Pacchetto amici" e dall'aggiornamento automatico

# --- 3) server: Login, Game (contenuti), World (logica + gestione zone)
dotnet publish "$AAEmu\AAEmu.Login\AAEmu.Login.csproj" -c Release -o "$srv\server\bin\login"
dotnet publish "$AAEmu\AAEmu.Game\AAEmu.Game.csproj"   -c Release -o "$srv\server\bin\game"
dotnet build   "$AAEmu\AAEmu.WorldServer\AAEmu.World\AAEmu.World.csproj" -c Release --nologo   # il World si compila con build (publish va in conflitto)
robocopy "$AAEmu\AAEmu.WorldServer\AAEmu.World\bin\Release\net10.0" "$srv\server\bin\world" /E /XF Config.Local.json /XD Data Logs /NFL /NDL /NJH /NJS | Out-Null

# --- 4) strumenti e dati che servono al server
New-Item -ItemType Directory -Force "$srv\server\zonehost", "$srv\server\sql", "$srv\server\bin\game\Data", "$srv\server\bin\world\Data", "$srv\server\tools\7z" | Out-Null
Copy-Item "$AAEmu\SQL\*" "$srv\server\sql" -Recurse -Force
dotnet build "$root\tools\PakExtract\PakExtract.csproj" -c Release -o "$srv\server\tools\PakExtract"
foreach ($f in "AAEmu.ZoneHost.exe", "x2game-dev_dedicate.dll") {
    $src = Join-Path $root "..\AAEmu.ZoneHost.exe\$f"
    if (Test-Path $src) { Copy-Item $src "$srv\server\zonehost" -Force } else { Write-Warning "$f non trovato (si trova nel pacchetto dati)." }
}
$db = Join-Path $root "..\game_decrypted.sqlite3\game_decrypted.sqlite3"
if (Test-Path $db) { Copy-Item $db "$srv\server\zonehost\game_decrypted.sqlite3" -Force } else { Write-Warning "game_decrypted.sqlite3 non trovato (si trova nel pacchetto dati)." }

# 7-Zip (LGPL) per creare/estrarre il pacchetto dati senza installare nulla
$sz = "C:\Program Files\7-Zip"
if (Test-Path "$sz\7z.exe") { Copy-Item "$sz\7z.exe", "$sz\7z.dll", "$sz\License.txt" "$srv\server\tools\7z" -Force } else { Write-Warning "7-Zip non trovato: il pacchetto dati non si potra' creare/estrarre." }

Copy-Item "$root\docs\LEGGIMI-SERVER.txt" "$srv\LEGGIMI.txt" -Force -ErrorAction SilentlyContinue
Write-Host "Fatto. Server: $srv\DaProdServer.exe  Launcher: $dist\DaProdLauncher\DaProdLauncher.exe"
