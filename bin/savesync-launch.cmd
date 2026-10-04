@echo off
setlocal
rem SaveSync generic Steam launch wrapper (Windows).
rem created by Claude, 2026-09-22. Replaces the per-game spiderman-savesync.cmd.
rem
rem Steam launch options:
rem   "<install folder>\savesync-launch.cmd" <key> %%command%%
rem e.g.
rem   "C:\SaveSync\savesync-launch.cmd" spiderman %%command%%
rem   "C:\SaveSync\savesync-launch.cmd" bo1 %%command%%
rem
rem <key> is a short name from %APPDATA%\savesync\games.conf.
rem Windows launch options have no shell, so this .cmd provides one:
rem   1. restore the synced save (+ toast)  2. run the game and WAIT  3. back up (+ toast)

set KEY=%~1
if "%KEY%"=="" (
  echo SaveSync: no game key given. Usage: savesync-launch.cmd ^<key^> %%command%%
  exit /b 2
)
shift

rem Resolve siblings from this script's own folder, so the install can live anywhere.
set EXE=%~dp0SaveSync.exe
set LOG=%~dp0savesync-launch.log

rem %* still holds the original first arg, so rebuild the game command from %1 onward
set CMD=
:collect
if "%~1"=="" goto run
set CMD=%CMD% %1
shift
goto collect

:run
echo [%DATE% %TIME%] ---- launch [%KEY%] ---- >> "%LOG%"
"%EXE%" --toast restore %KEY%
echo [%DATE% %TIME%] restore done (exit %errorlevel%) >> "%LOG%"

echo [%DATE% %TIME%] game:%CMD% >> "%LOG%"
start /wait "" %CMD%

"%EXE%" --toast backup %KEY%
echo [%DATE% %TIME%] backup done (exit %errorlevel%) >> "%LOG%"
echo [%DATE% %TIME%] ---- done [%KEY%] ---- >> "%LOG%"
endlocal
