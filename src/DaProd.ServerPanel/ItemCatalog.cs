using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DaProd.ServerPanel;

/// <summary>
/// Catalogo di tutti gli oggetti del gioco (nome inglese, icona, categoria) per il menu creativo /dap. Si legge dal database inglese del client
/// (ClientDb.Source); le icone stanno in server\catalog\icons.zip (tools\mkcatalogicons.py).
/// </summary>
public static class ItemCatalog
{
    public sealed record Item(uint Id, string Name, uint Icon, uint Cat, int Grade, int Level, string Lower);

    static readonly object Gate = new();
    static List<Item>? _items;
    static Dictionary<uint, string> _cats = [];
    static readonly ConcurrentDictionary<uint, byte[]?> IconCache = new();
    static readonly Regex Bad = new(@"non target|do not translate|^test|\btest\b|dummy|^none$|unused|deprecated|\(old\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Cjk = new(@"[぀-ヿ㐀-鿿가-힯]", RegexOptions.Compiled);
    static string IconZip => Path.Combine(ServerManager.ServerDir, "catalog", "icons.zip");

    static List<Item> Load()
    {
        lock (Gate)
        {
            if (_items != null) return _items;
            var list = new List<Item>(40000);
            var src = ClientDb.Source;
            if (!File.Exists(src)) return _items = list;
            using var c = new SqliteConnection($"Data Source={src};Mode=ReadOnly;Pooling=False");
            c.Open();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT idx, en_us FROM localized_texts WHERE tbl_name='item_categories' AND tbl_column_name='name' AND en_us<>''";
                using var r = q.ExecuteReader();
                var cats = new Dictionary<uint, string>();
                while (r.Read()) cats[(uint)r.GetInt64(0)] = r.GetString(1);
                _cats = cats;
            }
            var names = new Dictionary<uint, string>();
            using (var q = c.CreateCommand())
            {
                // inglese, altrimenti il nome originale (coreano): nessun oggetto resta fuori
                q.CommandText = "SELECT idx, en_us, ko FROM localized_texts WHERE tbl_name='items' AND tbl_column_name='name'";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var en = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    var ko = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                    names[(uint)r.GetInt64(0)] = en.Length > 0 ? en : ko;
                }
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT id, icon_id, category_id, level, fixed_grade FROM items ORDER BY id";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var id = (uint)r.GetInt64(0);
                    if (!names.TryGetValue(id, out var n) || n.Length == 0) n = "Item #" + id;
                    list.Add(new Item(id, n, r.IsDBNull(1) ? 0 : (uint)r.GetInt64(1), r.IsDBNull(2) ? 0 : (uint)r.GetInt64(2),
                        r.IsDBNull(4) ? -1 : (int)r.GetInt64(4), r.IsDBNull(3) ? 0 : (int)r.GetInt64(3), n.ToLowerInvariant()));
                }
            }
            return _items = list;
        }
    }

    public static IEnumerable<(uint Id, string Name)> Categories()
    {
        var used = Load().Select(i => i.Cat).ToHashSet();
        return _cats.Where(k => used.Contains(k.Key) && !k.Value.Equals("none", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Value).Select(k => (k.Key, k.Value));
    }

    public static (int Total, List<Item> Page) Search(string q, uint cat, int skip, int take)
    {
        IEnumerable<Item> r = Load();
        if (cat != 0) r = r.Where(i => i.Cat == cat);
        var terms = (q ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in terms) { var tt = t; r = r.Where(i => i.Lower.Contains(tt) || i.Id.ToString() == tt); }
        if (terms.Length > 0)
        {
            var first = terms[0];
            r = r.OrderBy(i => i.Id.ToString() == first ? 0 : i.Lower.StartsWith(first) ? 1 : 2).ThenBy(i => i.Lower.Length).ThenBy(i => i.Id);
        }
        var list = r.ToList();
        return (list.Count, list.Skip(skip).Take(take).ToList());
    }

    public static byte[]? Icon(uint id) => IconCache.GetOrAdd(id, i =>
    {
        try
        {
            if (!File.Exists(IconZip)) return null;
            using var z = ZipFile.OpenRead(IconZip);
            var e = z.GetEntry(i + ".png");
            if (e == null) return null;
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    });
}
