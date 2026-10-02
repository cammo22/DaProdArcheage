using System.Drawing;
using System.Net.Http.Json;
using System.Text.Json;

namespace DaProd.Launcher;

/// <summary>
/// Menu creativo (/dap in chat): tutti gli oggetti del gioco con anteprima, ricerca e filtro per categoria. Doppio clic o "Dai" li mette
/// nella borsa del personaggio in gioco. Elenco e icone arrivano dal server (API /dap/...).
/// </summary>
sealed class DapForm : Form
{
    readonly HttpClient _http;
    readonly Func<string, string> _api;
    readonly string _user, _token;
    readonly long _charId;
    readonly TextBox _search = new() { BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11), PlaceholderText = "Cerca un oggetto (nome o ID)..." };
    readonly ComboBox _cat = new() { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Field, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10) };
    readonly ListView _list = new() { View = View.LargeIcon, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, MultiSelect = false, HideSelection = false, Font = new Font("Segoe UI", 8.5f) };
    readonly ImageList _imgs = new() { ImageSize = new Size(48, 48), ColorDepth = ColorDepth.Depth32Bit };
    readonly NumericUpDown _count = new() { Minimum = 1, Maximum = 9999, Value = 1, Width = 80, BackColor = Theme.Field, ForeColor = Theme.Text, Font = new Font("Segoe UI", 11) };
    readonly RoundButton _give = new() { Text = "Dai al personaggio", Fill = Theme.Green, Width = 190, Height = 34 };
    readonly RoundButton _prev = new() { Text = "< Indietro", Fill = Theme.Blue, Width = 110, Height = 34, Enabled = false };
    readonly RoundButton _next = new() { Text = "Avanti >", Fill = Theme.Blue, Width = 110, Height = 34, Enabled = false };
    readonly Label _pageLbl = new() { AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(8, 8, 8, 0), Text = "Pagina 1" };
    const int PageSize = 200;
    int _page;
    readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Small };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    readonly Dictionary<uint, string> _iconKeys = [];
    readonly SemaphoreSlim _iconGate = new(8);
    readonly Dictionary<string, (uint Id, string Name)> _rows = [];
    int _loaded, _total, _gen;

    public DapForm(HttpClient http, Func<string, string> api, string user, string token, long charId, string charName)
    {
        _http = http; _api = api; _user = user; _token = token; _charId = charId;
        Text = $"Ponticheage - Menu oggetti ({charName})";
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(880, 640); MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Normal; TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _list.LargeImageList = _imgs;
        _imgs.Images.Add("0", new Bitmap(48, 48));

        var top = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 12, 14, 6) };
        _cat.Dock = DockStyle.Right; _cat.Width = 230;
        _search.Dock = DockStyle.Fill;
        top.Controls.Add(_search); top.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10 }); top.Controls.Add(_cat);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(14, 10, 14, 8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var qty = new Label { Text = "Quantità", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 8, 6, 0) };
        _give.Margin = new Padding(12, 0, 0, 0);
        buttons.Controls.AddRange([_prev, _pageLbl, _next, new Label { Width = 14 }, qty, _count, _give]);
        bottom.Controls.Add(_status); bottom.Controls.Add(buttons);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 4) };
        host.Controls.Add(_list);
        Controls.Add(host); Controls.Add(top); Controls.Add(bottom);

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Reload(); };
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _cat.SelectedIndexChanged += async (_, _) => await Reload();
        _give.Click += async (_, _) => await Give();
        _list.DoubleClick += async (_, _) => await Give();
        _prev.Click += async (_, _) => { _page--; await Load(); };
        _next.Click += async (_, _) => { _page++; await Load(); };
        Shown += async (_, _) => { _search.Focus(); await LoadCats(); await Reload(); };
        FormClosed += (_, _) => { _debounce.Dispose(); _imgs.Dispose(); };
    }

    string Url(string path, string extra = "") => _api($"{path}?user={Uri.EscapeDataString(_user)}&t={Uri.EscapeDataString(_token)}{extra}");

    async Task LoadCats()
    {
        try
        {
            var doc = await _http.GetFromJsonAsync<JsonElement>(Url("/dap/items", "&cats=1&skip=0&q=zzzzzzzz"));
            _cat.Items.Clear(); _cat.Items.Add(new CatItem(0, "Tutte le categorie"));
            if (doc.TryGetProperty("cats", out var cats) && cats.ValueKind == JsonValueKind.Array)
                foreach (var c in cats.EnumerateArray()) _cat.Items.Add(new CatItem(c.GetProperty("id").GetUInt32(), c.GetProperty("name").GetString() ?? ""));
            _cat.SelectedIndex = 0;
        }
        catch (Exception ex) { _status.Text = "Elenco non raggiungibile: " + ex.Message; }
    }

    sealed record CatItem(uint Id, string Name) { public override string ToString() => Name; }

    Task Reload() { _page = 0; return Load(); }

    async Task Load()
    {
        var gen = ++_gen;
        try
        {
            var cat = (_cat.SelectedItem as CatItem)?.Id ?? 0;
            var skip = _page * PageSize;
            _status.Text = "Carico...";
            var doc = await _http.GetFromJsonAsync<JsonElement>(Url("/dap/items", $"&q={Uri.EscapeDataString(_search.Text)}&cat={cat}&skip={skip}"));
            if (gen != _gen) return;
            if (doc.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) { _status.Text = doc.GetProperty("message").GetString() ?? "Rifiutato."; return; }
            _total = doc.GetProperty("total").GetInt32();
            _list.BeginUpdate();
            _list.Items.Clear(); _rows.Clear(); _loaded = 0;
            var pending = new List<(ListViewItem Item, uint Icon)>();
            foreach (var it in doc.GetProperty("items").EnumerateArray())
            {
                var id = it.GetProperty("id").GetUInt32(); var name = it.GetProperty("name").GetString() ?? "";
                var icon = it.GetProperty("icon").GetUInt32();
                var lvi = new ListViewItem(name) { ImageKey = _iconKeys.GetValueOrDefault(icon, "0"), Tag = id, ToolTipText = $"{name}\nID {id}" };
                _list.Items.Add(lvi); _rows[id.ToString()] = (id, name); _loaded++;
                if (!_iconKeys.ContainsKey(icon)) pending.Add((lvi, icon));
            }
            _list.EndUpdate();
            var pages = Math.Max(1, (_total + PageSize - 1) / PageSize);
            _pageLbl.Text = $"Pagina {_page + 1} / {pages}";
            _prev.Enabled = _page > 0; _next.Enabled = _page + 1 < pages;
            _status.Text = $"{_total} oggetti. Doppio clic o \"Dai\" per metterlo in borsa.";
            _ = LoadIcons(pending, gen);
        }
        catch (Exception ex) { _status.Text = "Errore: " + ex.Message; }
    }

    async Task LoadIcons(List<(ListViewItem Item, uint Icon)> pending, int gen)
    {
        var tasks = pending.GroupBy(p => p.Icon).Select(async g =>
        {
            await _iconGate.WaitAsync();
            try
            {
                if (IsDisposed) return;
                var bytes = await _http.GetByteArrayAsync(_api($"/dap/icon/{g.Key}.png"));
                using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    var key = "i" + g.Key;
                    if (!_imgs.Images.ContainsKey(key)) _imgs.Images.Add(key, bmp);
                    _iconKeys[g.Key] = key;
                    foreach (var p in g) { try { p.Item.ImageKey = key; } catch { } }
                });
            }
            catch { }
            finally { _iconGate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    async Task Give()
    {
        if (_list.SelectedItems.Count == 0) { _status.Text = "Seleziona un oggetto."; return; }
        var lvi = _list.SelectedItems[0]; var id = (uint)lvi.Tag!;
        _give.Enabled = false;
        try
        {
            var doc = await _http.GetFromJsonAsync<JsonElement>(Url("/dap/give", $"&char={_charId}&item={id}&count={(int)_count.Value}"));
            _status.ForeColor = doc.GetProperty("ok").GetBoolean() ? Theme.Green : Theme.Red;
            _status.Text = $"{lvi.Text} x{(int)_count.Value}: {doc.GetProperty("message").GetString()}";
        }
        catch (Exception ex) { _status.ForeColor = Theme.Red; _status.Text = "Errore: " + ex.Message; }
        finally { _give.Enabled = true; }
    }
}
