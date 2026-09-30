// Decompila le funzioni che usano una stringa contenente uno degli aghi.  Args: fileUscita ago1 ago2 ...
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class Decomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        File out = new File(a[0]);
        List<String> needles = new ArrayList<>(Arrays.asList(a).subList(1, a.length));
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Set<Function> done = new LinkedHashSet<>();
        try (PrintWriter w = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) {
            DataIterator it = currentProgram.getListing().getDefinedData(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Data d = it.next();
                if (!d.hasStringValue() || d.getValue() == null) continue;
                String s = d.getValue().toString();
                String hit = null;
                for (String n : needles) if (s.contains(n)) { hit = n; break; }
                if (hit == null) continue;
                for (Reference r : currentProgram.getReferenceManager().getReferencesTo(d.getAddress())) {
                    Function f = currentProgram.getFunctionManager().getFunctionContaining(r.getFromAddress());
                    if (f == null || !done.add(f)) continue;
                    DecompileResults res = di.decompileFunction(f, 90, monitor);
                    w.println("// ===== " + f.getName() + " @ " + f.getEntryPoint() + "  (usa \"" + s + "\")");
                    w.println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : "// decompilazione fallita");
                    w.println();
                }
            }
        }
        println("Funzioni decompilate: " + done.size());
    }
}
