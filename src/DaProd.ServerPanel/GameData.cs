using Microsoft.Data.Sqlite;

namespace DaProd.ServerPanel;

/// <summary>
/// Ritocchi ai dati di gioco del server (bin\game\Data\compact.sqlite3), rifatti a ogni avvio del World:
/// mercanti generici, istanze multiplayer. Tutto idempotente; le istanze tolte vengono salvate in una tabella di backup
/// così si possono rimettere dalle Regole.
/// </summary>
public static class GameData
{
    public static string ServerDb => Path.Combine(ServerManager.ServerDir, "bin", "game", "Data", "compact.sqlite3");

    /// <summary>NPC "General Merchant" del gioco (id = indice del testo).</summary>
    static readonly uint[] GeneralMerchants = [206, 216, 975, 1045, 3579, 3581, 3582, 3966, 17366];

    /// <summary>Venduti dai mercanti generici: Story Quest Infusion (rank 1-3 e generica) e Manastorm Crystal.</summary>
    public static readonly uint[] MerchantItems = [47852, 47853, 47854, 48845, 48846, 48847, 54335, 45508];

    /// <summary>Istanze che restano: i dungeon (PvE) e i luoghi collegati alle loro quest.</summary>
    static readonly string[] DungeonPrefixes =
    [
        "instance_burntcastle_armory", "instance_hadir_farm", "instance_sal_temple", "instance_cuttingwind_deadmine",
        "instance_howling_abyss", "instance_cradle_of_destruction", "instance_nachashgar", "instance_immortal_isle",
        "instance_library_", "instance_feast_garden", "instance_hanging_gardens_of_ipna", "instance_challenge_tower",
        "instance_black_spike", "instance_life_dungeon_daru", "instance_prologue", "instance_carcass",
        "instance_dewplane_boss", "instance_eternity", "instance_mount_ipnir_story"
    ];

    /// <summary>Istanza multiplayer (arene, campi di battaglia, eventi, difese...): tutto ciò che è "instance_" ma non un dungeon.</summary>
    public static bool IsMultiplayerInstance(string zoneName)
    {
        var n = zoneName.Trim().ToLowerInvariant();
        var isInstance = n.StartsWith("instance_") || n.StartsWith("test_instance") || n.StartsWith("zone_instance") || n.StartsWith("zonegroup_instance") || n == "test_arcaneearth";
        return isInstance && !DungeonPrefixes.Any(p => n.StartsWith(p));
    }

    public static string Apply(Action<string> log)
    {
        if (!File.Exists(ServerDb)) return "dati di gioco non trovati";
        var tw = Tweaks.Load();
        var notes = new List<string>();
        using var c = new SqliteConnection($"Data Source={ServerDb};Pooling=False");
        c.Open();
        int Sql(string sql, params (string, object)[] ps)
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
            return cmd.ExecuteNonQuery();
        }
        long Scalar(string sql)
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
        }

        // ---- mercanti generici ----
        using (var tx = c.BeginTransaction())
        {
            var nextGood = Scalar("SELECT COALESCE(MAX(id),0) FROM merchant_goods") + 1;
            var nextPack = Math.Max(9_000_000, Scalar("SELECT COALESCE(MAX(id),0) FROM merchant_packs") + 1);
            var nextMerchant = Math.Max(9_000_000, Scalar("SELECT COALESCE(MAX(id),0) FROM merchants") + 1);
            var added = 0;
            foreach (var npc in GeneralMerchants)
            {
                var pack = Scalar($"SELECT COALESCE(MIN(merchant_pack_id),0) FROM merchants WHERE npc_id={npc}");
                if (pack == 0)
                {
                    pack = nextPack++;
                    Sql("INSERT INTO merchant_packs (id, name, owner_npc_id, kind_id, item_point_id, item_point_icon, item_point_icon_key) VALUES (@i, @n, @o, 0, 0, '', '')", ("@i", pack), ("@n", "daprod.npc." + npc), ("@o", npc));
                    Sql("INSERT INTO merchants (id, npc_id, merchant_pack_id) VALUES (@i, @n, @p)", ("@i", nextMerchant++), ("@n", npc), ("@p", pack));
                }
                var order = 1000;
                foreach (var item in MerchantItems)
                {
                    if (Scalar($"SELECT COUNT(*) FROM merchant_goods WHERE merchant_pack_id={pack} AND item_id={item} AND grade_id=0") > 0) { order++; continue; }
                    Sql("INSERT INTO merchant_goods (id, merchant_pack_id, item_id, grade_id, enable, view_order, cost, purchase_type_id, purchase_limit) VALUES (@i, @p, @t, 0, 't', @o, 0, 1, 0)",
                        ("@i", nextGood++), ("@p", pack), ("@t", item), ("@o", order++));
                    added++;
                }
            }
            tx.Commit();
            if (added > 0) notes.Add($"mercanti generici: {added} oggetti aggiunti");
        }

        // ---- istanze multiplayer ----
        var blocked = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT name, group_id FROM zones WHERE group_id IS NOT NULL AND name IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (IsMultiplayerInstance(r.GetString(0))) blocked.Add(r.GetInt64(1));
        }
        var ids = string.Join(",", blocked.DefaultIfEmpty(-1));
        Sql("CREATE TABLE IF NOT EXISTS daprod_indun_backup AS SELECT * FROM indun_zones WHERE 0");
        if (tw.GetValueOrDefault("multiInstances") == 0)
        {
            Sql($"INSERT INTO daprod_indun_backup SELECT * FROM indun_zones WHERE zone_group_id IN ({ids}) AND zone_group_id NOT IN (SELECT zone_group_id FROM daprod_indun_backup)");
            var n = Sql($"DELETE FROM indun_zones WHERE zone_group_id IN ({ids})");
            if (n > 0) notes.Add($"istanze multiplayer tolte: {n}");
        }
        else
        {
            var n = Sql("INSERT INTO indun_zones SELECT * FROM daprod_indun_backup WHERE zone_group_id NOT IN (SELECT zone_group_id FROM indun_zones)");
            Sql("DELETE FROM daprod_indun_backup");
            if (n > 0) notes.Add($"istanze multiplayer rimesse: {n}");
        }
        SqliteConnection.ClearAllPools();
        var msg = notes.Count == 0 ? "dati di gioco già a posto" : string.Join("; ", notes);
        log(msg);
        return msg;
    }
}
