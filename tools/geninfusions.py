"""
Genera src/DaProd.ServerPanel/Resources/infusions.txt: gli id di tutti gli oggetti "... Infusion ..." del gioco (nome inglese),
venduti dai mercanti generici. Si rigenera con:  python tools/geninfusions.py
"""
import os, re, sqlite3

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(BASE, "dist", "DaProdServer", "server", "bin", "game", "Data", "compact.sqlite3")
LANG = os.path.join(BASE, "dist", "langpatch", "compact.sqlite3")
OUT = os.path.join(BASE, "src", "DaProd.ServerPanel", "Resources", "infusions.txt")

d = sqlite3.connect("file:" + DATA + "?mode=ro", uri=True)
l = sqlite3.connect("file:" + LANG + "?mode=ro", uri=True)
cjk = re.compile(r'[぀-ヿ㐀-鿿가-힯]')
ids = []
for idx, name in l.execute("select idx, en_us from localized_texts where tbl_name='items' and tbl_column_name='name' and lower(en_us) like '%infusion%' order by idx"):
    if cjk.search(name or ""):
        continue
    if d.execute("select 1 from items where id=?", (idx,)).fetchone():
        ids.append((idx, name))
with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    f.write("# id\tnome (tutti gli oggetti Infusion del gioco). Generato da tools/geninfusions.py\n")
    for i, n in ids:
        f.write(f"{i}\t{n}\n")
print(len(ids), "infusion ->", OUT)
