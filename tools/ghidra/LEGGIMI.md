# Reverse engineering del client con Ghidra

Ghidra 12.1.4 (NSA, Apache-2.0) e un JDK 21 portatile stanno in `Z:\Archeage\tools` (fuori dalla repo, ~1,5 GB).
Il progetto `ghidra-proj` contiene `x2game.dll` gia' analizzata (12 minuti), quindi le interrogazioni successive sono rapide.

```bash
# analisi completa di un nuovo file (lenta: minuti/ore)
tools/ghidra/ghidra.sh "Z:\Archeage\tools\ghidra-proj" AA -import "<file.dll>" -overwrite \
    -scriptPath "Z:\Archeage\tools\ghidra-scripts" -postScript DumpStrings.java "<out.tsv>"

# domande su un file gia' analizzato (veloce, ~1 minuto)
tools/ghidra/ghidra.sh "Z:\Archeage\tools\ghidra-proj" AA -process x2game.dll -noanalysis \
    -scriptPath "Z:\Archeage\tools\ghidra-scripts" -postScript LuaBind.java "<out.c>" NomeFunzioneLua
```

| Script | Cosa fa |
|---|---|
| `DumpStrings.java` | tutte le stringhe con le funzioni che le usano (TSV) |
| `Decomp.java` | decompila le funzioni che usano una stringa |
| `LuaBind.java` | dalla stringa di una funzione Lua/cvar arriva al codice che la registra |
| `DecompAt.java` | decompila a un indirizzo, con i chiamati a profondita' N |

Risultati utili (in `Z:\Archeage\tools\ghidra-out`): elenco di ~2500 funzioni Lua e ~5500 identificatori/cvar di `x2game.dll`.
