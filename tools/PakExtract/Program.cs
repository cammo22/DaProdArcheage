// Estrae game_pak di ArcheAge in una cartella (usa la libreria AAPacker di ZeromusXYZ, Unlicense).
// Uso:  PakExtract <game_pak> <cartella_destinazione> [--list filtro]
using AAPacker;

if (args.Length < 2) { Console.WriteLine("Uso: PakExtract <game_pak> <destinazione> [--list filtro]  |  PakExtract <game_pak> --replace <percorso/nel/pak> <file>"); return 1; }

// sostituisce un file dentro il pak (es. game/db/compact.sqlite3 con i testi inglesi)
if (args.Length >= 4 && args[1] == "--replace")
{
    var rw = new AAPak(args[0], false);
    if (!rw.IsOpen) { Console.WriteLine("Impossibile aprire in scrittura " + args[0]); return 2; }
    using (var fs = File.OpenRead(args[3]))
        if (!rw.AddFileFromStream(args[2], fs, DateTime.Now, DateTime.Now, false, out _)) { Console.WriteLine("Sostituzione fallita"); return 3; }
    rw.ClosePak();
    Console.WriteLine("SOSTITUITO " + args[2]);
    return 0;
}

var pak = new AAPak(args[0], true);
if (!pak.IsOpen) { Console.WriteLine("Impossibile aprire " + args[0]); return 2; }
Console.WriteLine($"File nel pak: {pak.Files.Count}");

if (args.Length >= 4 && args[2] == "--list")
{
    foreach (var f in pak.Files.Where(f => f.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"{f.Name}  {f.Size}");
    return 0;
}

// opzionale: --only <testo> estrae solo i file il cui percorso contiene il testo
var only = args.Length >= 4 && args[2] == "--only" ? args[3] : null;
var files = only == null ? pak.Files.ToList() : pak.Files.Where(f => f.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
long total = files.Sum(f => f.Size), done = 0;
int n = 0, skipped = 0;
foreach (var f in files)
{
    var dest = Path.Combine(args[1], f.Name.Replace('/', Path.DirectorySeparatorChar));
    n++;
    // ripresa: salta i file già estratti con la stessa dimensione
    if (File.Exists(dest) && new FileInfo(dest).Length == f.Size) { done += f.Size; skipped++; continue; }
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    using (var src = pak.ExportFileAsStream(f))
    using (var dst = File.Create(dest))
        src.CopyTo(dst);
    done += f.Size;
    if (n % 500 == 0) Console.WriteLine($"PROGRESS {n}/{files.Count} {done * 100 / Math.Max(1, total)}%");
}
Console.WriteLine($"FATTO {n} file ({skipped} già presenti)");
pak.ClosePak();
return 0;
