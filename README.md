# DaProd ArcheAge

Server ArcheAge privato per giocare con gli amici via internet, in modo semplice e automatico.
Basato sull'emulatore open source [AAEmu](https://github.com/AAEmu/AAEmu). Il repository **non contiene file del gioco**.

| Exe | Per chi | Cosa fa |
|---|---|---|
| `DaProdServer.exe` | chi ospita | Pannello: avvia MySQL + Login + Game, account, eventi, giocatori live, condivide il client |
| `DaProdLauncher.exe` | chi gioca | Si collega da solo, scarica/aggiorna il gioco, mostra lo stato live, avvia il gioco |

## Come funziona la rete
```
PC amico                                   PC host
DaProdLauncher ── Tailscale portatile ──►  DaProdServer (API :8080)
archeage.exe → 127.0.0.1:1237/1239 ───────► AAEmu Login :1237 / Game :1239
```
Gli amici **non installano niente e non inseriscono email**: il launcher avvia un Tailscale portatile
(userspace, senza admin) ed entra nella rete dell'host con la chiave inclusa nel pacchetto.

## Host: prima volta
1. Installa **MySQL Server 8.4** e **Tailscale** (accedi una volta sola, solo tu).
2. Esegui `build.ps1` (serve .NET SDK 10), poi apri `dist\DaProdServer\DaProdServer.exe`.
   Setup del database e avvio del server sono automatici.
3. **Impostazioni**:
   - `ClientDir` = cartella del client estratto
   - `TailscaleAuthKey` = chiave da <https://login.tailscale.com/admin/settings/keys> (**Reusable** + **Ephemeral**)
4. **📦 Pacchetto amici** crea `PacchettoAmici.zip`: mandalo agli amici.

## Amici
Estrai lo zip, apri `DaProdLauncher.exe`, aspetta il download, inserisci utente e password e premi **GIOCA**.
Con `AutoAccount` l'account viene creato al primo accesso.

## Pannello
- **🏠 Server**: avvia/ferma/riavvia, log live, console del Game server
- **🌐 Giocatori live**: chi è in gioco, al login o in download, dispositivi online
- **👤 Account**: crea, cambia password, elimina
- **📅 Eventi**: azioni programmate (una volta o ogni giorno): `News`, `Sql`, `ConsoleCommand`, `RestartServer`
- **🗄 Database**: query dirette
- **⚙ Impostazioni**: tutto in un'unica griglia

## Struttura
```
src/DaProd.ServerPanel   pannello (WinForms, .NET 10)
src/DaProd.Launcher      launcher (WinForms, .NET 10) + TailscaleTunnel
build.ps1                build completa in dist/ (compila anche AAEmu)
.github/workflows        build automatica degli exe
```

## Sicurezza
Chi possiede il pacchetto può entrare nella tua rete Tailscale. Condividilo solo con amici fidati
e usa le ACL di Tailscale per limitare l'accesso al solo PC server.
