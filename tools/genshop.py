"""
Genera sql-extra/shop-full.sql: lo shop "completo" (tutti gli oggetti del gioco con nome inglese, divisi per le schede dello shop
del client), tutto a prezzo 0. Si rigenera con:  python tools/genshop.py
Sorgenti: server\\bin\\game\\Data\\compact.sqlite3 (dati) e dist\\langpatch\\compact.sqlite3 (nomi inglesi).
"""
import os, re, sqlite3, sys

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(BASE, "dist", "DaProdServer", "server", "bin", "game", "Data", "compact.sqlite3")
LANG = os.path.join(BASE, "dist", "langpatch", "compact.sqlite3")
OUT = os.path.join(BASE, "sql-extra", "shop-full.sql")

# categoria oggetto -> [(scheda principale, sotto-scheda), ...]. Le schede 2, 3 e 5 hanno anche "Tutto" (sotto-scheda 1).
MAP = {}
def add(cats, *tabs):
    for c in cats: MAP.setdefault(c, []).extend(tabs)

add([118, 175], (5, 1), (5, 2))                    # deltaplani e aeronavi
add([92, 198, 109, 93], (5, 1), (5, 3))            # cavalcature e veicoli
add([95, 197, 176], (5, 1), (5, 4))                # animali da battaglia
add([191, 171, 117], (5, 1), (5, 5))               # animali da compagnia e giocattoli
add([10, 207, 156], (4, 3))                        # costumi
add([83, 84, 85, 121, 86, 87, 125], (4, 4))        # armature, mantelli, gioielli
add([69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 127, 128, 129, 130, 131, 132, 203], (4, 5))  # armi
add([119, 23, 24, 25, 26, 27, 28, 30, 31, 32, 38, 39, 40, 41, 42, 43, 59, 33, 58], (4, 6))                # materiali
add([12, 13, 97, 113, 114, 116, 106, 107, 62, 53], (2, 1), (2, 2))                                          # comodità: pozioni, cibo, chiavi
add([20, 152, 199, 200, 173], (2, 1), (2, 3))                                                                # affinamento / conversione
add([94, 201, 99, 133, 122, 14, 145, 138, 141, 174], (2, 1), (2, 4))                                         # altro (casse, scambi, mestieri)
add([6, 65], (3, 1), (3, 2))                       # costruzioni e progetti
add([8, 51, 21, 55, 56, 45, 46, 47, 48, 49], (3, 1), (3, 4))                                                 # colture e animali da fattoria
add([7], (3, 1), (3, 5))                           # mobili

# oggetti sempre presenti (biglietti pass)
TICKETS = [48543, 47617, 50633, 50634, 52145, 54232, 45508, 47852, 47853, 47854, 48845, 48846, 48847, 54335]  # biglietti pass, cristalli Manastorm, Story Quest Infusion

s = sqlite3.connect(DATA)
l = sqlite3.connect(LANG)
en = {r[0]: r[1] for r in l.execute("select idx,en_us from localized_texts where tbl_name='items' and tbl_column_name='name'")}
cjk = re.compile(r'[぀-ヿ㐀-鿿가-힯]')
bad = re.compile(r'\btest\b|\(test\)|dummy|^none$|do not|unused|deprecated|\[old\]', re.I)

def good(i):
    n = en.get(i)
    return bool(n) and n.strip() != "" and not cjk.search(n) and not bad.search(n)

items = {}
for iid, cat, stack in s.execute("select id, category_id, max_stack_size from items"):
    if iid in TICKETS or (cat in MAP and good(iid)):
        items[iid] = (cat, stack or 1)
print("oggetti:", len(items), file=sys.stderr)

menu, shops, skus = [], [], []
shop_id, sku_id, menu_id = 3000000, 3000000, 10000
pos = {}
def tab_pos(t):
    pos[t] = pos.get(t, 0) + 1
    return pos[t]

for iid in sorted(items):
    cat, stack = items[iid]
    tabs = MAP.get(cat, [(2, 1), (2, 2)] if iid in TICKETS else [])
    if iid in TICKETS: tabs = [(1, 2), (2, 1), (2, 2)]
    if not tabs: continue
    shop_id += 1
    shops.append(f"({shop_id}, 0, NULL, 0, 0, 0, 0, 0, 0, 0, 0, NULL, NULL, 0, -1)")
    counts = [1]
    if stack >= 10 and cat not in (83, 84, 85, 69, 70, 71, 72, 73, 74, 75, 76, 77, 79, 92, 118, 10) : counts = [1, 10]
    for k, cnt in enumerate(counts):
        sku_id += 1
        skus.append(f"({sku_id}, {shop_id}, {k}, {iid}, {cnt}, 0, {1 if k == 0 else 0}, 0, NULL, 0, 0, 0, 0, 0)")
    for t in tabs:
        menu_id += 1
        menu.append(f"({menu_id}, {t[0]}, {t[1]}, {tab_pos(t)}, {shop_id})")

def chunks(rows, n=800):
    for i in range(0, len(rows), n): yield rows[i:i + n]

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    f.write("-- Generato da tools/genshop.py: shop completo (prezzi a zero). Non modificare a mano.\n")
    f.write("DELETE FROM ics_menu WHERE shop_id >= 3000000;\nDELETE FROM ics_skus WHERE shop_id >= 3000000;\nDELETE FROM ics_shop_items WHERE shop_id >= 3000000;\n")
    for part in chunks(shops):
        f.write("INSERT INTO ics_shop_items (shop_id, display_item_id, name, limited_type, limited_stock_max, level_min, level_max, buy_restrict_type, buy_restrict_id, is_sale, is_hidden, sale_start, sale_end, shop_buttons, remaining) VALUES\n" + ",\n".join(part) + ";\n")
    for part in chunks(skus):
        f.write("INSERT INTO ics_skus (sku, shop_id, position, item_id, item_count, select_type, is_default, event_type, event_end_date, currency, price, discount_price, bonus_item_id, bonus_item_count) VALUES\n" + ",\n".join(part) + ";\n")
    for part in chunks(menu):
        f.write("INSERT INTO ics_menu (id, main_tab, sub_tab, tab_pos, shop_id) VALUES\n" + ",\n".join(part) + ";\n")
print("shop items", len(shops), "skus", len(skus), "menu", len(menu), "->", OUT, file=sys.stderr)
