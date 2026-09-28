"""Completa compact.sqlite3 con le righe mancanti prese da game_decrypted.sqlite3.

Il compact multilingue ha alcuni buchi (es. crafts referenziati ma assenti) che
fanno fallire l'avvio del Game server. Le righe già presenti non vengono toccate
(INSERT OR IGNORE), quindi le traduzioni restano.

Uso: python repair-compact.py <compact.sqlite3> <game_decrypted.sqlite3>
"""
import sqlite3
import sys

compact, source = sys.argv[1], sys.argv[2]
db = sqlite3.connect(compact)
db.execute("ATTACH DATABASE ? AS src", (source,))
tables = [r[0] for r in db.execute("SELECT name FROM main.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
src_tables = {r[0] for r in db.execute("SELECT name FROM src.sqlite_master WHERE type='table'")}
added = 0
for t in tables:
    if t not in src_tables:
        continue
    cols = [r[1] for r in db.execute(f'PRAGMA main.table_info("{t}")')]
    src_cols = {r[1] for r in db.execute(f'PRAGMA src.table_info("{t}")')}
    common = [c for c in cols if c in src_cols]
    pk = [r[1] for r in db.execute(f'PRAGMA main.table_info("{t}")') if r[5]]
    if not common or not pk or not set(pk) <= set(common):
        continue
    col_list = ", ".join(f'"{c}"' for c in common)
    join = " AND ".join(f'm."{c}" = s."{c}"' for c in pk)
    cur = db.execute(
        f'INSERT OR IGNORE INTO main."{t}" ({col_list}) '
        f'SELECT {", ".join(f"s.{chr(34)}{c}{chr(34)}" for c in common)} FROM src."{t}" s '
        f'WHERE NOT EXISTS (SELECT 1 FROM main."{t}" m WHERE {join})')
    if cur.rowcount > 0:
        print(f"{t}: +{cur.rowcount}")
        added += cur.rowcount
db.commit()
print(f"Totale righe aggiunte: {added}")
