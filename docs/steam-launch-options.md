# Steam launch options

How to make a game back up and restore its save automatically, by launching it
through the SaveSync wrapper instead of directly.

`<key>` throughout is a short name from your `games.conf`.

## Windows

**`%command%` is a Steam-for-Linux feature.** Windows Steam does not substitute
it — it just appends the launch options to the target as arguments. So on
Windows the wrapper goes in **Target** and the game exe goes in **Launch
Options**, which is the opposite of every Deck guide you will read.

Using `%command%` here makes Steam run the game with junk arguments. The symptom
is the game hanging on "Launching".

For a non-Steam shortcut, set its properties to:

| Field              | Value                                                        |
| ------------------ | ------------------------------------------------------------ |
| **Target**         | `"<install folder>\savesync-launch.cmd"`                      |
| **Start in**       | the game's folder                                             |
| **Launch options** | `<key> "<full path to the game exe>"`                         |

Worked example:

```
Target           "C:\SaveSync\savesync-launch.cmd"
Start in         D:\Games\Marvels Spider-Man Remastered\
Launch options   spiderman "D:\Games\Marvels Spider-Man Remastered\Spider-Man.exe"
```

### Notes

- Steam artwork and the shortcut name are unaffected; only what it runs changes.
- The Steam overlay may not hook, since the game is now a grandchild of `cmd`.
- Playtime still tracks — `start /wait` keeps the wrapper alive for the session.
- Run log: `savesync-launch.log`, next to the wrapper. No new entry means the
  wrapper never ran, which is the first thing to check.
- Manual push/pull any time: run `SaveSync.exe`.

## Steam Deck / Linux

`%command%` genuinely works here, so it is the normal single-field form:

```
SAVESYNC_KEY=<key> /home/deck/bin/savesync-launch %command%
```
