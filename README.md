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
- **Giocatori > Personaggi**: anche punti onore e punti vocazione (come l'oro, in tempo reale).
- **Mercanti generici**: vendono Story Quest Infusion (rank 1-3) e Manastorm Crystal gratis. I dati si ritoccano a ogni avvio (`GameData.cs`).
- **Manastorm Shop**: il pulsante "Random Shop" dell'HUD apre il negozio Manastorm/Palos ovunque (regola `manastormShop`).
- **Daily Contract**: regola `dailyInstant` = la missione giornaliera si completa appena accettata.
- **Solo dungeon**: arene, campi di battaglia, difese ed eventi sono tolti (regola `multiInstances`); i dungeon si caricano al volo quando servono.
- **Nomi** (Regole > Nomi): cambia come il gioco chiama città, PNG, oggetti (di serie Marianople = MariaNapoli, Marilolo = Mariuolo). Il database del client parte SEMPRE da quello inglese (`zoneclient\game\db\compact_en.sqlite3`, o `..\langpatch` sul PC di sviluppo): quello coreano non va mai dato ai giocatori.
- **Launcher**: splash "Ponticheage" (`python tools/makesplash.py`), finestra adattiva (mai più grande dello schermo, in colonna se stretta). `--nosplash` la salta.
- **Release**: `release.ps1` pubblica il pannello (zip) e `DaProdLauncher-Amici.exe`, il launcher GIÀ CONFIGURATO (indirizzo, NetBird e chiave): la repo deve restare privata.
- **Giornaliere** (Regole > Missioni): i Daily Contract si chiudono da soli appena finiti gli obiettivi (`dailyAuto`), anche subito all'accettazione (`dailyInstant`); sblocco gratis; cambi missione al giorno a piacere; i premi dell'obiettivo giornaliero (Gilda Star, Giftbox...) ora arrivano davvero (`DaProdTodayGoals.cs`).
- **Infusion**: tutti gli oggetti Infusion del gioco stanno nello shop (schede in evidenza) e nei mercanti generici (lista `Resources\infusions.txt`, `python tools/geninfusions.py`). Giocatori > Dai oggetto li mette in borsa per ID.
- **Gear Upgrade (sintesi)**: i pezzi della storia (pool `live.19.07.main.*`) ora salgono davvero fino al massimo (prima restavano a Grand con la barra piena: il server leggeva i costi come "prezzo del grado che si raggiunge", il client come "costo per lasciare il grado"). **Lunafrost** (lunastone): i bonus ora contano (prima erano salvati ma mai applicati).
- **Aggiornamento automatico** di Daily Schedule e pass: finita una missione lo stato viene rimandato al client (`DaProdTodayRefreshTask`).
- **Mod dell'interfaccia** (`addons/DaProdMod`): addon del client installato dal launcher in Documenti\ArcheAge\Addon. Parla col server con una posta a "DaProd" (intercettata in `CSSendMailPacket`, comandi in `DaProdGameplay.HandleUiCommand`). Per ora: finestra "Tieni il nuovo / Ripristina il vecchio" dopo un Replace Effect.
- **Menu creativo `/dap`**: in chat `/dap` apre sul launcher una finestra con tutti gli oggetti del gioco (anteprima, ricerca, categorie) e li mette in borsa. Server: comando `Dap.cs`, API `/dap/*` in `LauncherApi.cs`, catalogo `ItemCatalog.cs`; icone in `server\catalog\icons.zip` (`tools/mkcatalogicons.py`). Il mod Lua dell'interfaccia (`addons/`) resta nel repo ma il launcher non lo installa più.
- **Teletrasporti/portali** verso una zona spenta: la zona viene richiesta e si arriva appena è pronta.
- **Gioca**: il pulsante "Gioca (apri il launcher)" del pannello apre il launcher su questo PC.
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
