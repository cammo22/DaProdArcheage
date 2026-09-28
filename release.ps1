# Crea una release GitHub con DaProdServer.zip (pannello + server compilati) e DaProdLauncher.exe.
# Uso:  .\release.ps1 1.3.0
# Il pulsante "Aggiornamenti" del pannello scarica DaProdServer.zip dall'ultima release.
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

# versione negli exe
foreach ($p in "src\DaProd.ServerPanel\DaProd.ServerPanel.csproj", "src\DaProd.Launcher\DaProd.Launcher.csproj") {
    $f = Join-Path $root $p
    (Get-Content $f -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" | Set-Content $f -NoNewline
}
& "$root\build.ps1"

# zip senza dati del gioco, database e impostazioni locali
$stage = Join-Path $env:TEMP "DaProdRelease"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
robocopy "$dist\DaProdServer" $stage /E /XD gioco mysql-data data logs PacchettoAmici /XF *.sqlite3 Config.Local.json DaProdGameData.zip PacchettoAmici.zip *.pdb | Out-Null
$zip = Join-Path $dist "DaProdServer.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive "$stage\*" $zip

git -C $root add -A
git -C $root commit -m "Release $Version" | Out-Null
git -C $root push
gh release create "v$Version" $zip "$dist\DaProdLauncher\DaProdLauncher.exe" --repo cammo22/DaProdArcheage --title "DaProd ArcheAge $Version" --notes "Aggiornamento $Version"
