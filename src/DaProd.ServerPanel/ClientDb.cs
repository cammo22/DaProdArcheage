using Microsoft.Data.Sqlite;

namespace DaProd.ServerPanel;

/// <summary>
/// Database del client personalizzato (pass, testi...). Il client lo carica con "+db_location game/db/daprod.sqlite3"
/// (il launcher aggiunge il parametro da solo se il file esiste), quindi non serve toccare game_pak da 68 GB:
/// il file viaggia con i normali aggiornamenti del launcher.
/// </summary>
public static class ClientDb
{
    public static string Source => Path.Combine(ServerManager.ZoneClient, "game", "db", "compact.sqlite3");
    public static string Output(PanelSettings s) => Path.Combine(s.ClientDir, "game", "db", "daprod.sqlite3");

    const string Version = "2"; // alzare quando cambia cosa mettiamo nel database del client
    static string SigFile(PanelSettings s) => Path.Combine(PanelSettings.DataDir, "clientdb.sig");
    static string Signature() => Version + "|" + string.Join(",", Tweaks.Load().OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value));

    /// <summary>Da rifare se manca o se sono cambiate le regole che lo riguardano.</summary>
    public static bool NeedsBuild(PanelSettings s) =>
        File.Exists(Source) && (!File.Exists(Output(s)) || !File.Exists(SigFile(s)) || File.ReadAllText(SigFile(s)) != Signature());

    /// <summary>Riparte da una copia pulita del database inglese e applica le modifiche scelte nelle Regole.</summary>
    public static string Build(PanelSettings s)
    {
        if (!File.Exists(Source)) return "Database del client non trovato (le mappe del server non sono ancora pronte).";
        var tw = Tweaks.Load();
        var tmp = Output(s) + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        File.Copy(Source, tmp, true);
        var done = new List<string>();
        using (var c = new SqliteConnection($"Data Source={tmp};Pooling=False"))
        {
            c.Open();
            void Sql(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            if (tw.GetValueOrDefault("clientAllPasses") != 0)
            {
                // tutte le categorie di ArchePass visibili e nessuna scadenza
                Sql("UPDATE arche_pass_categories SET enable='t'");
                Sql("UPDATE arche_passes SET ed_year=0, ed_month=0, ed_day=0, ed_hour=0, ed_min=0");
                done.Add("pass");
            }
            if (tw.GetValueOrDefault("multiInstances") == 0)
            {
                // niente arene, battaglie ed eventi nella finestra delle istanze (restano i dungeon)
                var ids = new List<long>();
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT DISTINCT name, group_id FROM zones WHERE group_id IS NOT NULL AND name IS NOT NULL";
                    using var r = q.ExecuteReader();
                    while (r.Read()) if (GameData.IsMultiplayerInstance(r.GetString(0))) ids.Add(r.GetInt64(1));
                }
                try { Sql($"DELETE FROM indun_zones WHERE zone_group_id IN ({string.Join(",", ids.DefaultIfEmpty(-1))})"); done.Add("solo dungeon"); }
                catch { /* il client potrebbe non avere la tabella */ }
            }
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmp, Output(s), true);
        File.WriteAllText(SigFile(s), Signature());
        return done.Count == 0 ? "Database del client aggiornato (nessuna modifica attiva)." : "Database del client aggiornato: " + string.Join(", ", done) + ". I giocatori lo ricevono al prossimo avvio del launcher.";
    }
}
