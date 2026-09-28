// Estrae game_pak di ArcheAge in una cartella (usa la libreria AAPacker di ZeromusXYZ, Unlicense).
// Uso:  PakExtract <game_pak> <cartella_destinazione> [--list filtro]
using AAPacker;

if (args.Length < 2) { Console.WriteLine("Uso: PakExtract <game_pak> <destinazione> [--list filtro]"); return 1; }
var pak = new AAPak(args[0], true);
if (!pak.IsOpen) { Console.WriteLine("Impossibile aprire " + args[0]); return 2; }
Console.WriteLine($"File nel pak: {pak.Files.Count}");

if (args.Length >= 4 && args[2] == "--list")
{
    foreach (var f in pak.Files.Where(f => f.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"{f.Name}  {f.Size}");
    return 0;
}

long total = pak.Files.Sum(f => f.Size), done = 0;
int n = 0, skipped = 0;
foreach (var f in pak.Files)
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
    if (n % 500 == 0) Console.WriteLine($"PROGRESS {n}/{pak.Files.Count} {done * 100 / Math.Max(1, total)}%");
}
Console.WriteLine($"FATTO {n} file ({skipped} già presenti)");
pak.ClosePak();
return 0;
