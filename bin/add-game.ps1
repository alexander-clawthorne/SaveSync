<#
    SaveSync - register a new game on BOTH machines in one command.

    Created by Claude, 2026-09-22.

    Runs add_game.py here, copies it to the Deck and runs it there too, with the
    paths swapped so each machine gets the right customGame and the right restore
    redirect. The Pi needs nothing - it relays whatever lands in the folder.

    Example (Miles Morales):

      .\add-game.ps1 -Key miles `
          -Name "Marvel's Spider-Man: Miles Morales" `
          -WindowsPath "C:/Users/<you>/Documents/Marvel's Spider-Man Miles Morales/<steamid>" `
          -DeckPath    "/home/deck/.local/share/Steam/steamapps/compatdata/<id>/pfx/drive_c/users/steamuser/Documents/..."

    Either path may be omitted - the matching steps are skipped and the script is
    idempotent, so re-run it later with both paths to fill in the redirects.

    Get the exact -Name from ludusavi, never by typing it:
      ludusavi find --steam-id 1817190
      ludusavi find --multiple --normalized "miles morales"

    Use forward slashes in both paths. Add -DryRun to see what it would do.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Key,
    [Parameter(Mandatory)][string]$Name,
    [string]$WindowsPath = "",
    [string]$DeckPath = "",
    # Override per-run with -DeckHost, or set SAVESYNC_PEER_HOST once.
    [string]$DeckHost = $(if ($env:SAVESYNC_PEER_HOST) { $env:SAVESYNC_PEER_HOST } else { "deck@steamdeck.local" }),
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$py = Join-Path $PSScriptRoot 'add_game.py'

# Build argument lists, omitting empty ones entirely: passing an empty string to
# argparse makes it swallow the next flag as the value.
function Build-Args([string]$local, [string]$peer) {
    $a = @('--key', $Key, '--name', $Name)
    if ($local) { $a += @('--local-path', $local) }
    if ($peer)  { $a += @('--peer-path',  $peer)  }
    if ($DryRun) { $a += '--dry-run' }
    return $a
}

# POSIX single-quote escaping: close, escape, reopen. Required because game names
# contain apostrophes (Marvel's ...), which would otherwise end the quoted string.
function Quote-Posix([string]$s) {
    return "'" + ($s -replace "'", "'\''") + "'"
}

Write-Host ""
Write-Host "==================== WINDOWS ====================" -ForegroundColor Cyan
& python $py @(Build-Args $WindowsPath $DeckPath)

Write-Host ""
Write-Host "==================== STEAM DECK ====================" -ForegroundColor Cyan
$deckUp = $false
try {
    $probe = & ssh -o BatchMode=yes -o ConnectTimeout=6 $DeckHost 'echo up' 2>&1
    $deckUp = ($probe -match 'up')
} catch { $deckUp = $false }

if (-not $deckUp) {
    Write-Host "  Deck not reachable (asleep?). Windows is done." -ForegroundColor Yellow
    Write-Host "  Re-run this later with the same arguments - it is idempotent," -ForegroundColor Yellow
    Write-Host "  so only the missing Deck half will be applied." -ForegroundColor Yellow
} else {
    & scp -o BatchMode=yes $py "${DeckHost}:/home/deck/bin/add_game.py" | Out-Null

    # paths swap over: the Deck's local path is the Deck path, its peer is Windows
    $remote = "python3 /home/deck/bin/add_game.py --key " + (Quote-Posix $Key) +
              " --name " + (Quote-Posix $Name)
    if ($DeckPath)    { $remote += " --local-path " + (Quote-Posix $DeckPath) }
    if ($WindowsPath) { $remote += " --peer-path "  + (Quote-Posix $WindowsPath) }
    if ($DryRun)      { $remote += " --dry-run" }

    & ssh -o BatchMode=yes $DeckHost $remote
}

Write-Host ""
Write-Host "==================== LAUNCH OPTIONS ====================" -ForegroundColor Cyan
Write-Host "  Windows :  `"C:\SaveSync\bin\savesync-launch.cmd`" $Key %command%"
Write-Host "  Deck    :  SAVESYNC_KEY=$Key /home/deck/bin/savesync-launch %command%"
Write-Host ""
if (-not $DeckPath) {
    Write-Host "  NOTE: no -DeckPath given, so no restore redirects were written." -ForegroundColor Yellow
    Write-Host "  Sync will work, but a cross-machine restore would land on the wrong" -ForegroundColor Yellow
    Write-Host "  path. Re-run with -DeckPath once the game exists on the Deck." -ForegroundColor Yellow
    Write-Host ""
}
Write-Host "  The 30-minute auto-push already covers every game in games.conf,"
Write-Host "  and both SaveSync.exe and the Decky plugin will list $Key."
Write-Host ""
