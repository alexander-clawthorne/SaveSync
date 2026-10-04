#!/usr/bin/env python3
"""
SaveSync - register a game on THIS machine.

Created by Claude, 2026-09-22. Runs unchanged on Windows and on the Steam Deck;
it just uses different paths. Driven by add-game.ps1, which runs it here and then
again on the Deck over ssh, so one command sets up both ends.

It does three things, all idempotent:

  1. adds  <key>|<name>  to games.conf
  2. adds a customGames entry pointing at this machine's save folder
     (only when ludusavi cannot already find the game by itself)
  3. adds a restore redirect mapping the OTHER machine's save path to this one's

Step 3 is the one people forget. Syncthing moves the files, but a backup records
absolute paths, so without the redirect a restore lands somewhere the game never
reads - which looks like "the sync silently did nothing".

Usage:
  python add_game.py --key bo1 --name "Call of Duty: Black Ops" \
      --local-path "C:/Users/<you>/Documents/Black Ops/saves" \
      --peer-path  "/home/deck/.local/share/Steam/steamapps/compatdata/12345/pfx/..." \
      [--no-custom-game] [--dry-run]
"""
import argparse
import io
import os
import platform
import re
import shutil
import subprocess
import sys
from datetime import date


def is_windows():
    return platform.system().lower().startswith("win")


def conf_path():
    if is_windows():
        return os.path.join(os.environ["APPDATA"], "savesync", "games.conf")
    return os.path.expanduser("~/.config/savesync/games.conf")


def ludusavi_config_path():
    if is_windows():
        return os.path.join(os.environ["APPDATA"], "ludusavi", "config.yaml")
    return os.path.expanduser(
        "~/.var/app/com.github.mtkennerly.ludusavi/config/ludusavi/config.yaml")


def ludusavi_cmd():
    if is_windows():
        local = os.environ.get("LOCALAPPDATA", "")
        exe = os.path.join(
            local, "Microsoft", "WinGet", "Packages",
            "mtkennerly.ludusavi_Microsoft.Winget.Source_8wekyb3d8bbwe", "ludusavi.exe")
        if os.path.exists(exe):
            return [exe]
        return ["ludusavi"]
    return ["flatpak", "run", "com.github.mtkennerly.ludusavi"]


def read(path):
    with io.open(path, encoding="utf-8") as f:
        return f.read()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def backup_file(path):
    if os.path.exists(path):
        stamp = date.today().strftime("%Y%m%d")
        dest = "%s.bak-%s" % (path, stamp)
        if not os.path.exists(dest):
            shutil.copy(path, dest)
            return dest
    return None


# ---------------------------------------------------------------- steps

def add_to_registry(key, name, dry):
    path = conf_path()
    text = read(path) if os.path.exists(path) else "# SaveSync game registry\n"
    for line in text.splitlines():
        s = line.strip()
        if s and not s.startswith("#") and s.split("|", 1)[0].strip() == key:
            return "registry: key '%s' already present - left alone" % key
    if not text.endswith("\n"):
        text += "\n"
    text += "%s|%s\n" % (key, name)
    if not dry:
        write(path, text)
    return "registry: added '%s' -> %s" % (key, name)


def ludusavi_finds_it(name):
    try:
        out = subprocess.run(ludusavi_cmd() + ["backup", "--preview", name],
                             capture_output=True, text=True, timeout=600)
        for line in (out.stdout or "").splitlines():
            s = line.strip()
            if s.startswith("Games:"):
                return not s.split(":", 1)[1].strip().startswith("0")
    except Exception:
        pass
    return False


def add_custom_game(name, local_path, dry):
    """Append a customGames entry so ludusavi knows where the saves live here."""
    path = ludusavi_config_path()
    text = read(path)
    if ('name: "%s"' % name) in text:
        return "customGames: '%s' already present - left alone" % name

    entry = ('  - name: "%s"\n'
             '    files:\n'
             '      - "%s"\n'
             '    registry: []\n') % (name, local_path)

    if re.search(r"^customGames: \[\]\s*$", text, re.M):
        text = re.sub(r"^customGames: \[\]\s*$", "customGames:\n" + entry.rstrip(), text, count=1, flags=re.M)
    elif re.search(r"^customGames:\s*$", text, re.M):
        text = re.sub(r"^customGames:\s*$", "customGames:\n" + entry.rstrip(), text, count=1, flags=re.M)
    else:
        if not text.endswith("\n"):
            text += "\n"
        text += "customGames:\n" + entry
    if not dry:
        backup_file(path)
        write(path, text)
    return "customGames: added %s -> %s" % (name, local_path)


def add_redirect(peer_path, local_path, dry):
    """Map the other machine's save path onto this machine's, for restores."""
    path = ludusavi_config_path()
    text = read(path)
    if peer_path in text:
        return "redirects: entry for that path already present - left alone"

    entry = ('  - kind: restore\n'
             '    source: "%s"\n'
             '    target: "%s"\n') % (peer_path, local_path)

    if re.search(r"^redirects: \[\]\s*$", text, re.M):
        text = re.sub(r"^redirects: \[\]\s*$", "redirects:\n" + entry.rstrip(), text, count=1, flags=re.M)
    elif re.search(r"^redirects:\s*$", text, re.M):
        text = re.sub(r"^redirects:\s*$", "redirects:\n" + entry.rstrip(), text, count=1, flags=re.M)
    else:
        return "redirects: FAILED - no 'redirects:' key found in %s" % path
    if not dry:
        backup_file(path)
        write(path, text)
    return "redirects: %s -> %s" % (peer_path, local_path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--key", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--local-path", default="")
    ap.add_argument("--peer-path", default="")
    ap.add_argument("--no-custom-game", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    host = "Windows" if is_windows() else platform.node()
    print("== SaveSync add-game on %s ==" % host)
    print("   key   : %s" % a.key)
    print("   name  : %s" % a.name)
    print()

    print(" ", add_to_registry(a.key, a.name, a.dry_run))

    if a.local_path and not a.no_custom_game:
        if ludusavi_finds_it(a.name):
            print("   customGames: ludusavi already finds this game - not adding one")
        else:
            print(" ", add_custom_game(a.name, a.local_path, a.dry_run))
    elif not a.local_path:
        print("   customGames: skipped (no --local-path given)")

    if a.peer_path and a.local_path:
        print(" ", add_redirect(a.peer_path, a.local_path, a.dry_run))
    else:
        print("   redirects: skipped (need both --peer-path and --local-path)")

    print()
    print("   verifying with a preview ...")
    try:
        out = subprocess.run(ludusavi_cmd() + ["backup", "--preview", a.name],
                             capture_output=True, text=True, timeout=600)
        for line in (out.stdout or "").splitlines():
            s = line.strip()
            if s.startswith("Games:") or s.startswith("Size:"):
                print("     " + s)
    except Exception as e:
        print("     preview failed: %s" % e)

    if a.dry_run:
        print("\n   (dry run - nothing was written)")


if __name__ == "__main__":
    main()
