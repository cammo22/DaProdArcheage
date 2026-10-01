using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;

namespace DaProd.Launcher;

public sealed class LauncherForm : Form
{
    readonly LauncherSettings _s = LauncherSettings.Load();
    HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    HttpClient _dl = new() { Timeout = Timeout.InfiniteTimeSpan };
    GameUpdater _updater;
    VpnTunnel? _tunnel;
    ServerInfo? _info;
    GameUpdater.Plan? _plan;
    CancellationTokenSource? _cts;
    bool _busy, _ready, _register, _gameRunning, _downloading, _firstConnect = true;
    int _pingMs;
    static readonly CultureInfo It = new("it-IT");

    // interfaccia
    readonly Label _title = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Gold, Text = "Ponticheage" };
    readonly Label _subtitle = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
    readonly Chip _cStatus = new() { Width = 150 }, _cPlayers = new() { Width = 130 }, _cZones = new() { Width = 110 }, _cPing = new() { Width = 90 };
    readonly Label _news = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Normal, Padding = new Padding(16, 6, 16, 6), BackColor = Theme.Card };
    readonly Label _events = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Normal, Padding = new Padding(16, 2, 16, 6), BackColor = Theme.Card };
    readonly TextBox _user = Field(), _pass = Field(true), _pass2 = Field(true);
    readonly CheckBox _remember = new() { Text = "Ricorda credenziali", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Small };
    readonly RoundButton _play = new() { Fill = Theme.Green, Font = Theme.Play, Height = 54 };
    readonly LinkLabel _switch = new() { AutoSize = true, LinkColor = Theme.Gold, ActiveLinkColor = Color.White, Font = Theme.Small };
    readonly Label _msg = new() { Height = 64, ForeColor = Theme.Muted, Font = Theme.Small };
    readonly Label _gameInfo = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
    readonly SlimBar _bar = new() { Dock = DockStyle.Top, Height = 8, Maximum = 1000 };
    readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Small };
    readonly Label _speed = new() { Dock = DockStyle.Right, AutoSize = true, ForeColor = Theme.Gold, TextAlign = ContentAlignment.MiddleRight, Font = Theme.Small };
    readonly Panel _dxBanner = new() { Dock = DockStyle.Top, Height = 0, BackColor = Color.FromArgb(120, 70, 20), Visible = false };
    readonly RoundButton _selfUpdate = new() { Fill = Theme.Orange, Text = "Aggiorna launcher", Width = 150, Height = 30, Visible = false, Font = Theme.Small };
    readonly LinkLabel _cancel = new() { Text = "Annulla download", AutoSize = true, LinkColor = Theme.Red, Font = Theme.Small, Visible = false };
    // disposizione adattiva: a destra l'accesso e a sinistra le notizie; con la finestra stretta tutto in colonna e scorrevole
    readonly Panel _body = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 6, 28, 6) };
    readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty };
    Control? _left, _loginCard;
    bool _narrow, _laidOut;
    long _lastDone; DateTime _lastTick = DateTime.Now; double _speedEma;

    static TextBox Field(bool pwd = false) => new() { BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11), UseSystemPasswordChar = pwd, Dock = DockStyle.Top };

    public LauncherForm()
    {
        _updater = new GameUpdater(() => _dl, Api, _s.GameDir);
        Text = "Ponticheage Launcher";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 620); MinimumSize = new Size(600, 440);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Normal; DoubleBuffered = true;

        // ---- intestazione
        var header = new Panel { Dock = DockStyle.Top, Height = 86, Padding = new Padding(28, 14, 28, 0) };
        var titles = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        titles.Controls.AddRange([_title, _subtitle]);
        var chips = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 18, 0, 0), WrapContents = false };
        chips.Controls.AddRange([_cPing, _cZones, _cPlayers, _cStatus]);
        header.Controls.Add(titles); header.Controls.Add(chips);

        // ---- banner DirectX
        var dxText = new Label { Text = "Manca DirectX 9: senza, il gioco non parte (errore CryRenderD3D9.dll).", Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(28, 0, 0, 0) };
        var dxBtn = new RoundButton { Text = "Installa DirectX", Fill = Theme.Green, Dock = DockStyle.Right, Width = 170 };
        dxBtn.Click += async (_, _) => await Run(InstallDx);
        _dxBanner.Controls.Add(dxText); _dxBanner.Controls.Add(dxBtn);

        // ---- piè di pagina
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(28, 4, 28, 8) };
        var line = new Panel { Dock = DockStyle.Fill };
        _cancel.Location = new Point(0, 30); line.Controls.Add(_status); line.Controls.Add(_speed);
        footer.Controls.Add(line); footer.Controls.Add(_bar);
        _bar.Margin = new Padding(0);

        // ---- colonna sinistra: notizie + eventi
        var left = new Panel { Dock = DockStyle.Fill };
        var newsCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(2, 34, 2, 4) };
        newsCard.Controls.Add(_news);
        newsCard.Controls.Add(new Label { Text = "NOTIZIE", Font = Theme.Section, ForeColor = Theme.Gold, AutoSize = true, Location = new Point(16, 12), BackColor = Theme.Card });
        var evCard = new Card { Dock = DockStyle.Bottom, Height = 170, Padding = new Padding(2, 34, 2, 4) };
        evCard.Controls.Add(_events);
        evCard.Controls.Add(new Label { Text = "PROSSIMI EVENTI", Font = Theme.Section, ForeColor = Theme.Gold, AutoSize = true, Location = new Point(16, 12), BackColor = Theme.Card });
        left.Controls.Add(newsCard); left.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 14 }); left.Controls.Add(evCard);

        // ---- colonna destra: accesso
        var login = new Card { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 14), Margin = new Padding(16, 0, 0, 0) };
        var lf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card };
        Control Lbl(string t) => new Label { Text = t, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Card, Margin = new Padding(0, 8, 0, 2) };
        _user.Dock = _pass.Dock = _pass2.Dock = DockStyle.None; _user.Width = _pass.Width = _pass2.Width = 268;
        _play.Width = 268; _msg.Width = 268;
        _pass2.Visible = false;
        var passLbl2 = Lbl("Ripeti password"); passLbl2.Visible = false;
        _user.Text = _s.Username; _remember.Checked = _s.Remember; if (_s.Remember) _pass.Text = _s.GetPassword();
        _remember.BackColor = Theme.Card; _switch.BackColor = Theme.Card; _msg.BackColor = Theme.Card; _gameInfo.BackColor = Theme.Card; _cancel.BackColor = Theme.Card;
        lf.Controls.AddRange([new Label { Text = "ACCEDI", Font = Theme.Section, ForeColor = Theme.Gold, AutoSize = true, BackColor = Theme.Card },
            Lbl("Utente"), _user, Lbl("Password"), _pass, passLbl2, _pass2, new Panel { Height = 6, Width = 1, BackColor = Theme.Card }, _remember,
            new Panel { Height = 8, Width = 1, BackColor = Theme.Card }, _play, new Panel { Height = 4, Width = 1, BackColor = Theme.Card }, _switch, _msg, _gameInfo, _cancel]);
        login.Controls.Add(lf);
        _switch.Click += (_, _) => { _register = !_register; ApplyMode(passLbl2); };
        _switch.Text = "Crea un nuovo account";
        _play.Click += async (_, _) => await Run(_register ? RegisterAsync : PlayAsync);
        _pass.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !_register) { e.SuppressKeyPress = true; await Run(PlayAsync); } };
        _cancel.Click += (_, _) => _cts?.Cancel();

        _left = left; _loginCard = login;
        left.Margin = Padding.Empty;
        _body.AutoScroll = false; _body.HorizontalScroll.Enabled = false; _body.HorizontalScroll.Visible = false; _body.HorizontalScroll.Maximum = 0; _body.AutoScroll = true;
        _body.Controls.Add(_grid);

        // ---- link in basso a destra
        var links = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 0, 28, 0), WrapContents = true };
        LinkLabel Link(string t, Action a) { var l = new LinkLabel { Text = t, AutoSize = true, LinkColor = Theme.Muted, ActiveLinkColor = Color.White, Font = Theme.Small, Margin = new Padding(14, 2, 0, 0) }; l.Click += (_, _) => a(); return l; }
        links.Controls.Add(Link("Cambia server", ChangeServer));
        links.Controls.Add(Link("Apri cartella di gioco", () => { Directory.CreateDirectory(_s.GameDir); Process.Start("explorer.exe", _s.GameDir); }));
        links.Controls.Add(Link("Cerca aggiornamenti", async () => await Run(UpdateAllAsync)));
        links.Controls.Add(Link("Indica archeage.exe", PickGameExe));
        links.Controls.Add(Link("Ripara file", async () => await Run(RepairAsync)));
        links.Controls.Add(_selfUpdate);
        _selfUpdate.Click += async (_, _) => await Run(SelfUpdate);

        Controls.Add(_body); Controls.Add(links); Controls.Add(_dxBanner); Controls.Add(header); Controls.Add(footer);

        ApplyMode(passLbl2);
        UpdateChips();
        AddonInstaller.Install();
        Relayout();
        SizeChanged += (_, _) => Relayout();
        Shown += async (_, _) => { FitToScreen(); Relayout(); _user.SelectionStart = _user.TextLength; _user.SelectionLength = 0; if (!Program.TestMode) await Run(Connect); };
        FormClosed += (_, _) => _tunnel?.Dispose();
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += async (_, _) => await OnTick();
        t.Start();
    }

    /// <summary>La finestra non deve mai essere più grande dello schermo (con la scala di Windows al 150% lo era): i pulsanti restano sempre raggiungibili.</summary>
    void FitToScreen()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width - 20), Math.Min(MinimumSize.Height, wa.Height - 20));
        var w = Math.Min(Width, wa.Width - 20); var h = Math.Min(Height, wa.Height - 20);
        if (w != Width || h != Height) { Size = new Size(w, h); Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2); }
    }

    /// <summary>Largo: notizie a sinistra e accesso a destra. Stretto: accesso sopra e notizie sotto, con barra di scorrimento.</summary>
    void Relayout()
    {
        if (_left == null || _loginCard == null) return;
        var narrow = ClientSize.Width < LogicalToDeviceUnits(780);
        var loginH = LogicalToDeviceUnits(_register ? 470 : 400);
        var fw = Math.Clamp(narrow ? ClientSize.Width - LogicalToDeviceUnits(100) : LogicalToDeviceUnits(268), LogicalToDeviceUnits(200), LogicalToDeviceUnits(420));
        _user.Width = _pass.Width = _pass2.Width = _play.Width = _msg.Width = fw;
        _cPing.Visible = _cZones.Visible = !narrow;
        _body.AutoScrollMinSize = new Size(0, narrow ? loginH + LogicalToDeviceUnits(380) : loginH);
        if (_laidOut && narrow == _narrow) return;
        _laidOut = true; _narrow = narrow;
        _grid.SuspendLayout();
        _grid.Controls.Clear(); _grid.ColumnStyles.Clear(); _grid.RowStyles.Clear();
        if (!narrow)
        {
            _grid.ColumnCount = 2; _grid.RowCount = 1;
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(316 + 16)));
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _loginCard.Margin = new Padding(LogicalToDeviceUnits(16), 0, 0, 0);
            _grid.Controls.Add(_left, 0, 0); _grid.Controls.Add(_loginCard, 1, 0);
        }
        else
        {
            _grid.ColumnCount = 1; _grid.RowCount = 2;
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _grid.RowStyles.Add(new RowStyle(SizeType.Absolute, loginH));
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _loginCard.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(12));
            _grid.Controls.Add(_loginCard, 0, 0); _grid.Controls.Add(_left, 0, 1);
        }
        _grid.ResumeLayout(true);
    }

    string Api(string path) => _s.ServerUrl.TrimEnd('/') + path;
    void SetStatus(string t) { if (_status.Text != t) _status.Text = t; }
    void Msg(string t, bool error = false) { if (_msg.Text != t) _msg.Text = t; var c = error ? Theme.Red : Theme.Muted; if (_msg.ForeColor != c) _msg.ForeColor = c; }
    static void SetText(Label l, string t) { if (l.Text != t) l.Text = t; }

    async Task Run(Func<Task> f)
    {
        if (_busy) return;
        _busy = true; UpdatePlayButton();
        try { await f(); }
        catch (Exception ex) { SetStatus("Errore: " + ex.Message); Msg(ex.Message, true); }
        finally { _busy = false; _downloading = false; _cancel.Visible = false; UpdatePlayButton(); }
    }

    void ApplyMode(Control passLbl2)
    {
        _pass2.Visible = passLbl2.Visible = _register;
        Relayout();
        _switch.Text = _register ? "Ho già un account" : "Crea un nuovo account";
        _msg.Text = "";
        UpdatePlayButton();
    }

    void UpdatePlayButton()
    {
        if (_register) { _play.Fill = Theme.Blue; _play.Text = "CREA ACCOUNT"; _play.Enabled = !_busy && _info is { registration: true }; _play.Invalidate(); return; }
        _play.Fill = Theme.Green;
        _play.Text = _gameRunning ? "IN GIOCO" : _downloading ? "SCARICO..." : _busy ? "ATTENDI..." : !_ready ? "SERVER NON PRONTO" : "GIOCA";
        _play.Enabled = !_busy && _ready && !_gameRunning;
        _play.Invalidate();
    }

    // ------------------------------------------------------------------ collegamento
    async Task Connect()
    {
        SetStatus("Collego la rete privata...");
        await EnsureVpn();
        SetStatus("Contatto il server...");
        await RefreshInfo();
        if (_info != null) await CheckPatch();
        CheckDx();
    }

    /// <summary>Se il server è su un altro PC, entra nella sua rete privata (NetBird o Tailscale portatile) senza chiedere nulla all'utente.</summary>
    async Task EnsureVpn()
    {
        var host = new Uri(_s.ServerUrl).Host;
        if (_tunnel != null || string.IsNullOrWhiteSpace(_s.VpnKey) || VpnTunnel.IsLocal(host)) return;
        await Task.Run(Trailer.ExtractTools);
        var t = VpnTunnel.Create(_s.Vpn);
        if (t == null) return;
        try { await t.StartAsync(_s.VpnKey, SetStatus); }
        catch { t.Dispose(); throw; }
        _tunnel = t;
        _http = new HttpClient(new HttpClientHandler { Proxy = t.Proxy, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };
        _dl = new HttpClient(new HttpClientHandler { Proxy = t.Proxy, UseProxy = true }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    async Task RefreshInfo()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var info = await _http.GetFromJsonAsync<ServerInfo>(Api("/launcher.json"));
            _pingMs = (int)sw.ElapsedMilliseconds;
            var wasDown = _info == null;
            _info = info;
            SetText(_subtitle, $"{info!.name}  -  v{MyVersion}");
            _ready = !info.maintenance && info.live?.ready == true;
            SetText(_news, info.maintenance ? (info.maintenanceMessage ?? "Manutenzione in corso.") + "\n\n" + info.news
                : !_ready ? "Il server si sta avviando: riprovo in automatico.\n" + (info.live?.status ?? "") + "\n\n" + info.news : info.news);
            SetText(_events, info.events is { Count: > 0 } ev ? string.Join("\n", ev.Select(e => $"•  {e.name}   -   {Rel(DateTime.Parse(e.at))}")) : "Nessun evento in programma.");
            _selfUpdate.Visible = Version.TryParse(info.launcherVersion, out var v) && v > MyVersion;
            if (wasDown && !_firstConnect && !_busy) await CheckPatch();
        }
        catch { _info = null; _ready = false; _pingMs = 0; }
        _firstConnect = false;
        UpdateChips(); UpdatePlayButton();
    }

    static string Rel(DateTime at)
    {
        var d = at - DateTime.Now;
        if (d < TimeSpan.Zero) return "in corso";
        if (at.Date == DateTime.Today) return $"oggi alle {at:HH:mm}";
        if (at.Date == DateTime.Today.AddDays(1)) return $"domani alle {at:HH:mm}";
        return at.ToString("ddd d MMM 'alle' HH:mm", It);
    }

    void UpdateChips()
    {
        var i = _info; var l = i?.live;
        if (i == null) _cStatus.Set("OFFLINE", Theme.Red);
        else if (i.maintenance) _cStatus.Set("MANUTENZIONE", Theme.Orange);
        else if (_ready) _cStatus.Set("ONLINE", Theme.Green);
        else _cStatus.Set("IN AVVIO", Theme.Orange);
        _cPlayers.Set(l == null ? "- giocatori" : $"{l.players} in gioco", Theme.Grey);
        _cZones.Set(l == null ? "zone -" : $"zone {l.zonesLoaded}/{l.zonesTotal}", l != null && l.zonesLoaded >= l.zonesTotal && l.zonesTotal > 0 ? Theme.Grey : Theme.Orange);
        _cPing.Set(_pingMs > 0 ? $"{_pingMs} ms" : "- ms", Theme.Grey);
    }

    static Version MyVersion => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new(0, 0);

    // ------------------------------------------------------------------ aggiornamenti gioco
    async Task CheckPatch()
    {
        try
        {
            _plan = await _updater.CheckAsync();
            var mb = _plan.Bytes / 1048576.0;
            _gameInfo.Text = _plan.TotalFiles == 0 ? "Il server non condivide il gioco."
                : _plan.Todo.Count == 0 ? "Gioco aggiornato."
                : _plan.FirstInstall ? $"Da installare: {mb / 1024:F1} GB (parte con GIOCA)"
                : $"Aggiornamento: {(mb > 1024 ? $"{mb / 1024:F1} GB" : $"{mb:F0} MB")} (parte con GIOCA)";
            _gameInfo.ForeColor = _plan.Todo.Count == 0 ? Theme.Green : Theme.Gold;
            SetStatus("Pronto.");
        }
        catch (Exception ex) { _gameInfo.Text = "Controllo aggiornamenti non riuscito."; SetStatus(ex.Message); }
    }

    async Task Download(GameUpdater.Plan plan)
    {
        _downloading = true; _cancel.Visible = true; _lastDone = 0; _speedEma = 0; _lastTick = DateTime.Now; UpdatePlayButton();
        _cts = new CancellationTokenSource();
        try { await _updater.DownloadAsync(plan, _cts.Token); }
        catch (OperationCanceledException) { SetStatus("Download annullato: riprenderà da dove era."); throw new Exception("Download annullato."); }
        finally { _downloading = false; _cancel.Visible = false; _bar.Value = 0; _speed.Text = ""; }
        _gameInfo.Text = "Gioco aggiornato."; _gameInfo.ForeColor = Theme.Green;
    }

    /// <summary>Per chi ha già il gioco scaricato: si indica dove sta archeage.exe e il launcher usa quella cartella (e la aggiorna).</summary>
    void PickGameExe()
    {
        using var f = new OpenFileDialog { Title = "Indica archeage.exe (di solito nella cartella Bin64)", Filter = "archeage.exe|archeage.exe", CheckFileExists = true };
        if (f.ShowDialog(this) != DialogResult.OK) return;
        var dir = Path.GetDirectoryName(f.FileName)!;
        var name = Path.GetFileName(dir);
        // il gioco è la cartella che contiene Bin64 (e game_pak): archeage.exe sta dentro Bin64 o bin32
        var root = name.Equals("Bin64", StringComparison.OrdinalIgnoreCase) || name.Equals("bin32", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir)! : dir;
        _s.GameDir = root; _s.Save();
        _updater = new GameUpdater(() => _dl, Api, _s.GameDir);
        Msg("Cartella del gioco: " + root);
        _ = Run(CheckPatch);
    }

    /// <summary>Un solo tasto: nuovo launcher (si riavvia da solo) e file del gioco/database, che il server pubblica e aggiorna.</summary>
    async Task UpdateAllAsync()
    {
        SetStatus("Cerco aggiornamenti...");
        await RefreshInfo();
        if (_info == null) { Msg("Server non raggiungibile: riprova tra poco.", true); return; }
        if (_selfUpdate.Visible) { await SelfUpdate(); return; }
        var plan = await _updater.CheckAsync();
        _plan = plan;
        if (plan.Todo.Count == 0) { SetStatus("Tutto aggiornato."); _gameInfo.Text = "Gioco aggiornato."; _gameInfo.ForeColor = Theme.Green; return; }
        await Download(plan);
        SetStatus("Aggiornamento completato.");
    }

    async Task RepairAsync()
    {
        if (MessageBox.Show("Controllo i file del gioco e riscarico quelli danneggiati o mancanti (esclusi i pacchetti enormi, verificati solo per dimensione).\nContinuo?", "Ripara", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        SetStatus("Controllo i file...");
        var plan = await _updater.CheckAsync(repair: true);
        if (plan.Todo.Count == 0) { SetStatus("Tutti i file sono a posto."); return; }
        await Download(plan);
        SetStatus("Riparazione completata.");
    }

    // ------------------------------------------------------------------ DirectX
    void CheckDx()
    {
        var need = !DirectX.Installed;
        _dxBanner.Visible = need; _dxBanner.Height = need ? 46 : 0;
    }

    async Task InstallDx()
    {
        try { await DirectX.InstallAsync(SetStatus); }
        catch (Exception ex) { Msg($"Installazione automatica fallita ({ex.Message}). Scarica DirectX da: {DirectX.ManualPage}", true); }
        CheckDx();
        SetStatus(DirectX.Installed ? "DirectX installato." : "DirectX non risulta installato.");
    }

    // ------------------------------------------------------------------ accesso e gioco
    async Task<AuthReply> Auth(string path, string user, string pwd)
    {
        var resp = await _http.PostAsJsonAsync(Api(path), new { user, password = pwd });
        return await resp.Content.ReadFromJsonAsync<AuthReply>() ?? new AuthReply(false, "Risposta non valida dal server.", null);
    }

    void SaveLogin()
    {
        _s.Username = _user.Text.Trim(); _s.Remember = _remember.Checked;
        _s.SetPassword(_remember.Checked ? _pass.Text : ""); _s.Save();
    }

    async Task RegisterAsync()
    {
        var user = _user.Text.Trim();
        if (_pass.Text != _pass2.Text) { Msg("Le password non coincidono.", true); return; }
        Msg("Creo l'account...");
        var r = await Auth("/register", user, _pass.Text);
        Msg(r.message ?? "", !r.ok);
        if (!r.ok) return;
        SaveLogin(); _register = false; ApplyMode(_pass2.Parent!.Controls.OfType<Label>().First(l => l.Text == "Ripeti password"));
        Msg("Account creato! Premi GIOCA.");
    }

    async Task PlayAsync()
    {
        if (_info == null || !_ready) { Msg("Il server non è ancora pronto.", true); return; }
        var user = _user.Text.Trim();
        if (user == "" || _pass.Text == "") { Msg("Inserisci utente e password.", true); return; }
        Msg("Verifico l'account...");
        var r = await Auth("/login", user, _pass.Text);
        if (!r.ok || r.token == null) { Msg(r.message ?? "Accesso rifiutato.", true); return; }
        SaveLogin(); Msg("");

        if (!DirectX.Installed)
        {
            if (MessageBox.Show("Al gioco serve DirectX 9 (componente ufficiale Microsoft, circa 100 MB).\nLo installo ora?", "DirectX", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await InstallDx();
            if (!DirectX.Installed) return;
        }

        SetStatus("Controllo gli aggiornamenti...");
        var plan = await _updater.CheckAsync();
        if (plan.Todo.Count > 0) await Download(plan);

        var exe = Directory.Exists(_s.GameDir) ? Directory.EnumerateFiles(_s.GameDir, "archeage.exe", SearchOption.AllDirectories)
            .OrderBy(p => p.Contains("bin32", StringComparison.OrdinalIgnoreCase) ? 1 : 0).FirstOrDefault() : null;
        if (exe == null) { Msg("archeage.exe non trovato: prova 'Ripara file'.", true); return; }

        var ip = _info.ip;
        if (_tunnel != null)
        {
            // il client si collega a localhost, il tunnel porta tutto al server
            _tunnel.Forward(ip, _info.port, _info.gamePort, _info.streamPort);
            ip = "127.0.0.1";
        }
        var args = _info.launchArgs.Replace("{ip}", ip).Replace("{port}", _info.port.ToString()).Replace("{user}", user).Replace("{token}", r.token);
        // database personalizzato del server (pass, testi...): viaggia con gli aggiornamenti, il client lo carica con db_location
        if (File.Exists(Path.Combine(_s.GameDir, "game", "db", "daprod.sqlite3")) && !args.Contains("db_location"))
            args += " +db_location game/db/daprod.sqlite3";
        AddonInstaller.Install(); // pulsanti e finestre in più del mod dell'interfaccia
        var p = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetDirectoryName(exe)! });
        if (p == null) { Msg("Impossibile avviare il gioco.", true); return; }
        _gameRunning = true; p.EnableRaisingEvents = true;
        p.Exited += (_, _) => BeginInvoke(() => { _gameRunning = false; WindowState = FormWindowState.Normal; UpdatePlayButton(); SetStatus("Gioco chiuso."); });
        SetStatus("Gioco avviato. Buon divertimento!");
        WindowState = FormWindowState.Minimized;
    }

    // ------------------------------------------------------------------ varie
    async Task SelfUpdate()
    {
        SetStatus("Scarico il nuovo launcher...");
        var me = Environment.ProcessPath!;
        var tmp = Path.Combine(Path.GetTempPath(), "DaProdLauncher.new.exe");
        await File.WriteAllBytesAsync(tmp, await _dl.GetByteArrayAsync(Api("/launcher.exe")));
        var cmd = Path.Combine(Path.GetTempPath(), "daprod-launcher-update.cmd");
        File.WriteAllText(cmd, $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\ncopy /y \"{tmp}\" \"{me}\" >nul\r\nstart \"\" \"{me}\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        Application.Exit();
    }

    void ChangeServer()
    {
        using var f = new Form { Text = "Indirizzo del server", ClientSize = new Size(440, 100), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Bg, Font = Theme.Normal };
        var tb = new TextBox { Left = 14, Top = 16, Width = 412, Text = _s.ServerUrl, BackColor = Theme.Field, ForeColor = Theme.Text };
        var ok = new RoundButton { Text = "OK", Left = 326, Top = 52, Width = 100, Height = 34, DialogResult = DialogResult.OK };
        f.Controls.AddRange([tb, ok]); f.AcceptButton = ok;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _s.ServerUrl = tb.Text.Trim(); _s.Save(); _info = null; _ready = false;
        _ = Run(Connect);
    }

    /// <summary>Ogni mezzo secondo: progresso del download; ogni 5 secondi: stato del server.</summary>
    int _n;
    async Task OnTick()
    {
        if (_downloading)
        {
            var done = _updater.Done; var total = Math.Max(1, _updater.Total);
            var now = DateTime.Now; var dt = (now - _lastTick).TotalSeconds;
            if (dt >= 0.5)
            {
                var inst = Math.Max(0, (done - _lastDone) / dt);
                _speedEma = _speedEma == 0 ? inst : _speedEma * 0.8 + inst * 0.2;
                _lastDone = done; _lastTick = now;
            }
            var v = (int)Math.Min(1000, done * 1000 / total);
            if (_bar.Value != v) _bar.Value = v;
            var eta = _speedEma > 1 ? TimeSpan.FromSeconds((total - done) / _speedEma) : (TimeSpan?)null;
            _speed.Text = $"{_speedEma / 1048576:F1} MB/s  -  {done / 1073741824.0:F1} / {total / 1073741824.0:F1} GB" + (eta is { } e ? $"  -  {(e.TotalHours >= 1 ? $"{(int)e.TotalHours} h {e.Minutes} min" : $"{e.Minutes} min {e.Seconds} s")}" : "");
            SetStatus("Scarico: " + _updater.Current);
            return;
        }
        if (++_n % 10 == 0 && !_busy) { await RefreshInfo(); CheckDx(); }
    }
}
