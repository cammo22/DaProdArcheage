namespace DaProd.ServerPanel;

/// <summary>
/// Nomi che il gioco mostra (città, PNG, oggetti...) cambiati a piacere: un elenco "originale = nuovo" in data\renames.txt.
/// Vale per il testo inglese del database del client (viaggia con gli aggiornamenti del launcher).
/// </summary>
public static class Renames
{
    public static string FilePath => Path.Combine(PanelSettings.DataDir, "renames.txt");

    static readonly (string From, string To)[] Defaults = [("Marianople", "MariaNapoli"), ("Marilolo", "Mariuolo")];

    public static List<(string From, string To)> Load()
    {
        if (!File.Exists(FilePath)) { Save([.. Defaults]); }
        var list = new List<(string, string)>();
        foreach (var l in File.ReadAllLines(FilePath))
        {
            var i = l.IndexOf('=');
            if (i <= 0 || l.TrimStart().StartsWith('#')) continue;
            var a = l[..i].Trim(); var b = l[(i + 1)..].Trim();
            if (a.Length > 0 && b.Length > 0 && a != b) list.Add((a, b));
        }
        return list;
    }

    public static void Save(List<(string From, string To)> list)
    {
        Directory.CreateDirectory(PanelSettings.DataDir);
        File.WriteAllLines(FilePath, ["# originale = nuovo (una riga per nome; maiuscole contano)", .. list.Select(x => $"{x.From} = {x.To}")]);
    }

    public static string Signature() => string.Join(";", Load().Select(x => x.From + ">" + x.To));
}
