using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaProd.ServerPanel;

/// <summary>Una regola di gioco modificabile in tempo reale (il World rilegge data\tweaks.json da solo).</summary>
public sealed record TweakDef(string Key, string Group, string Label, double Default, string Help);

public static class Tweaks
{
    public static readonly TweakDef[] All =
    [
        new("shopFree", "Negozio", "Shop: tutto gratis e senza limiti", 1, "1 = ogni oggetto costa 0 e non ha limiti di acquisto (si applica quando si carica lo shop). 0 = prezzi originali."),
        new("auctionFree", "Mercato", "Mercato: niente commissioni né deposito", 1, "1 = mettere in vendita e vendere non costa nulla. 0 = commissioni originali."),
        new("passFree", "Pass", "Pass: acquisto e potenziamento gratis", 1, "1 = comprare un ArchePass e passare al premium non costa oro né oggetti."),
        new("passPointMult", "Pass", "Pass: moltiplicatore dei punti", 1, "Punti pass guadagnati x questo numero (es. 5 = cinque volte più veloci)."),
        new("clientAllPasses", "Gioco (client)", "Pass: mostra tutti gli ArchePass", 1, "1 = nella finestra ArchePass compaiono tutti i pass, senza scadenza. Richiede \"Ricostruisci dati client\" (poi i giocatori riavviano il launcher)."),
        new("geoData", "Memoria", "Navigazione precisa dei dungeon (GeoData)", PanelSettings.LowRam ? 0 : 1, "1 = carica i dati di navigazione (circa 1 GB di RAM in più). 0 = usa le mappe di altezza. Serve riavvio del server."),
        new("dailyInstant", "Missioni", "Daily Contract: completamento istantaneo", 1, "1 = appena accetti un Daily Contract (Orario giornaliero) la missione si completa da sola e dà il premio. 0 = vanno fatte davvero."),
        new("allRecipes", "Mestieri", "Folio: tutte le ricette utilizzabili", 1, "1 = anche le ricette che richiedono un oggetto-ricetta si possono creare senza averlo imparato."),
        new("dapMenu", "Creativo", "Menu oggetti /dap", 1, "1 = scrivendo /dap in chat si apre il menu con tutti gli oggetti del gioco (sul launcher), con anteprima e ricerca, per darseli. 0 = disattivato."),
        new("rerollUndo", "Sintesi", "Replace Effect: si può annullare", 1, "1 = dopo aver sostituito un effetto puoi scrivere /annulla in chat per rimettere quello vecchio (entro 30 minuti) e riavere il tentativo."),
        new("dailyAuto", "Missioni", "Daily Contract: si completano da sole", 1, "1 = finiti gli obiettivi di un Daily Contract la missione si chiude da sola e dà subito i premi (senza consegna a un PNG). 0 = comportamento originale."),
        new("dailyFreeUnlock", "Missioni", "Daily Schedule: sblocco gratis", 1, "1 = le missioni giornaliere a pagamento si sbloccano senza consumare l'oggetto richiesto."),
        new("dailyResetsMax", "Missioni", "Daily Schedule: cambi missione al giorno", 99, "Quante volte al giorno si può premere \"Cambia missione\" (3 = originale)."),
        new("manastormShop", "Negozio", "Manastorm Shop sul pulsante Random Shop", 1, "1 = il pulsante del negozio casuale (Manastorm / Palos) si attiva ovunque, senza NPC. Serve rientrare in gioco."),
        new("multiInstances", "Istanze", "Istanze multiplayer (arene, campi di battaglia, eventi)", 0, "0 = restano solo i dungeon; arene, battaglie, difese ed eventi sono tolti. 1 = tutte le istanze. Serve riavvio del server."),
        new("laborRegenMult", "Labor", "Labor: velocità di recupero", 1, "Il labor si ricarica x questo numero (es. 10 = dieci volte più veloce, online e offline)."),
    ];

    public static string FilePath => Path.Combine(PanelSettings.DataDir, "tweaks.json");

    public static Dictionary<string, double> Load()
    {
        var d = All.ToDictionary(t => t.Key, t => t.Default);
        try
        {
            if (File.Exists(FilePath))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)) ?? [])
                    d[k] = v;
        }
        catch { }
        return d;
    }

    public static void Save(Dictionary<string, double> d)
    {
        Directory.CreateDirectory(PanelSettings.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool On(string key) => Load().GetValueOrDefault(key) != 0;
}

/// <summary>
/// Tutte le opzioni "Configurations\*.json" del gioco (funzioni attive, regole del mondo...): si leggono dai file originali
/// e le modifiche vanno in data\overrides.json, che il pannello unisce a Config.Local.json a ogni avvio.
/// </summary>
public static class ConfigOverrides
{
    static readonly string[] Files = ["World", "InitialConfig", "Features", "CharacterSettings", "Expedition", "Butler"];
    /// <summary>Già modificabili dalla pagina Mondo.</summary>
    static readonly HashSet<string> InMondo = new(["ExpRate", "LootRate", "GoldLootMultiplier", "HonorRate", "PvpHonorRate", "VocationRate", "GrowthRate", "ActabilityRate", "PlayerLevelCap", "AutoSaveInterval", "MOTD"]);

    public static string FilePath => Path.Combine(PanelSettings.DataDir, "overrides.json");

    public sealed record Entry(string Path, string Value, string Original, string Kind);

    /// <summary>Elenco piatto: percorso (es. World.GodMode, Features.Flags.siege), valore attuale e valore originale.</summary>
    public static List<Entry> List(string configDir)
    {
        var over = LoadOverrides();
        var list = new List<Entry>();
        foreach (var f in Files)
        {
            var path = System.IO.Path.Combine(configDir, f + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                Walk(doc.RootElement, "", list, over);
            }
            catch { }
        }
        return list.Where(e => !(e.Path.StartsWith("World.") && InMondo.Contains(e.Path[6..]))).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Walk(JsonElement el, string prefix, List<Entry> list, Dictionary<string, string> over)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject()) Walk(p.Value, prefix == "" ? p.Name : prefix + "." + p.Name, list, over);
                break;
            case JsonValueKind.True or JsonValueKind.False:
                list.Add(new(prefix, over.GetValueOrDefault(prefix, el.GetBoolean() ? "true" : "false"), el.GetBoolean() ? "true" : "false", "bool"));
                break;
            case JsonValueKind.Number:
                list.Add(new(prefix, over.GetValueOrDefault(prefix, el.GetRawText()), el.GetRawText(), "number"));
                break;
            case JsonValueKind.String:
                list.Add(new(prefix, over.GetValueOrDefault(prefix, el.GetString() ?? ""), el.GetString() ?? "", "string"));
                break;
        }
    }

    public static Dictionary<string, string> LoadOverrides()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    /// <summary>Salva solo i valori diversi dall'originale.</summary>
    public static void Save(IEnumerable<Entry> entries)
    {
        var d = entries.Where(e => e.Value != e.Original).ToDictionary(e => e.Path, e => e.Value);
        Directory.CreateDirectory(PanelSettings.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Unisce le modifiche nella configurazione del gioco (Config.Local.json).</summary>
    public static void Apply(JsonObject game, string configDir)
    {
        var over = LoadOverrides();
        if (over.Count == 0) return;
        var kinds = List(configDir).ToDictionary(e => e.Path, e => e.Kind);
        foreach (var (path, value) in over)
        {
            if (!kinds.TryGetValue(path, out var kind)) continue;
            var parts = path.Split('.');
            JsonObject node = game;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (node[parts[i]] is not JsonObject next) node[parts[i]] = next = new JsonObject();
                node = next;
            }
            node[parts[^1]] = kind switch
            {
                "bool" => JsonValue.Create(value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"),
                "number" when double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
                _ => JsonValue.Create(value)
            };
        }
    }
}
