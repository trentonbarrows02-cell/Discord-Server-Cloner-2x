#!/usr/bin/env python3
"""Build a self-contained preview of the site for hosts that block outside images.

Usage:
    python3 tools/build_preview.py OUT_DIR

Writes OUT_DIR/index.html (no <html>/<head>/<body> wrapper; the host adds it)
plus OUT_DIR/img/*.webp. Product photos are downloaded once (first two per
product) and reused on later runs. The newsletter form stays on the page,
and the footer is labelled as a design preview.
"""
import json
import re
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def rep(s, old, new):
    assert old in s, f"missing: {old[:80]}"
    return s.replace(old, new, 1)


def fetch_images(out, catalog):
    (out / "img").mkdir(parents=True, exist_ok=True)

    def get(job):
        i, k, src = job
        existing = list((out / "img").glob(f"p{i:02d}-{k}.*"))
        if existing:
            return i, k, f"img/{existing[0].name}"
        url = src + ("&" if "?" in src else "?") + "width=640"
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0", "Accept": "image/webp,image/jpeg,image/png"})
        with urllib.request.urlopen(req, timeout=40) as r:
            ct, data = r.headers.get("Content-Type", ""), r.read()
        ext = {"image/webp": "webp", "image/png": "png"}.get(ct.split(";")[0], "jpg")
        (out / f"img/p{i:02d}-{k}.{ext}").write_bytes(data)
        return i, k, f"img/p{i:02d}-{k}.{ext}"

    jobs = [(i, k, src) for i, p in enumerate(catalog) for k, src in enumerate(p["img"][:2])]
    with ThreadPoolExecutor(8) as ex:
        found = list(ex.map(get, jobs))
    local = {}
    for i, k, name in found:
        local.setdefault(i, {})[k] = name
    for i, p in enumerate(catalog):
        p["img"] = [local[i][k] for k in sorted(local.get(i, {}))]
        for v in p["v"]:
            v.pop("img", None)


def main(out):
    out = Path(out)
    src = (ROOT / "assets/catalog.js").read_text()
    store = json.loads(re.search(r"STORE_URL = (.*?);\n", src).group(1))
    catalog = json.loads(re.search(r"CATALOG = (.*);\n", src).group(1))
    fetch_images(out, catalog)

    h = (ROOT / "index.html").read_text()
    css = (ROOT / "assets/styles.css").read_text()
    app = (ROOT / "assets/app.js").read_text()

    head = h[h.index("<title>"): h.index("</head>")]
    head = re.sub(r"<title>.*?</title>", "<title>Mills814 Redesign</title>", head)
    head = re.sub(r'\s*<meta name="(description|theme-color)"[^>]*>', "", head)
    head = re.sub(r'\s*<link rel="(icon|stylesheet)" href="assets/[^"]*"[^>]*>', "", head)
    body = h[h.index("<body>") + 6: h.index("</body>")]

    # Safe areas for the phone app, plus the preview label and sign-up note.
    css = rep(css, "  height: var(--nav-h);\n  transition: transform .5s",
              "  height: calc(var(--nav-h) + env(safe-area-inset-top, 0px)); padding-top: env(safe-area-inset-top, 0px);\n  transition: transform .5s")
    css = rep(css, ".drawer {\n  position: fixed; top: 8px; right: 8px; bottom: 8px;",
              ".drawer {\n  position: fixed; top: calc(8px + env(safe-area-inset-top, 0px)); right: calc(8px + env(safe-area-inset-right, 0px)); bottom: calc(8px + env(safe-area-inset-bottom, 0px));")
    css = rep(css, ".toast { position: fixed; left: 50%; bottom: 24px;",
              ".toast { position: fixed; left: 50%; bottom: calc(24px + env(safe-area-inset-bottom, 0px));")
    css += """
.preview-tag { display: inline-flex; align-items: center; gap: 8px; color: var(--muted); }
.preview-tag b { font-weight: 500; color: var(--accent-ink); background: var(--accent); padding: 3px 7px; border-radius: 6px; }
.signup__note { position: relative; grid-column: 1 / -1; margin: 0; font: 13px/1.5 var(--mono); color: var(--muted); }
.signup__note a { color: var(--accent); }
"""
    body = re.sub(r'<form class="signup__form"[^>]*>', '<form class="signup__form" id="signupForm">', body)
    body = re.sub(r'\s*<input type="hidden"[^>]*/>', "", body)
    body = rep(body, "</form>\n      </div>", '</form>\n        <p class="signup__note" id="signupNote" hidden></p>\n      </div>')
    body = rep(body, '<span>© <span id="year">2026</span> Mills814Store</span>',
               '<span class="preview-tag"><b>Design preview</b> Live store: <a href="https://mills814store.com" target="_blank" rel="noopener">mills814store.com</a></span>')
    body = rep(body, '<script src="assets/catalog.js"></script>', "")

    app = rep(app, 'const sized = (src, w) => (src ? `${src}${src.includes("?") ? "&" : "?"}width=${w}` : "");',
              'const sized = (src) => src || "";')
    app = rep(app, '$("#year").textContent = new Date().getFullYear();', """$("#signupForm").addEventListener("submit", (e) => {
    e.preventDefault();
    const note = $("#signupNote");
    note.innerHTML = 'This is a design preview, so sign-ups aren\\u2019t sent anywhere. On the live site this form adds the email to the store\\u2019s newsletter list. <a href="https://mills814store.com" target="_blank" rel="noopener">Visit the live store \\u2197</a>';
    note.hidden = false;
  });""")
    data_js = f"window.STORE_URL = {json.dumps(store)};\nwindow.CATALOG = {json.dumps(catalog, ensure_ascii=False, separators=(',', ':'))};"
    body = rep(body, '<script src="assets/app.js"></script>',
               "<script>\n" + data_js.replace("</", "<\\/") + "\n</script>\n<script>\n" + app.replace("</", "<\\/") + "\n</script>")

    page = head.strip() + "\n<style>\n" + css + "\n</style>\n" + body.strip() + "\n"
    assert "assets/" not in page and "contact#contact_form" not in page
    (out / "index.html").write_text(page)
    print(f"wrote {out / 'index.html'} ({len(page)} bytes), {len(list((out / 'img').iterdir()))} images")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "preview")
