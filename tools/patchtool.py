"""
Gestisce patches/daprod-aaemu.patch senza git nella cartella di AAEmu.

  python tools/patchtool.py init            # una volta: salva gli originali dei file gia' toccati dalla patch
  python tools/patchtool.py track <file>... # PRIMA di modificare un file nuovo: ne salva l'originale
  python tools/patchtool.py newfile <file>  # registra un file che non esiste in AAEmu (creato da noi)
  python tools/patchtool.py build           # rigenera la patch (originali -> stato attuale)

<file> e' il percorso relativo alla radice di AAEmu (con /). Gli originali stanno in ../pristine-aaemu (fuori dalla repo).
"""
import difflib, json, os, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT_REPO = os.path.dirname(HERE)
AAEMU = os.path.abspath(os.path.join(ROOT_REPO, "..", "AAEmu repository", "AAEmu-client_version-zone-10.0.2_r575"))
PRISTINE = os.path.abspath(os.path.join(ROOT_REPO, "..", "pristine-aaemu"))
PATCH = os.path.join(ROOT_REPO, "patches", "daprod-aaemu.patch")
LIST = os.path.join(PRISTINE, "files.json")


def load():
    return json.load(open(LIST)) if os.path.exists(LIST) else {"modified": [], "new": []}


def save(d):
    os.makedirs(PRISTINE, exist_ok=True)
    json.dump(d, open(LIST, "w"), indent=1)


def rd(path):
    return open(path, "rb").read().decode("utf-8-sig").replace("\r\n", "\n")


def git(*a):
    return subprocess.run(["git", "apply", "--whitespace=nowarn", *a], cwd=AAEMU, capture_output=True, text=True)


def files_in_patch():
    mod, new = [], []
    lines = open(PATCH, encoding="utf-8").read().split("\n")
    for i, l in enumerate(lines):
        if l.startswith("+++ b/"):
            f = l[6:]
            (new if lines[i - 1] == "--- /dev/null" else mod).append(f)
    return mod, new


def copy_orig(rel):
    dst = os.path.join(PRISTINE, rel.replace("/", os.sep))
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy(os.path.join(AAEMU, rel.replace("/", os.sep)), dst)


def init():
    mod, new = files_in_patch()
    r = git("--reverse", PATCH)
    assert r.returncode == 0, r.stderr
    try:
        for f in mod: copy_orig(f)
    finally:
        r = git(PATCH)
        assert r.returncode == 0, r.stderr
    save({"modified": mod, "new": new})
    print("originali salvati:", len(mod), "modificati,", len(new), "nuovi")


def track(files):
    d = load()
    for f in files:
        if f in d["modified"] or f in d["new"]:
            print("gia' tracciato:", f); continue
        copy_orig(f)
        d["modified"].append(f)
        print("tracciato:", f)
    save(d)


def newfile(files):
    d = load()
    for f in files:
        if f not in d["new"]: d["new"].append(f)
    save(d)


def build():
    d = load()
    out = ""
    for f in d["modified"]:
        a = rd(os.path.join(PRISTINE, f.replace("/", os.sep))).splitlines(keepends=True)
        b = rd(os.path.join(AAEMU, f.replace("/", os.sep))).splitlines(keepends=True)
        out += "".join(difflib.unified_diff(a, b, "a/" + f, "b/" + f, n=3))
    for f in d["new"]:
        b = rd(os.path.join(AAEMU, f.replace("/", os.sep))).splitlines(keepends=True)
        out += "".join(difflib.unified_diff([], b, "/dev/null", "b/" + f, n=3))
    open(PATCH, "w", encoding="utf-8", newline="").write(out)
    # controllo: la patch deve risultare gia' applicata
    r = git("--check", "--reverse", PATCH)
    print("patch scritta; coerente con i sorgenti:", r.returncode == 0, r.stderr.strip()[:200])


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    {"init": init, "build": build}.get(cmd, lambda: None)() if cmd in ("init", "build") else \
        track(sys.argv[2:]) if cmd == "track" else newfile(sys.argv[2:]) if cmd == "newfile" else print(__doc__)
