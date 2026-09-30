namespace DaProd.ServerPanel;

/// <summary>Pagina "Regole": personalizzazione del gioco (shop, mercato, pass, labor) e tutte le opzioni di configurazione.</summary>
public sealed partial class MainForm
{
    Control RulesPage()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = Ui.Normal };
        var t1 = new TabPage("Regole live") { BackColor = Ui.Bg };
        var t2 = new TabPage("Configurazione avanzata") { BackColor = Ui.Bg };
        tabs.TabPages.AddRange([t1, t2]);

        // ---------- regole live: cambiano subito, senza riavviare ----------
        var values = Tweaks.Load();
        var g = Ui.Grid(); g.ReadOnly = false;
        g.Columns.Add("Gruppo", "Gruppo"); g.Columns.Add("Regola", "Regola"); g.Columns.Add("Valore", "Valore"); g.Columns.Add("Cosa fa", "Cosa fa");
        foreach (var c in new[] { 0, 1, 3 }) g.Columns[c].ReadOnly = true;
        g.Columns[0].FillWeight = 40; g.Columns[1].FillWeight = 130; g.Columns[2].FillWeight = 40; g.Columns[3].FillWeight = 260;
        foreach (var t in Tweaks.All)
        {
            var i = g.Rows.Add(t.Group, t.Label, values[t.Key].ToString(System.Globalization.CultureInfo.InvariantCulture), t.Help);
            g.Rows[i].Tag = t.Key;
        }
        g.CellEndEdit += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 2) return;
            var row = g.Rows[e.RowIndex]; var key = (string)row.Tag!;
            if (!double.TryParse(((string?)row.Cells[2].Value ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                row.Cells[2].Value = values[key].ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            var was = values[key]; values[key] = v; Tweaks.Save(values);
            if (key == "shopFree" && (was != 0) != (v != 0))
                MessageBox.Show(v != 0 ? await Bg2(() => _srv.ApplyShopFreeAsync()) : await Bg2(() => _srv.ImportShopAsync()), "Shop");
        };

        var shopBar = Ui.Bar();
        shopBar.Controls.Add(Ui.Btn("Shop: carica predefinito", Ui.Blue, async () =>
        {
            if (MessageBox.Show("Ricarico lo shop predefinito (sostituisce quello attuale). Continuo?", "Shop", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            MessageBox.Show(await Bg2(() => _srv.ImportShopAsync()), "Shop");
        }, 200));
        shopBar.Controls.Add(Ui.Btn("Shop: tutto gratis ora", Ui.Green, async () => MessageBox.Show(await Bg2(() => _srv.ApplyShopFreeAsync()), "Shop"), 200));
        shopBar.Controls.Add(Ui.Btn("Ricostruisci dati client", Ui.Purple, async () => MessageBox.Show(await Bg2(() => Task.Run(() => { var r = ClientDb.Build(_s); _api.ResetManifest(); return r; })), "Dati client"), 220));
        t1.Controls.Add(g); t1.Controls.Add(Ui.Gap()); t1.Controls.Add(shopBar);
        t1.Controls.Add(Ui.Hint("Queste regole valgono subito, anche con i giocatori online (il World le rilegge da solo ogni pochi secondi). " +
                                "Per lo shop: i giocatori devono riaprire la finestra. Livello, oro, labor e punti pass di un personaggio si cambiano da Giocatori > Personaggi.", 56));

        // ---------- configurazione avanzata: tutte le opzioni dei file Configurations\*.json ----------
        var entries = ConfigOverrides.List(Path.Combine(ServerManager.ServerDir, "bin", "game", "Configurations"));
        var edited = ConfigOverrides.LoadOverrides();
        var ga = Ui.Grid(); ga.ReadOnly = false;
        ga.Columns.Add("Opzione", "Opzione"); ga.Columns.Add("Valore", "Valore"); ga.Columns.Add("Originale", "Originale");
        ga.Columns[0].ReadOnly = true; ga.Columns[2].ReadOnly = true; ga.Columns[0].FillWeight = 220;
        var filter = new TextBox { Dock = DockStyle.Top, BackColor = Ui.Card, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Cerca (es. siege, tax, labor, premium...)" };
        void Fill()
        {
            ga.Rows.Clear();
            foreach (var e in entries.Where(e => filter.Text.Length == 0 || e.Path.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)))
                ga.Rows.Add(e.Path, edited.GetValueOrDefault(e.Path, e.Original), e.Original);
        }
        filter.TextChanged += (_, _) => Fill();
        ga.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
            var path = (string)ga.Rows[e.RowIndex].Cells[0].Value!; var val = ((string?)ga.Rows[e.RowIndex].Cells[1].Value ?? "").Trim();
            var orig = entries.First(x => x.Path == path);
            if (orig.Kind == "bool" && !(val is "true" or "false")) { val = val is "1" or "sì" or "si" ? "true" : "false"; ga.Rows[e.RowIndex].Cells[1].Value = val; }
            if (val == orig.Original) edited.Remove(path); else edited[path] = val;
            ga.Rows[e.RowIndex].Cells[1].Style.ForeColor = val == orig.Original ? Ui.Text : Ui.Orange;
        };
        Fill();
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Btn("Salva e applica (riavvia)", Ui.Orange, async () =>
        {
            ConfigOverrides.Save(entries.Select(x => x with { Value = edited.GetValueOrDefault(x.Path, x.Original) }));
            if (MessageBox.Show("Le modifiche si applicano riavviando il server (circa 2-3 minuti, i giocatori vengono scollegati). Riavvio ora?", "Applica", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Bg(() => { _srv.StopAll(); return Task.CompletedTask; }); await Task.Delay(2000); await Bg(() => _srv.StartAllAsync());
        }, 240));
        bar.Controls.Add(Ui.Btn("Ripristina tutto", Ui.Grey, () =>
        {
            edited.Clear(); ConfigOverrides.Save([]); Fill(); return Task.CompletedTask;
        }, 160));
        t2.Controls.Add(ga); t2.Controls.Add(Ui.Gap()); t2.Controls.Add(bar); t2.Controls.Add(filter);
        t2.Controls.Add(Ui.Hint("Tutte le opzioni del gioco (funzioni attive/spente come siege, premium, ingamecashshop, regole del mondo...). In arancione le voci modificate. " +
                                "Attenzione: un valore sbagliato può impedire l'avvio; \"Ripristina tutto\" torna all'originale.", 56));

        var p = Ui.Buffered(new Panel { Dock = DockStyle.Fill });
        p.Controls.Add(tabs); p.Controls.Add(Ui.Gap());
        p.Controls.Add(Ui.Title("Regole del gioco"));
        return p;
    }

    /// <summary>Esegue in background e restituisce il messaggio (o l'errore) da mostrare.</summary>
    static Task<string> Bg2(Func<Task<string>> f) => Task.Run(async () =>
    {
        try { return await f(); }
        catch (Exception ex) { return "Non riuscito (il server è acceso?): " + ex.Message; }
    });
}
