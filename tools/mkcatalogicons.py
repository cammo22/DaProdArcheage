"""
Crea server/catalog/icons.zip: le icone di tutti gli oggetti del gioco (PNG 48x48, nome = id dell'icona) per il menu /dap.
Prima si estraggono dal pak:
    dotnet PakExtract.dll <game_pak> Z:\\Archeage\\icons-raw --only game/ui/icon/
Poi:  python tools/mkcatalogicons.py
"""
import io, os, sqlite3, sys, zipfile
from PIL import Image

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(BASE, "dist", "DaProdServer", "server", "zoneclient", "game", "db", "compact_en.sqlite3")
RAW = r"Z:\Archeage\icons-raw\game\ui\icon"
OUT = os.path.join(BASE, "dist", "DaProdServer", "server", "catalog", "icons.zip")

d = sqlite3.connect("file:" + DB + "?mode=ro", uri=True)
rows = d.execute("select distinct i.icon_id, c.filename from items i join icons c on c.id = i.icon_id").fetchall()
os.makedirs(os.path.dirname(OUT), exist_ok=True)
ok = miss = 0
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
    for icon_id, fn in rows:
        p = os.path.join(RAW, fn.replace("/", os.sep))
        if not os.path.exists(p):
            miss += 1
            continue
        try:
            im = Image.open(p).convert("RGBA")
            if im.size != (48, 48):
                im = im.resize((48, 48), Image.LANCZOS)
            b = io.BytesIO(); im.save(b, "PNG", optimize=True)
            z.writestr(f"{icon_id}.png", b.getvalue()); ok += 1
        except Exception:
            miss += 1
print("icone", ok, "mancanti", miss, "->", OUT, os.path.getsize(OUT) // 1024, "KB")
