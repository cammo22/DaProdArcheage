# DaProd ArcheAge

Progetto open source per ospitare un server ArcheAge privato tra amici, basato su [AAEmu](https://github.com/AAEmu/AAEmu).
Non contiene file del client di gioco.

## Struttura
```
src/DaProd.ServerPanel   Pannello server (DaProdServer.exe)
src/DaProd.Launcher      Launcher giocatori (DaProdLauncher.exe)
build.ps1                Compila tutto in dist/
docs/                    Guide
```

## Build
Requisiti: .NET SDK 10, MySQL Server 8.4 (non serve configurarlo come servizio).
```powershell
.\build.ps1
```

## Hostare (tu)
1. Apri `dist\DaProdServer\DaProdServer.exe`.
2. **Impostazioni**: `PublicIp` (IP pubblico o Radmin/ZeroTier), `GamePakDir` (cartella `game_pak` del client).
3. **Server → Primo setup** (una volta): crea il database MySQL e importa le tabelle.
4. **Avvia server**. Apri sul router le porte TCP 1237, 1239, 8080.

## Giocare (amici)
Apri `DaProdLauncher.exe`, scrivi `http://IP-DEL-SERVER:8080`, scegli la cartella del gioco, utente/password, **GIOCA**.
Con `AutoAccount = true` l'account viene creato al primo accesso.

## Eventi
Scheda **Eventi**: programma azioni una tantum o giornaliere:
- `News`: cambia le notizie del launcher
- `Sql`: esegue SQL sul database di gioco
- `ConsoleCommand`: comando alla console del Game server
- `RestartServer`: riavvio programmato

Gli eventi sono salvati in `data\events.json`.
