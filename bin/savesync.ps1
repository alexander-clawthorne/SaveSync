<#
    SaveSync dispatcher (Windows) - backup/restore one game (or all) by short key.
    Mirror of the Deck's ~/bin/savesync. created by Claude, 2026-09-18.

      savesync.ps1 backup  spiderman
      savesync.ps1 restore spiderman -n      # preview, change nothing
      savesync.ps1 backup  all
      savesync.ps1 backup  list

    Set SAVESYNC_PREVIEW=1 in the environment to force preview mode - used for
    testing the Steam wrapper without touching a live save.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('backup','restore','backups')][string]$Mode,
    [Parameter(ValueFromRemainingArguments)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'

# Absolute path on purpose: a process Steam launched may carry a stale PATH that
# predates the ludusavi install, so relying on the command name is not safe here.
$LudusaviCandidates = @(
    "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\mtkennerly.ludusavi_Microsoft.Winget.Source_8wekyb3d8bbwe\ludusavi.exe",
    "$env:LOCALAPPDATA\Microsoft\WinGet\Links\ludusavi.exe"
)
$Ludusavi = $LudusaviCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Ludusavi) {
    $cmd = Get-Command ludusavi -ErrorAction SilentlyContinue
    if ($cmd) { $Ludusavi = $cmd.Source }
}
if (-not $Ludusavi) { Write-Error 'savesync: cannot find ludusavi.exe'; exit 1 }

$Conf = Join-Path $env:APPDATA 'savesync\games.conf'
if (-not (Test-Path $Conf)) { Write-Error "savesync: no game registry at $Conf"; exit 1 }

$preview  = @()
$forcePush = $false
$key      = ''
$backupId = ''
$wantId   = $false
foreach ($a in @($Rest)) {
    if ($wantId) { $backupId = $a; $wantId = $false; continue }
    if ($a -eq '-f' -or $a -eq '--force-push') { $forcePush = $true }
    elseif ($a -eq '-n' -or $a -eq '--preview') { $preview = @('--preview') }
    elseif ($a -eq '-b' -or $a -eq '--backup') { $wantId = $true }
    elseif ($a -like '-*') { Write-Error "savesync: unknown option $a"; exit 1 }
    elseif ($a) { $key = $a }
}
if ($env:SAVESYNC_PREVIEW -eq '1') { $preview = @('--preview') }

$entries = Get-Content $Conf |
    Where-Object { $_ -notmatch '^\s*#' -and $_ -notmatch '^\s*$' } |
    ForEach-Object {
        $parts = $_ -split '\|', 2
        if ($parts.Count -eq 2) { [pscustomobject]@{ Key = $parts[0].Trim(); Name = $parts[1].Trim() } }
    }

if (-not $key -or $key -eq 'list') {
    Write-Host "Registered games ($Conf):"
    $entries | ForEach-Object { Write-Host ("  {0,-12} -> {1}" -f $_.Key, $_.Name) }
    exit 0
}

if ($key -eq 'all') {
    $names = @($entries.Name)
    if ($names.Count -eq 0) { Write-Error 'savesync: registry is empty'; exit 1 }
} else {
    $match = $entries | Where-Object { $_.Key -eq $key } | Select-Object -First 1
    if (-not $match) {
        Write-Host "savesync: unknown key $key" -ForegroundColor Red
        Write-Host ''
        Write-Host 'Known keys:'
        $entries | ForEach-Object { Write-Host ("  {0}" -f $_.Key) }
        exit 1
    }
    # @() guards against PowerShell unwrapping a single-element array into a bare
    # string - splatting a string passes it one CHARACTER at a time.
    $names = @($match.Name)
}

if ($Mode -eq 'backups') {
    # list the restore points ludusavi is currently retaining for this game
    & $Ludusavi backups $names
    exit $LASTEXITCODE
}

# ---------------------------------------------------------------- stale guard
# A backup of an OUT OF DATE local save is worse than no backup: it becomes the
# newest restore point, so the next PULL on the other machine pulls old progress.
# This happens by itself - the PC boots, the scheduled auto-push fires, and the
# save it captures may be days behind what the Deck already pushed.
#
# So before backing up: compare the newest mtime of the LIVE save files against
# the newest restore point already in the folder. If the live files are clearly
# older, this machine has nothing new to contribute - skip.
#
# The margin is deliberately generous (5 min) because the three machines do not
# share a clock; a wrong skip loses a backup of real progress, which is worse
# than a wrong push.
function Get-LiveNewestUtc {
    param([string]$GameName)
    try {
        $json = & $Ludusavi backup --preview --api $GameName 2>$null | Out-String
        if (-not $json.Trim()) { return $null }
        $obj = $json | ConvertFrom-Json
        $game = $obj.games.PSObject.Properties | Where-Object { $_.Name -eq $GameName } | Select-Object -First 1
        if (-not $game) { return $null }
        $paths = ($game.Value.files | Get-Member -MemberType NoteProperty).Name
        $newest = $null
        foreach ($f in $paths) {
            $item = Get-Item -LiteralPath $f -ErrorAction SilentlyContinue
            if ($item -and (-not $newest -or $item.LastWriteTimeUtc -gt $newest)) { $newest = $item.LastWriteTimeUtc }
        }
        return $newest
    } catch { return $null }
}

function Get-NewestBackupUtc {
    param([string]$GameName)
    try {
        $json = & $Ludusavi backups --api $GameName 2>$null | Out-String
        if (-not $json.Trim()) { return $null }
        $obj = $json | ConvertFrom-Json
        $game = $obj.games.PSObject.Properties | Where-Object { $_.Name -eq $GameName } | Select-Object -First 1
        if (-not $game) { return $null }
        $newest = $null
        foreach ($b in $game.Value.backups) {
            $t = [datetime]::Parse($b.when, [Globalization.CultureInfo]::InvariantCulture,
                                   [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
            if (-not $newest -or $t -gt $newest) { $newest = $t }
        }
        return $newest
    } catch { return $null }
}

if ($Mode -eq 'backup' -and -not $forcePush -and $preview.Count -eq 0) {
    $skipped = @()
    $keep = @()
    foreach ($n in $names) {
        $live = Get-LiveNewestUtc -GameName $n
        $back = Get-NewestBackupUtc -GameName $n
        if ($live -and $back -and $live -lt $back.AddMinutes(-5)) {
            $skipped += "$n (live save $($live.ToString('yyyy-MM-dd HH:mm')) UTC is older than newest restore point $($back.ToString('yyyy-MM-dd HH:mm')) UTC)"
        } else {
            $keep += $n
        }
    }
    foreach ($msg in $skipped) { Write-Host "savesync: SKIPPED $msg" }
    if ($keep.Count -eq 0) {
        Write-Host 'savesync: nothing to back up - this machine is behind. Pull first, or pass --force-push.'
        Write-Host 'Games: 0'
        exit 0
    }
    $names = $keep
}

# ---------------------------------------------------------------- per-game hooks
# Some games name their save files after the OS user (Deadpool does), so the bytes
# sync fine but the other machine never reads them. A hook translates locally.
#   %APPDATA%\savesync\hooks\<key>.pre-backup.cmd
#   %APPDATA%\savesync\hooks\<key>.post-restore.cmd
# Mirrors ~/.config/savesync/hooks/<key>.{pre-backup,post-restore} on the Deck.
function Invoke-SaveSyncHook {
    param([string]$Phase, [string]$GameKey)
    $hook = Join-Path $env:APPDATA ("savesync\hooks\{0}.{1}.cmd" -f $GameKey, $Phase)
    if (Test-Path $hook) {
        Write-Host "savesync: running $Phase hook for $GameKey"
        & cmd /c "`"$hook`"" $Phase
        if ($LASTEXITCODE -ne 0) { Write-Host "savesync: hook $Phase failed (continuing)" }
    }
}

if ($Mode -eq 'backup' -and $preview.Count -eq 0) {
    foreach ($n in $names) { Invoke-SaveSyncHook -Phase 'pre-backup' -GameKey $key }
}

# a specific restore point, for undoing a bad sync
$pick = @()
if ($backupId) { $pick = @('--backup', $backupId) }

# --force skips the confirmation prompt; harmless alongside --preview
& $Ludusavi $Mode --force $preview $pick $names
$code = $LASTEXITCODE

if ($Mode -eq 'restore' -and $code -eq 0 -and $preview.Count -eq 0) {
    Invoke-SaveSyncHook -Phase 'post-restore' -GameKey $key
}

exit $code
