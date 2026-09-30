// Decompila le funzioni agli indirizzi dati (esadecimale) e, con profondita' 1, anche quelle chiamate.  Args: fileUscita profondita' addr1 addr2 ...
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;

public class DecompAt extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        int depth = Integer.parseInt(a[1]);
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Set<Address> done = new LinkedHashSet<>();
        try (PrintWriter w = new PrintWriter(new OutputStreamWriter(new FileOutputStream(a[0]), "UTF-8"))) {
            Deque<Object[]> q = new ArrayDeque<>();
            for (int i = 2; i < a.length; i++) q.add(new Object[]{toAddr(a[i]), 0});
            while (!q.isEmpty()) {
                Object[] e = q.poll();
                Address ad = (Address) e[0]; int dp = (Integer) e[1];
                Function f = getFunctionAt(ad);
                if (f == null) f = getFunctionContaining(ad);
                if (f == null || !done.add(f.getEntryPoint())) continue;
                DecompileResults res = di.decompileFunction(f, 90, monitor);
                w.println("// ===== " + f.getName() + " @ " + f.getEntryPoint() + " (profondita' " + dp + ")");
                w.println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : "// decompilazione fallita");
                if (dp < depth)
                    for (Function c : f.getCalledFunctions(monitor)) q.add(new Object[]{c.getEntryPoint(), dp + 1});
            }
        }
        println("DecompAt completato: " + done.size());
    }
}
