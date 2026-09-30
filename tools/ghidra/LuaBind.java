// Per ogni stringa che contiene un ago: se e' referenziata da una tabella dati (registrazione Lua/cvar),
// decompila la funzione puntata subito dopo il nome (name, func).  Args: fileUscita ago1 ago2 ...
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class LuaBind extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        File out = new File(a[0]);
        List<String> needles = new ArrayList<>(Arrays.asList(a).subList(1, a.length));
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Set<Address> done = new HashSet<>();
        try (PrintWriter w = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) {
            DataIterator it = currentProgram.getListing().getDefinedData(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Data d = it.next();
                if (!d.hasStringValue() || d.getValue() == null) continue;
                String s = d.getValue().toString();
                boolean hit = false;
                for (String n : needles) if (s.equals(n) || s.contains(n)) { hit = true; break; }
                if (!hit) continue;
                for (Reference r : currentProgram.getReferenceManager().getReferencesTo(d.getAddress())) {
                    Address from = r.getFromAddress();
                    Function inFn = currentProgram.getFunctionManager().getFunctionContaining(from);
                    Function target = null;
                    String how;
                    if (inFn != null) { target = inFn; how = "codice"; }
                    else {
                        long ptr = currentProgram.getMemory().getLong(from.add(8));
                        Address fa = toAddr(ptr);
                        target = fa == null ? null : getFunctionAt(fa);
                        how = "tabella";
                    }
                    w.println("// ===== \"" + s + "\" riferita da " + from + " (" + how + ")" + (target == null ? " -> nessuna funzione" : " -> " + target.getName() + " @ " + target.getEntryPoint()));
                    if (target == null || !done.add(target.getEntryPoint())) continue;
                    DecompileResults res = di.decompileFunction(target, 90, monitor);
                    w.println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : "// decompilazione fallita");
                    w.println();
                }
            }
        }
        println("LuaBind completato");
    }
}
