# DaProd ArcheAge

Server ArcheAge privato per giocare con gli amici via internet, semplice e automatico.
Basato sull'emulatore open source [AAEmu](https://github.com/AAEmu/AAEmu). **Questa repository non contiene file del gioco.**

| Programma | Per chi | Cosa fa |
|---|---|---|
| `DaProdServer.exe` | chi ospita | Avvia da solo MySQL, Login, World e le zone; account, giocatori, rate, eventi, backup, manutenzione, aggiornamenti |
| `DaProdLauncher-Amici.exe` | chi gioca | **Un solo file**: si collega da solo, mostra lo stato in tempo reale, scarica solo le patch, installa DirectX, avvia il gioco |

## Come funziona la rete
```
PC amico                                                  PC host
DaProdLauncher-Amici.exe ── rete privata (NetBird) ─────► DaProdServer (API :8080: stato, download, accesso)
archeage.exe → 127.0.0.1:1237/1239 ─────────────────────► AAEmu Login :1237 / World :1239 ── zone (1 processo per zona)
```
Il launcher contiene già indirizzo del server, chiave di rete e il programma della rete (NetBird portatile, in modalità
userspace con proxy SOCKS5: **niente installazione, niente amministratore, niente account per gli amici**).
Motore a scelta in *Impostazioni > VpnEngine*: **NetBird** (chiave setup che non scade mai) oppure Tailscale (chiave max 90 giorni).

## Sicurezza
Il gioco entra in modalità "launcher" e il Login di AAEmu si fida del nome utente. Per questo la patch `patches/daprod-aaemu.patch`
richiede un **token firmato** (HMAC-SHA256 con scadenza) che solo il pannello rilascia, dopo aver controllato la password.
Sapere un nome utente non basta per entrare come un altro. Tentativi di accesso sbagliati limitati (6 al minuto per IP).

## Prima installazione (nuovo PC host)
1. Scarica l'ultima release (`DaProdServer.zip`) ed estrailo.
2. Metti `DaProdGameData.7z` (il pacchetto dati) sul Desktop, in Download o accanto a `DaProdServer.exe`.
3. Apri `DaProdServer.exe`: trova il pacchetto, ti chiede conferma, installa il gioco, MySQL portatile e database del mondo,
   prepara le mappe (20-40 minuti, una volta sola) e avvia il server. È l'unica cosa che chiede.
4. Rete privata (una volta sola):
   - installa NetBird su questo PC da <https://app.netbird.io/install> e accedi (account gratuito);
   - su app.netbird.io > *Setup Keys* crea una chiave **Reusable e senza scadenza** e incollala in *Impostazioni > NetBirdSetupKey*.
5. **Server > File per gli amici**: mostra `DaProdLauncher-Amici.exe` (circa 60 MB). Mandalo con un link (Drive, Mega...).
   Il file si rifà da solo se cambiano IP o chiave, e il launcher degli amici si aggiorna dal tuo server.

Il pacchetto dati si crea sul PC dove tutto funziona con **Server > Pacchetto dati** (7-Zip: 65 GB → circa 25 GB).

## Uso quotidiano
- **Server**: stato colorato (grigio spento, giallo in caricamento, verde operativo, rosso problema, con la causa: server o PC), Manutenzione, Aggiornamenti.
- **Zone**: le zone *sempre attive* restano caricate; le altre sono **dinamiche**: si caricano da sole quando un giocatore ci entra
  (insieme alla loro regione) e si scaricano dopo alcuni minuti vuote. Si possono anche caricare/scaricare/riavviare a mano.
- **Giocatori**: chi è online, personaggi (GM, sblocco, oro, livello), account (sospendi, password).
- **Mondo**: rate (XP, loot, onore...), notizie del launcher.
- **Eventi**: azioni programmate (notizie, SQL, riavvio, backup) che compaiono nel launcher come "prossimi eventi".
- **Backup**: automatici ogni poche ore, ripristino con un clic.
- Se un servizio si ferma il pannello lo riavvia da solo. Chiudendo il pannello si chiude tutto (anche in caso di crash).

## Personalizzazione (pagina "Regole")
- **Regole live** (valgono subito, anche con i giocatori online): shop gratis e senza limiti, mercato senza commissioni, ArchePass gratis + moltiplicatore dei punti,
  velocità di recupero del labor, pass sempre visibili nel client.
- **Configurazione avanzata**: tutte le opzioni dei file `Configurations\*.json` del gioco (funzioni attive/spente come siege o premium, regole del mondo), con ricerca e ripristino.
- **Shop**: al primo avvio si carica lo shop completo (circa 25.000 oggetti divisi per scheda, biglietti ArchePass compresi). Si rigenera con `python tools/genshop.py`.
- **Giocatori > Personaggi**: oro, labor al massimo, punti pass e livello. Oro, labor e punti pass funzionano in tempo reale con il personaggio in gioco.
- **Dati del client**: il pannello crea `gioco\game\db\daprod.sqlite3` (database personalizzato) e il launcher lo carica con `+db_location`: si distribuisce con i normali aggiornamenti, senza toccare `game_pak`.
- Icone: `python tools/makeicons.py`.

## Aggiornamenti
`Server > Aggiornamenti` scarica l'ultima release da GitHub (serve un token di sola lettura in *Impostazioni > GitHubToken* se la repo è privata).
Per pubblicare: `.\release.ps1 1.4.0`.

## Build da sorgente
Requisiti: .NET SDK 10, git, MySQL Server 8.4 (solo per creare il pacchetto dati), 7-Zip.
Le cartelle `AAEmu repository`, `game_decrypted.sqlite3`, `AAEmu.ZoneHost.exe` stanno accanto alla repo (come nel PC di sviluppo).
```powershell
.\build.ps1        # applica le patch ad AAEmu e compila tutto in dist\
```

## Struttura
```
src/DaProd.ServerPanel   pannello (WinForms, .NET 10)
src/DaProd.Launcher      launcher (WinForms, .NET 10) + rete privata portatile (NetBird / Tailscale)
patches/                 correzioni ad AAEmu (crash delle zone, token firmato, eventi per le zone dinamiche)
tools/AAPacker           libreria di ZeromusXYZ (Unlicense) per leggere game_pak
tools/PakExtract         estrae game_pak / sostituisce file nel pak
build.ps1  release.ps1   compilazione e pubblicazione
```

## Note
Progetto amatoriale per uso privato tra amici. I diritti di ArcheAge appartengono ai rispettivi proprietari; i file del gioco non fanno parte di questo progetto.
NetBird e Tailscale sono distribuiti con licenza BSD-3; 7-Zip con licenza LGPL.
