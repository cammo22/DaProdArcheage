# Crea una release GitHub: DaProdServer.zip (pannello + server compilati, SENZA gioco/database/MySQL) e DaProdLauncher.exe.
# Uso:  .\release.ps1 1.4.0
# Il pulsante "Aggiornamenti" del pannello scarica DaProdServer.zip dall'ultima release.
# Il pacchetto dati del gioco (DaProdGameData.7z, ~25 GB) NON va su GitHub: si crea dal pannello ("Pacchetto dati").
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$srv  = Join-Path $dist "DaProdServer"

# versione dentro gli exe
foreach ($p in "src\DaProd.ServerPanel\DaProd.ServerPanel.csproj", "src\DaProd.Launcher\DaProd.Launcher.csproj") {
    $f = Join-Path $root $p
    (Get-Content $f -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" | Set-Content $f -NoNewline
}
& "$root\build.ps1"

# zip: niente dati del gioco, database, MySQL, zone host, impostazioni locali, log, backup
$stage = Join-Path $env:TEMP "DaProdRelease"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
robocopy $srv $stage /E /XD gioco zoneclient mysql-data mysql zonehost data logs backups PacchettoAmici /XF *.sqlite3 Config.Local.json DaProdGameData.7z PacchettoAmici.zip *.pdb /NFL /NDL /NJH /NJS | Out-Null
$zip = Join-Path $dist "DaProdServer.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive "$stage\*" $zip
"{0:N0} MB" -f ((Get-Item $zip).Length / 1MB) | Write-Host

# git scrive avvisi su stderr: non devono fermare lo script
$ErrorActionPreference = "Continue"
git -C $root add -A
git -C $root commit -m "Release $Version" | Out-Null
git -C $root push
gh release create "v$Version" $zip "$dist\DaProdLauncher\DaProdLauncher.exe" --repo cammo22/DaProdArcheage --title "DaProd ArcheAge $Version" --generate-notes
