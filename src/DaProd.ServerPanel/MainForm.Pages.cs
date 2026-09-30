using System.Data;
using System.Diagnostics;
using System.Drawing;

namespace DaProd.ServerPanel;

public sealed partial class MainForm
{
    // =====================================================================
    // Server: scheda di stato + comandi + log
    // =====================================================================
    readonly Panel _card = Ui.Buffered(new Panel { Dock = DockStyle.Top, Height = 158, Padding = new Padding(18, 10, 18, 10), BackColor = Ui.Grey });
    readonly Label _cardHead = new() { Dock = DockStyle.Top, Height = 34, Font = Ui.Huge, ForeColor = Color.White };
    readonly Label _cardAdvice = new() { Dock = DockStyle.Top, Height = 26, ForeColor = Color.White };
    readonly Dictionary<string, MiniCard> _cards = new();
    Button? _maintBtn;

    static Task Bg(Func<Task> f) => Task.Run(f);
    static Color StateColor(HealthState st) => st switch
    {
        HealthState.Operativo => Ui.Green, HealthState.Avvio => Ui.Yellow, HealthState.Problema => Ui.Red, _ => Ui.Grey
    };

    Control ServerPage()
    {
        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Btn("Avvia", Ui.Green, async () =>
        {
            if (_s.Maintenance) { MessageBox.Show("Disattiva prima la manutenzione."); return; }
            await Bg(() => _srv.StartAllAsync());
        }, 110));
        bar.Controls.Add(Ui.Btn("Ferma", Ui.Red, () => Bg(() => { _srv.StopAll(); return Task.CompletedTask; }), 110));
        bar.Controls.Add(Ui.Btn("Riavvia", Ui.Blue, async () => { await Bg(() => { _srv.StopAll(); return Task.CompletedTask; }); await Task.Delay(2000); await Bg(() => _srv.StartAllAsync()); }, 120));
        bar.Controls.Add(_maintBtn = Ui.Btn(_s.Maintenance ? "Fine manutenzione" : "Manutenzione", _s.Maintenance ? Ui.Green : Ui.Orange, ToggleMaintenance, 180));
        bar.Controls.Add(Ui.Btn("File per gli amici", Ui.Purple, ShowFriendLauncher, 170));
        bar.Controls.Add(Ui.Btn("Pacchetto dati", Ui.Grey, MakeDataPack, 150));
        bar.Controls.Add(Ui.Btn("Aggiornamenti", Ui.Grey, CheckUpdates, 150));

        var svc = Ui.Buffered(new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false });
        foreach (var n in new[] { "MySQL", "Login", "World", "Zone", "Il tuo PC" }) { var c = new MiniCard(n); _cards[n] = c; svc.Controls.Add(c); }
        _card.Controls.Add(svc); _card.Controls.Add(_cardAdvice); _card.Controls.Add(_cardHead);

        p.Controls.Add(_log); p.Controls.Add(Ui.Gap(10)); p.Controls.Add(_card); p.Controls.Add(Ui.Gap()); p.Controls.Add(bar);
        p.Controls.Add(Ui.Title("Server"));
        return p;
    }

    void ApplyHealth(HealthReport r)
    {
        var c = StateColor(r.Overall);
        if (_card.BackColor != c) _card.BackColor = c;
        if (_cardHead.Text != r.Headline) _cardHead.Text = r.Headline;
        if (_cardAdvice.Text != r.Advice) _cardAdvice.Text = r.Advice;
        foreach (var sv in r.Services)
            if (_cards.TryGetValue(sv.Name, out var mc)) mc.Set(sv.State.ToString().ToUpperInvariant(), sv.Detail, StateColor(sv.State));
        var bad = r.CpuPc > 97 || r.RamPc > 92 || r.DiskFreeGb < 5;
        if (_cards.TryGetValue("Il tuo PC", out var pc))
            pc.Set(bad ? "SOTTO SFORZO" : "OK", $"CPU {r.CpuPc:F0}% RAM {r.RamPc:F0}% Disco {r.DiskFreeGb:F0} GB", bad ? Ui.Red : Color.FromArgb(50, 54, 66));
        if (_maintBtn != null)
        {
            var t = _s.Maintenance ? "Fine manutenzione" : "Manutenzione";
            if (_maintBtn.Text != t) { _maintBtn.Text = t; _maintBtn.BackColor = _s.Maintenance ? Ui.Green : Ui.Orange; }
        }
    }

    async Task ToggleMaintenance()
    {
        if (!_s.Maintenance)
        {
            var msg = Ui.Prompt(this, "Messaggio per i giocatori:", _s.MaintenanceMessage);
            if (msg == null) return;
            _s.MaintenanceMessage = msg; _s.Maintenance = true; _s.Save();
            await Bg(() => { _srv.StopAll(); return Task.CompletedTask; }); // MySQL si ferma con il resto: si riaccende alla fine della manutenzione
            await Bg(() => _srv.StartMySqlAsync());
            Log("panel", "Manutenzione ATTIVATA.");
        }
        else
        {
            _s.Maintenance = false; _s.Save();
            Log("panel", "Manutenzione terminata: riavvio il server.");
            await Bg(() => _srv.StartAllAsync());
        }
    }

    /// <summary>Il launcher per gli amici è un solo exe, sempre aggiornato: qui si trova il file da mandare.</summary>
    async Task ShowFriendLauncher()
    {
        if (_s.UseTailscale && _s.IsNetBird && !File.Exists(PanelSettings.NetBirdExe))
            MessageBox.Show("NetBird non è installato su questo PC.\nInstallalo da https://app.netbird.io/install, accedi, poi riprova: gli amici entrano nella rete di questo PC.", "NetBird");
        else if (_s.UseTailscale && string.IsNullOrWhiteSpace(_s.VpnKey))
            MessageBox.Show(_s.IsNetBird
                ? "Manca la chiave setup di NetBird.\nSu app.netbird.io > Setup Keys creane una Reusable e senza scadenza, poi incollala in Impostazioni > NetBirdSetupKey."
                : "Manca la chiave di Tailscale: incollala in Impostazioni > TailscaleAuthKey.", "Chiave di rete");
        var exe = await Task.Run(() => FriendLauncher.EnsureBuilt(_s));
        Log("panel", $"File per gli amici: {exe} ({new FileInfo(exe).Length / 1048576} MB)");
        Process.Start("explorer.exe", $"/select,\"{exe}\"");
    }

    async Task MakeDataPack()
    {
        if (MessageBox.Show("Creo il pacchetto dati (client + database + MySQL) sul Desktop.\nCi vogliono alcuni minuti e circa 25 GB. Continuo?", "Pacchetto dati", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _srv.Activity = "Creo il pacchetto dati...";
        try
        {
            var f = await GameDataPack.CreateAsync(_s, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), t => _srv.Activity = t);
            Process.Start("explorer.exe", $"/select,\"{f}\"");
        }
        finally { _srv.Activity = ""; }
    }

    async Task CheckUpdates()
    {
        Log("panel", $"Versione attuale {Updater.Current}. Controllo GitHub ({_s.GitHubRepo})...");
        var upd = await Updater.CheckAsync(_s);
        if (upd == null) { MessageBox.Show($"Hai già l'ultima versione ({Updater.Current}).", "Aggiornamenti"); return; }
        if (MessageBox.Show($"Disponibile la versione {upd.Value.ver}. Aggiornare ora?\nIl server viene fermato e il pannello si riapre da solo.", "Aggiornamenti", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        await Updater.InstallAsync(_s, upd.Value.assetApiUrl, t => Log("update", t));
        await Task.Run(() => _srv.StopAll());
        Environment.Exit(0);
    }

    // =====================================================================
    // Zone: una riga per zona, si aggiorna sul posto
    // =====================================================================
    DataGridView? _zg;

    Control ZonesPage()
    {
        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        _zg = Ui.Grid();
        foreach (var c in new[] { "Zona", "ID", "Modo", "Stato", "Giocatori", "RAM", "Riavvii", "Attiva da" }) _zg.Columns.Add(c, c);
        _zg.Columns["Zona"]!.FillWeight = 200;
        _zg.SortCompare += (_, e) =>
        {
            // numeri e "123 MB" si confrontano come numeri, il resto come testo
            static bool Num(object? o, out double d) => double.TryParse(new string(((o as string) ?? "").TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);
            if (e.Column.Index is 1 or 4 or 5 or 6)
            {
                var ok1 = Num(e.CellValue1, out var x); var ok2 = Num(e.CellValue2, out var y);
                if (ok1 || ok2) { e.SortResult = (ok1 ? x : -1).CompareTo(ok2 ? y : -1); e.Handled = true; }
            }
        };
        string? Key() => _zg!.CurrentRow?.Cells[0].Value as string;
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Btn("Carica zona", Ui.Green, () => { if (Key() is { } k) _srv.LoadZone(k); return Task.CompletedTask; }, 140));
        bar.Controls.Add(Ui.Btn("Scarica zona", Ui.Red, () => { if (Key() is { } k) _srv.StopZone(k); return Task.CompletedTask; }, 140));
        bar.Controls.Add(Ui.Btn("Riavvia zona", Ui.Blue, async () => { if (Key() is { } k) await Bg(() => _srv.RestartZoneAsync(k)); }, 140));
        bar.Controls.Add(Ui.Btn("Riavvia le cadute", Ui.Orange, async () =>
        {
            foreach (var z in _srv.Zones.Where(z => z.Wanted && !z.Running)) { await Bg(() => _srv.RestartZoneAsync(z.Key)); await Task.Delay(2000); }
        }, 170));
        p.Controls.Add(_zg); p.Controls.Add(Ui.Gap()); p.Controls.Add(bar);
        p.Controls.Add(Ui.Hint("Zone dinamiche: quelle DORMIENTI si caricano da sole quando un giocatore ci entra (con le altre della sua regione) e si scaricano dopo alcuni minuti vuote. Riavviare una zona non scollega chi è altrove.", 44));
        p.Controls.Add(Ui.Title("Zone del mondo"));
        _refreshers["Zone"] = () => { RefreshZones(); return Task.CompletedTask; };
        RefreshZones();
        return p;
    }

    void RefreshZones()
    {
        if (_zg == null) return;
        var zones = _srv.Zones;
        // le righe si riconoscono dal nome della zona (colonna 0): l'ordine scelto dall'utente resta anche se cambiano i dati
        var byKey = new Dictionary<string, DataGridViewRow>();
        foreach (DataGridViewRow r in _zg.Rows) if (r.Cells[0].Value is string k) byKey[k] = r;
        var keys = zones.Select(z => z.Key).ToHashSet();
        foreach (var (k, r) in byKey.Where(kv => !keys.Contains(kv.Key)).ToList()) { _zg.Rows.Remove(r); byKey.Remove(k); }
        var sortCol = _zg.SortedColumn; var sortedChanged = false;
        foreach (var z in zones)
        {
            long ram = 0; try { if (z.Running) ram = z.Proc!.WorkingSet64; } catch { }
            var state = !z.Wanted && !z.Running ? "DORMIENTE" : z.GaveUp ? "ESCLUSA" : !z.Running ? "SPENTA" : z.Loaded ? "OPERATIVA" : "IN CARICAMENTO";
            string[] v = [z.Key, z.ZoneId.ToString(), z.AlwaysOn ? "sempre" : "dinamica", state, _srv.PlayersIn(z).ToString(), ram > 0 ? $"{ram / 1048576} MB" : "-", z.Restarts.ToString(),
                z.Started is { } s && z.Running ? $"{(DateTime.Now - s):hh\\:mm\\:ss}" : "-"];
            if (!byKey.TryGetValue(z.Key, out var row)) { row = _zg.Rows[_zg.Rows.Add()]; byKey[z.Key] = row; sortedChanged = true; }
            for (var c = 0; c < v.Length; c++)
                if ((string?)row.Cells[c].Value != v[c]) { row.Cells[c].Value = v[c]; if (sortCol != null && c == sortCol.Index) sortedChanged = true; }
            var col = state == "OPERATIVA" ? Ui.Green : state == "IN CARICAMENTO" ? Ui.Yellow : state == "DORMIENTE" ? Ui.Muted : Ui.Red;
            if (row.Cells[3].Style.ForeColor != col) row.Cells[3].Style.ForeColor = col;
        }
        // i valori nuovi non riordinano da soli: rimetto l'ordine scelto solo se la colonna ordinata è cambiata
        if (sortCol != null && sortedChanged) _zg.Sort(sortCol, _zg.SortOrder == SortOrder.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending);
    }

    // =====================================================================
    // Giocatori: online, personaggi (GM, sblocco, oro), account (sospensione)
    // =====================================================================
    DataGridView? _online, _chars, _accs;
    string _onlineSig = "";

    static long? SelId(DataGridView? g, string col) => g?.CurrentRow?.Cells[col].Value is { } v && v != DBNull.Value ? Convert.ToInt64(v) : null;

    Control PlayersPage()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = Ui.Normal };
        var t1 = new TabPage("Online ora") { BackColor = Ui.Bg }; var t2 = new TabPage("Personaggi") { BackColor = Ui.Bg }; var t3 = new TabPage("Account") { BackColor = Ui.Bg };
        tabs.TabPages.AddRange([t1, t2, t3]);

        _online = Ui.Grid(); t1.Controls.Add(_online);
        t1.Controls.Add(Ui.Hint("Chi è collegato adesso (l'account è una stima dall'ultimo IP di accesso). Si aggiorna da solo.", 34));

        _chars = Ui.Grid();
        var b2 = Ui.Bar();
        b2.Controls.Add(Ui.Btn("Aggiorna", Ui.Blue, RefreshChars, 110));
        b2.Controls.Add(Ui.Btn("Rendi GM", Ui.Green, () => SetCharAsync("UPDATE characters SET access_level=100 WHERE id=@i"), 120));
        b2.Controls.Add(Ui.Btn("Togli GM", Ui.Grey, () => SetCharAsync("UPDATE characters SET access_level=0 WHERE id=@i"), 120));
        b2.Controls.Add(Ui.Btn("Sblocca (Solzreed)", Ui.Orange, () => SetCharAsync("UPDATE characters SET zone_id=179, x=15578, y=15382, z=126 WHERE id=@i"), 180));
        b2.Controls.Add(Ui.Btn("Dai oro", Ui.Purple, async () =>
        {
            if (SelId(_chars, "id") is not { } id) { MessageBox.Show("Seleziona un personaggio."); return; }
            var v = Ui.Prompt(this, "Quanto oro aggiungere? (numero negativo per togliere)", "100"); if (!long.TryParse(v, out var g)) return;
            MessageBox.Show(await _srv.GiveGoldAsync(id, g * 10000), "Dai oro");
            await RefreshChars();
        }, 110));
        b2.Controls.Add(Ui.Btn("Riempi labor", Ui.Green, async () =>
        {
            if (SelId(_chars, "id") is not { } id) { MessageBox.Show("Seleziona un personaggio."); return; }
            MessageBox.Show(await Bg2(() => _srv.FillLaborAsync(id)), "Labor");
        }, 130));
        b2.Controls.Add(Ui.Btn("Punti pass", Ui.Purple, async () =>
        {
            if (SelId(_chars, "id") is not { } id) { MessageBox.Show("Seleziona un personaggio."); return; }
            var v = Ui.Prompt(this, "Quanti punti pass aggiungere?", "1000"); if (!long.TryParse(v, out var pts) || pts <= 0) return;
            MessageBox.Show(await Bg2(() => _srv.GivePassPointsAsync(id, pts)), "Punti pass");
        }, 120));
        b2.Controls.Add(Ui.Btn("Imposta livello", Ui.Purple, async () =>
        {
            var v = Ui.Prompt(this, "Nuovo livello (1-55):", "50"); if (!int.TryParse(v, out var l) || l is < 1 or > 55) return;
            await SetCharAsync("UPDATE characters SET level=@l WHERE id=@i", ("@l", l));
        }, 150));
        t2.Controls.Add(_chars); t2.Controls.Add(Ui.Gap()); t2.Controls.Add(b2);
        t2.Controls.Add(Ui.Hint("Dai oro, Riempi labor e Punti pass funzionano in tempo reale anche con il personaggio in gioco. Le altre modifiche (GM, sblocco, livello) vanno fatte con il personaggio OFFLINE, altrimenti il salvataggio del gioco le sovrascrive.", 44));

        _accs = Ui.Grid();
        var b3 = Ui.Bar();
        b3.Controls.Add(Ui.Btn("Aggiorna", Ui.Blue, RefreshAccounts, 110));
        b3.Controls.Add(Ui.Btn("Nuovo account", Ui.Green, async () =>
        {
            var u = Ui.Prompt(this, "Nome utente:"); if (string.IsNullOrWhiteSpace(u)) return;
            var pw = Ui.Prompt(this, "Password:"); if (string.IsNullOrWhiteSpace(pw)) return;
            await _srv.CreateAccountAsync(u, pw); await RefreshAccounts();
        }, 150));
        b3.Controls.Add(Ui.Btn("Cambia password", Ui.Orange, async () =>
        {
            if (SelId(_accs, "id") is not { } id) return;
            var pw = Ui.Prompt(this, "Nuova password:"); if (!string.IsNullOrWhiteSpace(pw)) await _srv.SetPasswordAsync(id, pw);
        }, 160));
        b3.Controls.Add(Ui.Btn("Sospendi / Riattiva", Ui.Red, async () =>
        {
            if (SelId(_accs, "id") is not { } id) return;
            var susp = (string?)_accs!.CurrentRow!.Cells["stato"].Value == "SOSPESO";
            await _srv.ExecAsync("aaemu_login", "UPDATE users SET banned=@b, ban_reason=@r WHERE id=@i", ("@b", susp ? 0 : 1), ("@r", susp ? "" : "Sospeso dall'amministratore"), ("@i", id));
            await RefreshAccounts();
        }, 190));
        b3.Controls.Add(Ui.Btn("Elimina", Ui.Grey, async () =>
        {
            if (SelId(_accs, "id") is not { } id) return;
            if (MessageBox.Show("Eliminare l'account?", "Conferma", MessageBoxButtons.YesNo) == DialogResult.Yes) { await _srv.DeleteAccountAsync(id); await RefreshAccounts(); }
        }, 110));
        t3.Controls.Add(_accs); t3.Controls.Add(Ui.Gap()); t3.Controls.Add(b3);
        t3.Controls.Add(Ui.Hint("Gli utenti possono anche registrarsi da soli dal launcher (Impostazioni > AllowRegistration). Un account sospeso non può più entrare.", 34));

        tabs.SelectedIndexChanged += async (_, _) => await SafeRun(async () => { if (tabs.SelectedIndex == 1) await RefreshChars(); else if (tabs.SelectedIndex == 2) await RefreshAccounts(); });
        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        p.Controls.Add(tabs); p.Controls.Add(Ui.Title("Giocatori"));
        _refreshers["Giocatori"] = async () =>
        {
            if (tabs.SelectedIndex != 0 || _online == null) return;
            var t = await Live.PlayersAsync(_s, _srv);
            var sig = string.Join("|", t.Rows.Cast<DataRow>().Select(r => string.Join(",", r.ItemArray)));
            if (sig != _onlineSig) { _onlineSig = sig; _online.DataSource = t; }
        };
        return p;
    }

    async Task SetCharAsync(string sql, params (string, object)[] extra)
    {
        if (SelId(_chars, "id") is not { } id) { MessageBox.Show("Seleziona un personaggio."); return; }
        await _srv.ExecAsync("aaemu_game", sql, [.. extra, ("@i", id)]);
        await RefreshChars();
    }

    async Task RefreshChars() =>
        _chars!.DataSource = await _srv.QueryAsync("aaemu_game",
            "SELECT c.id, c.name AS personaggio, u.username AS account, c.level AS liv, c.money DIV 10000 AS oro, c.zone_id AS zona, c.access_level AS gm, IF(u.banned=1,'SOSPESO','') AS stato " +
            "FROM characters c LEFT JOIN aaemu_login.users u ON u.id=c.account_id ORDER BY c.id");

    async Task RefreshAccounts() =>
        _accs!.DataSource = await _srv.QueryAsync("aaemu_login",
            "SELECT id, username AS utente, last_ip AS ultimo_ip, FROM_UNIXTIME(last_login) AS ultimo_accesso, IF(banned=1,'SOSPESO','') AS stato FROM users ORDER BY id");

    // =====================================================================
    // Mondo: rate, MOTD, notizie
    // =====================================================================
    Control WorldPage()
    {
        var help = new Dictionary<string, string>
        {
            ["ExpRate"] = "Esperienza (1 = normale, 100 = molto veloce)", ["LootRate"] = "Quantità di oggetti che cadono", ["GoldLootMultiplier"] = "Monete che cadono",
            ["HonorRate"] = "Punti onore", ["PvpHonorRate"] = "Punti onore PvP", ["VocationRate"] = "Punti professione", ["GrowthRate"] = "Velocità crescita di piante e animali",
            ["ActabilityRate"] = "Velocità di crescita dei mestieri", ["PlayerLevelCap"] = "Livello massimo", ["AutoSaveInterval"] = "Salvataggio automatico (minuti)",
            ["MOTD"] = "Messaggio del giorno mostrato al login"
        };
        var g = Ui.Grid(); g.ReadOnly = false;
        g.Columns.Add("Opzione", "Opzione"); g.Columns.Add("Valore", "Valore"); g.Columns.Add("Cosa fa", "Cosa fa");
        g.Columns[0].ReadOnly = true; g.Columns[2].ReadOnly = true; g.Columns[2].FillWeight = 220;
        foreach (var (k, v) in _srv.LoadRates()) g.Rows.Add(k, v, help.GetValueOrDefault(k, ""));

        var news = new TextBox { Multiline = true, Height = 70, Dock = DockStyle.Top, Text = _s.News, BackColor = Ui.Card, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle };
        void Save()
        {
            _srv.SaveRates(g.Rows.Cast<DataGridViewRow>().ToDictionary(r => (string)r.Cells[0].Value!, r => (string?)r.Cells[1].Value ?? ""));
            _s.News = news.Text; _s.Save();
        }
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Btn("Salva notizie", Ui.Blue, () => { Save(); return Task.CompletedTask; }, 150));
        bar.Controls.Add(Ui.Btn("Salva e applica (riavvia)", Ui.Orange, async () =>
        {
            Save();
            if (MessageBox.Show("Le rate si applicano riavviando il server (circa 2-3 minuti, i giocatori vengono scollegati). Riavvio ora?", "Applica", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Bg(() => { _srv.StopAll(); return Task.CompletedTask; }); await Task.Delay(2000); await Bg(() => _srv.StartAllAsync());
        }, 240));

        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        p.Controls.Add(g); p.Controls.Add(Ui.Gap()); p.Controls.Add(bar);
        p.Controls.Add(news); p.Controls.Add(Ui.Hint("Notizie mostrate nel launcher:", 26));
        p.Controls.Add(Ui.Title("Mondo: rate e notizie"));
        return p;
    }

    // =====================================================================
    // Eventi
    // =====================================================================
    readonly ListBox _evList = new() { Dock = DockStyle.Left, Width = 260, BackColor = Ui.Card, ForeColor = Ui.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11) };
    readonly PropertyGrid _evProps = new() { Dock = DockStyle.Fill };

    Control EventsPage()
    {
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Btn("Nuovo evento", Ui.Green, () => { _events.Events.Add(new GameEvent()); _events.Save(); RefreshEvents(); return Task.CompletedTask; }, 150));
        bar.Controls.Add(Ui.Btn("Esegui ora", Ui.Blue, async () => { if (_evList.SelectedItem is GameEvent e) await RunEventAsync(e); }, 130));
        bar.Controls.Add(Ui.Btn("Elimina", Ui.Red, () => { if (_evList.SelectedItem is GameEvent e) { _events.Events.Remove(e); _events.Save(); RefreshEvents(); } return Task.CompletedTask; }, 110));
        _evList.SelectedIndexChanged += (_, _) => _evProps.SelectedObject = _evList.SelectedItem;
        _evProps.PropertyValueChanged += (_, _) => { _events.Save(); RefreshEvents(); };
        var body = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        body.Controls.Add(_evProps); body.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 10 }); body.Controls.Add(_evList);
        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        p.Controls.Add(body); p.Controls.Add(Ui.Gap()); p.Controls.Add(bar);
        p.Controls.Add(Ui.Hint("News = cambia le notizie del launcher | Sql = esegue SQL sul database di gioco | RestartServer = riavvio | Backup = copia dei database. " +
                               "Daily = ogni giorno all'ora di When. Gli eventi con ShowInLauncher compaiono nel launcher come 'prossimi eventi'.", 50));
        p.Controls.Add(Ui.Title("Eventi programmati"));
        RefreshEvents();
        return p;
    }

    void RefreshEvents()
    {
        var sel = _evList.SelectedItem;
        _evList.BeginUpdate();
        _evList.DataSource = null; _evList.DataSource = _events.Events;
        _evList.EndUpdate();
        if (sel != null && _events.Events.Contains(sel)) _evList.SelectedItem = sel;
    }

    // =====================================================================
    // Backup
    // =====================================================================
    Control BackupPage()
    {
        var g = Ui.Grid();
        foreach (var c in new[] { "File", "Dimensione", "Data" }) g.Columns.Add(c, c);
        Task Refresh()
        {
            g.Rows.Clear();
            foreach (var f in _srv.Backups()) g.Rows.Add(f.Name, $"{f.Length / 1048576} MB", f.LastWriteTime.ToString("g"));
            return Task.CompletedTask;
        }
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Btn("Backup ora", Ui.Green, async () => { await _srv.BackupNowAsync(); await Refresh(); }, 140));
        bar.Controls.Add(Ui.Btn("Ripristina", Ui.Red, async () =>
        {
            if (g.CurrentRow?.Cells[0].Value is not string n) return;
            if (MessageBox.Show($"Ripristinare {n}?\nIl server viene fermato e i dati attuali (account, personaggi) vengono SOSTITUITI.", "Ripristino", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await Bg(() => _srv.RestoreAsync(Path.Combine(ServerManager.BackupDir, n)));
        }, 140));
        bar.Controls.Add(Ui.Btn("Apri cartella", Ui.Grey, () => { Directory.CreateDirectory(ServerManager.BackupDir); Process.Start("explorer.exe", ServerManager.BackupDir); return Task.CompletedTask; }, 140));
        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        p.Controls.Add(g); p.Controls.Add(Ui.Gap()); p.Controls.Add(bar);
        p.Controls.Add(Ui.Hint($"Backup automatico ogni {_s.BackupEveryHours} ore, ne tengo {_s.KeepBackups} (Impostazioni). Contiene account e personaggi.", 30));
        p.Controls.Add(Ui.Title("Backup"));
        _refreshers["Backup"] = Refresh;
        _ = Refresh();
        return p;
    }

    // =====================================================================
    // Database
    // =====================================================================
    Control SqlPage()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 130 };
        var db = new ComboBox { Dock = DockStyle.Left, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        db.Items.AddRange(["aaemu_game", "aaemu_login"]); db.SelectedIndex = 0;
        var q = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = Ui.Mono, Text = "SELECT id, name, level FROM characters LIMIT 50", BackColor = Ui.Card, ForeColor = Ui.Text, BorderStyle = BorderStyle.None };
        var grid = Ui.Grid();
        var run = Ui.Btn("Esegui", Ui.Blue, async () =>
        {
            if (q.Text.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)) grid.DataSource = await _srv.QueryAsync((string)db.SelectedItem!, q.Text);
            else MessageBox.Show($"Righe modificate: {await _srv.ExecAsync((string)db.SelectedItem!, q.Text)}");
        }, 110);
        run.Dock = DockStyle.Right;
        top.Controls.Add(q); top.Controls.Add(db); top.Controls.Add(run);
        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        p.Controls.Add(grid); p.Controls.Add(Ui.Gap(10)); p.Controls.Add(top); p.Controls.Add(Ui.Title("Database"));
        return p;
    }

    // =====================================================================
    // Impostazioni
    // =====================================================================
    Control SettingsPage()
    {
        var pg = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = _s, PropertySort = PropertySort.Categorized, ToolbarVisible = false, HelpVisible = true };
        pg.PropertyValueChanged += (_, e) =>
        {
            _s.Save();
            if (e.ChangedItem?.Label == nameof(PanelSettings.ClientDir)) _api.ResetManifest();
        };
        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        p.Controls.Add(pg);
        p.Controls.Add(Ui.Hint("Le modifiche a rete, zone e cartelle valgono dal prossimo riavvio del server.", 26));
        p.Controls.Add(Ui.Title("Impostazioni"));
        return p;
    }
}
