using Microsoft.Data.Sqlite;

namespace DaProd.ServerPanel;

/// <summary>
/// Database del client personalizzato (pass, testi...). Il client lo carica con "+db_location game/db/daprod.sqlite3"
/// (il launcher aggiunge il parametro da solo se il file esiste), quindi non serve toccare game_pak da 68 GB:
/// il file viaggia con i normali aggiornamenti del launcher.
/// </summary>
public static class ClientDb
{
    /// <summary>
    /// La base è il database INGLESE del client (lo stesso che sta dentro game_pak): quello in zoneclient\game\db\compact.sqlite3 è coreano
    /// e senza testi inglesi, quindi non va mai usato per i giocatori.
    /// </summary>
    public static string Source
    {
        get
        {
            var a = Path.Combine(ServerManager.ZoneClient, "game", "db", "compact_en.sqlite3");
            var b = Path.GetFullPath(Path.Combine(PanelSettings.Root, "..", "langpatch", "compact.sqlite3"));
            return File.Exists(a) || !File.Exists(b) ? a : b;
        }
    }
    public static string Output(PanelSettings s) => Path.Combine(s.ClientDir, "game", "db", "daprod.sqlite3");

    const string Version = "5"; // alzare quando cambia cosa mettiamo nel database del client
    static string SigFile(PanelSettings s) => Path.Combine(PanelSettings.DataDir, "clientdb.sig");
    /// <summary>Solo le regole che finiscono nel database del client: cambiarne altre non deve far riscaricare 230 MB agli amici.</summary>
    static readonly string[] ClientTweaks = ["clientAllPasses", "multiInstances"];
    static string Signature() => Version + "|" + string.Join(",", ClientTweaks.Select(k => k + "=" + Tweaks.Load().GetValueOrDefault(k))) + "|" + Renames.Signature();

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
            var shops = GameData.ApplyMerchants(c); // le liste dei mercanti le legge il client dal suo database
            if (shops > 0) done.Add($"mercanti generici ({shops})");
            foreach (var (from, to) in Renames.Load())
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE localized_texts SET en_us = REPLACE(en_us, @a, @b) WHERE en_us LIKE @l";
                cmd.Parameters.AddWithValue("@a", from); cmd.Parameters.AddWithValue("@b", to); cmd.Parameters.AddWithValue("@l", "%" + from + "%");
                var n = cmd.ExecuteNonQuery();
                if (n > 0) done.Add($"{from} > {to} ({n})");
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
        using (var chk = new SqliteConnection($"Data Source={tmp};Pooling=False;Mode=ReadOnly"))
        {
            chk.Open();
            using var q = chk.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM localized_texts WHERE en_us IS NOT NULL AND en_us <> ''";
            if (Convert.ToInt64(q.ExecuteScalar()) < 100_000)
            {
                SqliteConnection.ClearAllPools(); File.Delete(tmp);
                return "Database del client NON creato: la base non ha i testi inglesi (serve compact_en.sqlite3 nella cartella zoneclient, game, db).";
            }
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmp, Output(s), true);
        File.WriteAllText(SigFile(s), Signature());
        return done.Count == 0 ? "Database del client aggiornato (nessuna modifica attiva)." : "Database del client aggiornato: " + string.Join(", ", done) + ". I giocatori lo ricevono al prossimo avvio del launcher.";
    }
}
