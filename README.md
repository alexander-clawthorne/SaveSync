# SaveSync

Keep PC game saves in sync with a Steam Deck (or any other machine), for games
Steam Cloud does not cover — non-Steam copies, Game Pass installs, and anything
whose cloud saves are broken or disabled.

SaveSync is a thin, controller-friendly layer over
[ludusavi](https://github.com/mtkennerly/ludusavi) for the save handling and
[Syncthing](https://syncthing.net/) for moving the files. It adds the parts that
are fiddly to do by hand: a short name per game, restore redirects so a save
from the other machine lands where this machine actually reads it, and a Steam
launch wrapper that restores before the game starts and backs up after it exits.

## How it works

```
   PC                         Syncthing                      Steam Deck
   ludusavi backup  ───▶  shared backup folder  ◀───  ludusavi backup
        ▲                                                      ▲
   restore redirect                                     restore redirect
   rewrites Deck paths                            rewrites Windows paths
   to Windows ones                                      to Deck ones
```

**The restore redirect is the load-bearing part.** Syncthing moves the files
happily, but a ludusavi backup records *absolute* paths. Without a redirect, a
restore puts the Deck's save at the Deck's path on your PC — somewhere the game
never looks. It appears as "the sync silently did nothing", and it is the single
most common way this setup fails. `add-game.ps1` sets both redirects for you.

## Requirements

- [ludusavi](https://github.com/mtkennerly/ludusavi) on both machines
- [Syncthing](https://syncthing.net/) replicating ludusavi's backup folder
- Windows 10/11 with .NET Framework 4.x (present by default) for `SaveSync.exe`
- SSH to the peer machine, if you want `add-game.ps1` to configure both ends

## Install

1. Put [`bin/`](bin) wherever you like — `C:\SaveSync` is a reasonable choice.
   Nothing in the scripts assumes a particular folder; they resolve siblings
   from their own location.
2. Build the exe:

   ```bat
   csc.exe /target:winexe /out:bin\SaveSync.exe ^
     /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll ^
     bin\SaveSync.cs
   ```

   `csc.exe` lives in `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\`. No SDK
   or downloads needed.
3. Point ludusavi's backup folder at the directory Syncthing replicates, and
   make sure the same folder is shared with the peer.

## Register a game

```powershell
.\bin\add-game.ps1 -Key spiderman `
    -Name "Marvel's Spider-Man Remastered" `
    -WindowsPath "C:/Users/<you>/Documents/Marvels Spider-Man Remastered/<steamid>" `
    -DeckPath    "/home/deck/.local/share/Steam/steamapps/compatdata/<appid>/pfx/drive_c/users/steamuser/Documents/..."
```

That is idempotent and does three things on each machine: adds the key to
`games.conf`, adds a ludusavi `customGames` entry if ludusavi cannot find the
game itself, and adds the restore redirect pointing the *other* machine's path
at this one's.

Either path may be omitted — the matching steps are skipped, so you can re-run it
later with both to fill in the redirects.

> Get the exact `-Name` from ludusavi rather than typing it. The name has to
> match ludusavi's game registry character for character.

## Use

Run `SaveSync.exe` for the window: pick a game, push or pull. It is navigable
with a D-pad, left stick, WASD or arrow keys, so it works from the couch.
Anything that overwrites a live save asks first.

| Flag                      | Behaviour                                        |
| ------------------------- | ------------------------------------------------ |
| *(none)*                  | Open the window                                   |
| `--toast <mode> <key>`    | Do the work, flash a notification, exit           |
| `--silent <mode> <key>`   | Do the work with no window at all                 |

`<mode>` is `backup` or `restore`. From the shell, `savesync.ps1` does the same
work without the GUI:

```powershell
.\bin\savesync.ps1 backup  spiderman
.\bin\savesync.ps1 restore spiderman -n     # preview, change nothing
.\bin\savesync.ps1 backup  all
.\bin\savesync.ps1 backup  list             # show the registry
.\bin\savesync.ps1 restore spiderman -b <id>  # a specific restore point
```

To hook it into Steam so it runs automatically, see
[docs/steam-launch-options.md](docs/steam-launch-options.md). The Windows form is
**not** the `%command%` one you will find in Deck guides — that is the single
most common mistake, and the doc explains why.

## Configuration

### Games — `%APPDATA%\savesync\games.conf`

One game per line, `#` for comments:

```
# <short key>|<exact ludusavi game name>
spiderman|Marvel's Spider-Man Remastered
miles|Marvel's Spider-Man: Miles Morales
bo1|Call of Duty: Black Ops
```

The same file, with the same keys, lives at `~/.config/savesync/games.conf` on
the Deck. `add-game.ps1` writes both.

### Environment variables

Nothing is compiled in. Set any of these to move things around:

| Variable             | Default                        | What it changes                            |
| -------------------- | ------------------------------ | ------------------------------------------ |
| `SAVESYNC_SCRIPT`    | `savesync.ps1` next to the exe | Which dispatcher the exe calls             |
| `SAVESYNC_LOG`       | `autopush.log` next to the exe | Where `--silent` runs are logged           |
| `SAVESYNC_ST_URL`    | `http://127.0.0.1:8384`        | Syncthing's REST endpoint                  |
| `SAVESYNC_ST_FOLDER` | `savesync-ludusavi`            | The Syncthing folder id to rescan          |
| `SAVESYNC_PEER_HOST` | `deck@steamdeck.local`         | Default `-DeckHost` for `add-game.ps1`     |
| `SAVESYNC_PREVIEW=1` | —                              | Force preview mode; nothing is written     |

`SAVESYNC_PREVIEW=1` is the safe way to test a Steam wrapper without touching a
live save.

After a push, SaveSync asks the local Syncthing instance to rescan the folder so
the transfer starts immediately rather than at the next watcher tick. It reads
the API key out of Syncthing's own `config.xml` — there is no key to paste
anywhere, and if Syncthing is not installed this step is skipped.

### Per-game hooks

Some games name their save files after the OS user — Deadpool does — so the bytes
sync fine but the other machine never reads them. A hook fixes that up locally:

```
%APPDATA%\savesync\hooks\<key>.pre-backup.cmd
%APPDATA%\savesync\hooks\<key>.post-restore.cmd
```

On the Deck: `~/.config/savesync/hooks/<key>.{pre-backup,post-restore}`.

A failing hook is reported but does not abort the sync.

## Scope

The Windows side is what lives here. The Deck side is a shell equivalent of
`savesync.ps1` plus the same `games.conf` and hooks layout; `add_game.py` runs
unchanged on both and is pushed over SSH by `add-game.ps1`.

Keep the two ends feature-equivalent. Shared logic belongs in the dispatcher, not
in the GUI.

## Licence

[MPL-2.0](LICENSE)
