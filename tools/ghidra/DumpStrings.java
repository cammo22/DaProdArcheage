// Scrive tutte le stringhe del programma con le funzioni che le usano: address<TAB>funzioni<TAB>stringa
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class DumpStrings extends GhidraScript {
    @Override
    public void run() throws Exception {
        File out = new File(getScriptArgs()[0]);
        int n = 0;
        try (PrintWriter w = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) {
            DataIterator it = currentProgram.getListing().getDefinedData(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Data d = it.next();
                if (!d.hasStringValue()) continue;
                Object v = d.getValue();
                if (v == null) continue;
                String s = v.toString();
                if (s.length() < 4) continue;
                Set<String> fs = new LinkedHashSet<>();
                for (Reference r : currentProgram.getReferenceManager().getReferencesTo(d.getAddress())) {
                    Function f = currentProgram.getFunctionManager().getFunctionContaining(r.getFromAddress());
                    if (f != null) fs.add(f.getEntryPoint() + ":" + f.getName());
                    if (fs.size() >= 6) break;
                }
                w.println(d.getAddress() + "\t" + String.join(",", fs) + "\t" + s.replace("\n", "\n").replace("\r", "\r").replace("\t", "\t"));
                n++;
            }
        }
        println("Stringhe scritte: " + n);
    }
}
