#!/usr/bin/env python3
"""Gather pro smoke data from HLTV demos, driving an already-authenticated
Chrome so the download passes Cloudflare without any cookie handling.

Prereq: a real Chrome running with remote debugging that has already passed
HLTV's challenge once (a human, or a headed browser on a residential IP):

  google-chrome-stable --remote-debugging-port=9222 \
    --user-data-dir=<profile> https://www.hltv.org/results

This navigates that browser to HLTV, reads match + demo links, lets Chrome
download each match archive to ~/Downloads, extracts every target map's .dem,
parses smoke throws/lands (T/CT tagged) into data/<map>.prosmokes.json +
protargets.json, and deletes the multi-hundred-MB archive. Paced slowly so it
does not re-trigger the challenge; safe to stop and re-run (--append merges).

  rig/gather-hltv-browser.py
  DEMOS_PER_MAP=12 MAPS="de_inferno de_ancient" PACE=25 rig/gather-hltv-browser.py
"""
import glob
import json
import os
import subprocess
import sys
import time

BROWSER_URL = os.environ.get("BROWSER_URL", "http://127.0.0.1:9222")
DOWNLOADS = os.path.expanduser("~/Downloads")
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PARSE = os.path.join(REPO, "rig", "parse-demo-smokes.py")
WORK = os.path.expanduser("~/.cache/cs2-demo-gather")
LOG = os.path.join(REPO, "data", "gather-hltv-browser.log")

MAPS = os.environ.get("MAPS", "de_inferno de_nuke de_ancient de_anubis de_dust2 de_overpass").split()
DEMOS_PER_MAP = int(os.environ.get("DEMOS_PER_MAP", "12"))
PAGES = int(os.environ.get("PAGES", "5"))
PACE = int(os.environ.get("PACE", "18"))       # seconds between heavy actions
MATCH_CAP = int(os.environ.get("MATCH_CAP", "120"))


def log(msg):
    line = f"[{time.strftime('%H:%M:%S')}] {msg}"
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def cda(*args, timeout=180):
    env = dict(os.environ, CHROME_DEVTOOLS_AXI_BROWSER_URL=BROWSER_URL,
               CHROME_DEVTOOLS_AXI_SESSION="gather")
    try:
        r = subprocess.run(["npx", "-y", "chrome-devtools-axi", *args],
                           env=env, capture_output=True, text=True, timeout=timeout)
        return r.stdout + r.stderr
    except subprocess.TimeoutExpired:
        return ""


def ev(js):
    """Run a JS expression in the browser and decode chrome-devtools-axi's
    doubly-JSON-encoded `result: "..."` line."""
    for line in cda("eval", js).splitlines():
        s = line.strip()
        if s.startswith("result:"):
            raw = s[len("result:"):].strip()
            try:
                v = json.loads(raw)
                return json.loads(v) if isinstance(v, str) else v
            except (json.JSONDecodeError, ValueError):
                return None
    return None


def nav(url):
    cda("open", url)
    time.sleep(4)


def match_urls_for(map_name):
    seen, out = set(), []
    for off in range(0, PAGES * 100, 100):
        nav(f"https://www.hltv.org/results?map={map_name}&offset={off}")
        u = ev('() => JSON.stringify([...new Set([...document.querySelectorAll('
               '\'a[href^="/matches/"]\')].map(a => a.getAttribute("href")))])')
        if isinstance(u, list):
            for x in u:
                mid = x.split("/")[2] if x.count("/") >= 2 else x
                if mid not in seen:
                    seen.add(mid)
                    out.append(x)
        time.sleep(PACE)
    return out


def demo_path_for(match_url):
    nav("https://www.hltv.org" + match_url)
    d = ev('() => { const a = document.querySelector(\'a[href*="/download/demo/"]\'); '
           'return a ? a.getAttribute("href") : ""; }')
    return d if isinstance(d, str) and d.startswith("/download/") else None


def download(demo_path):
    before = set(glob.glob(DOWNLOADS + "/*.rar"))
    cda("newpage", "https://www.hltv.org" + demo_path)
    deadline = time.time() + 900
    while time.time() < deadline:
        time.sleep(6)
        new = set(glob.glob(DOWNLOADS + "/*.rar")) - before
        if new and not glob.glob(DOWNLOADS + "/*.crdownload"):
            rar = next(iter(new))
            s = os.path.getsize(rar)
            time.sleep(4)
            if os.path.getsize(rar) == s and s > 1_000_000:
                return rar
    return None


def parse_rar(rar):
    got = {}
    for m in MAPS:
        subprocess.run(["unrar", "e", "-o+", "-inul", rar, f"*{m[3:]}*.dem", WORK + "/"],
                       capture_output=True)
    for dem in glob.glob(WORK + "/*.dem"):
        base = os.path.basename(dem).lower()
        for m in MAPS:
            if m[3:] in base:
                r = subprocess.run(["python3", PARSE, "--map", m, "--append",
                                    "--out", os.path.join(REPO, "data", f"{m}.prosmokes.json"),
                                    "--targets", os.path.join(REPO, "data", f"{m}.protargets.json"), dem],
                                   capture_output=True, text=True)
                if "wrote" in r.stderr:
                    got[m] = got.get(m, 0) + 1
                break
        os.remove(dem)
    return got


def main():
    os.makedirs(WORK, exist_ok=True)
    counts = {m: 0 for m in MAPS}
    log(f"=== browser gather start: maps={MAPS} target={DEMOS_PER_MAP}/map pace={PACE}s ===")

    matches, seen = [], set()
    for m in MAPS:
        log(f"collecting matches for {m}")
        for mu in match_urls_for(m):
            mid = mu.split("/")[2]
            if mid not in seen:
                seen.add(mid)
                matches.append(mu)
    log(f"{len(matches)} unique candidate matches")

    processed = 0
    for mu in matches:
        if processed >= MATCH_CAP or all(counts[m] >= DEMOS_PER_MAP for m in MAPS):
            break
        dp = demo_path_for(mu)
        if not dp:
            time.sleep(PACE)
            continue
        rar = download(dp)
        if not rar:
            log(f"  {mu.split('/')[-1]}: download failed"); time.sleep(PACE); continue
        got = parse_rar(rar)
        try:
            os.remove(rar)
        except OSError:
            pass
        for m, n in got.items():
            counts[m] += n
        processed += 1
        log(f"  [{processed}] {mu.split('/')[-1]}: +{got}  totals={counts}")
        time.sleep(PACE)

    log(f"=== done: {counts} ===")
    log("rsync data/*.prosmokes.json data/*.protargets.json to the server when ready.")


if __name__ == "__main__":
    main()
