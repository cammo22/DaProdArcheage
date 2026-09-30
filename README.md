# DaProd ArcheAge

Server ArcheAge privato per giocare con gli amici via internet, semplice e automatico.
Basato sull'emulatore open source [AAEmu](https://github.com/AAEmu/AAEmu). **Questa repository non contiene file del gioco.**

| Programma | Per chi | Cosa fa |
|---|---|---|
| `DaProdServer.exe` | chi ospita | Avvia da solo MySQL, Login, World e le zone; account, giocatori, rate, eventi, backup, manutenzione, aggiornamenti |
| `DaProdLauncher.exe` | chi gioca | Si collega, mostra lo stato in tempo reale, scarica solo le patch, installa DirectX, avvia il gioco |

## Come funziona la rete
```
PC amico                                        PC host
DaProdLauncher ── Tailscale portatile ────────► DaProdServer (API :8080: stato, download, accesso)
archeage.exe → 127.0.0.1:1237/1239 ───────────► AAEmu Login :1237 / World :1239 ── zone (1 processo per zona)
```
Gli amici non installano niente e non usano email: il launcher avvia un Tailscale portatile con la chiave inclusa nel pacchetto.

## Sicurezza
Il gioco entra in modalità "launcher" e il Login di AAEmu si fida del nome utente. Per questo la patch `patches/daprod-aaemu.patch`
richiede un **token firmato** (HMAC-SHA256 con scadenza) che solo il pannello rilascia, dopo aver controllato la password.
Sapere un nome utente non basta più per entrare come un altro. Tentativi di accesso sbagliati limitati (6 al minuto per IP).

## Prima installazione (nuovo PC host)
1. Scarica l'ultima release (`DaProdServer.zip`) ed estrailo.
2. Metti `DaProdGameData.7z` (il pacchetto dati) sul Desktop, in Download o accanto a `DaProdServer.exe`.
3. Apri `DaProdServer.exe`: trova il pacchetto, ti chiede conferma, installa il gioco, MySQL portatile e database del mondo,
   prepara le mappe (20-40 minuti, una volta sola) e avvia il server. È l'unica cosa che chiede.
4. **Impostazioni → TailscaleAuthKey**: chiave *Reusable + Ephemeral* da <https://login.tailscale.com/admin/settings/keys>.
5. **Server → Pacchetto amici**: manda lo zip agli amici.

Il pacchetto dati si crea sul PC dove tutto funziona con **Server → Pacchetto dati** (7-Zip: 65 GB → circa 25 GB).

## Uso quotidiano
- **Server**: stato colorato (grigio spento, giallo in caricamento, verde operativo, rosso problema, con la causa: server o PC), Manutenzione, Aggiornamenti.
- **Zone**: le zone *sempre attive* restano caricate; le altre sono **dinamiche**: si caricano da sole quando un giocatore ci entra
  (insieme alla loro regione) e si scaricano dopo alcuni minuti vuote. Si possono anche caricare/scaricare/riavviare a mano.
- **Giocatori**: chi è online, personaggi (GM, sblocco, oro, livello), account (sospendi, password).
- **Mondo**: rate (XP, loot, onore...), notizie del launcher.
- **Eventi**: azioni programmate (notizie, SQL, riavvio, backup) che compaiono nel launcher come "prossimi eventi".
- **Backup**: automatici ogni poche ore, ripristino con un clic.
- Se un servizio si ferma il pannello lo riavvia da solo. Chiudendo il pannello si chiude tutto (anche in caso di crash).

## Aggiornamenti
`Server → Aggiornamenti` scarica l'ultima release da GitHub (serve un token di sola lettura in *Impostazioni → GitHubToken* se la repo è privata).
Il launcher degli amici si aggiorna dal tuo server. Per pubblicare: `.\release.ps1 1.4.0`.

## Build da sorgente
Requisiti: .NET SDK 10, git, MySQL Server 8.4 (solo per creare il pacchetto dati), 7-Zip.
Le cartelle `AAEmu repository`, `game_decrypted.sqlite3`, `AAEmu.ZoneHost.exe` stanno accanto alla repo (come nel tuo PC).
```powershell
.\build.ps1        # applica le patch ad AAEmu e compila tutto in dist\
```

## Struttura
```
src/DaProd.ServerPanel   pannello (WinForms, .NET 10)
src/DaProd.Launcher      launcher (WinForms, .NET 10) + Tailscale portatile
patches/                 correzioni ad AAEmu (crash delle zone, token firmato, eventi per le zone dinamiche)
tools/AAPacker           libreria di ZeromusXYZ (Unlicense) per leggere game_pak
tools/PakExtract         estrae game_pak / sostituisce file nel pak
build.ps1  release.ps1   compilazione e pubblicazione
```

## Note
Progetto amatoriale per uso privato tra amici. I diritti di ArcheAge appartengono ai rispettivi proprietari; i file del gioco non fanno parte di questo progetto.
