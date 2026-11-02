@echo off
setlocal
cd /d "%~dp0"
if not exist "The Northwood Lair.exe" (
  echo Extract this package into the game folder, beside The Northwood Lair.exe.
  pause
  exit /b 1
)
if not exist "The Northwood Lair_Data\Managed\Assembly-CSharp.dll" (
  echo This package requires The Northwood Lair 1.36, Unity Mono x64.
  pause
  exit /b 1
)
tasklist /FI "IMAGENAME eq vrserver.exe" /NH 2>NUL | find /I "vrserver.exe" >NUL
if errorlevel 1 (
  echo Connect the headset and start SteamVR first.
  pause
  exit /b 1
)
tasklist /FI "IMAGENAME eq The Northwood Lair.exe" /NH 2>NUL | find /I "The Northwood Lair.exe" >NUL
if not errorlevel 1 (
  echo The game is already running.
  pause
  exit /b 0
)
start "" /D "%~dp0" "%~dp0The Northwood Lair.exe" -screen-fullscreen 1 -screen-width 2560 -screen-height 1440 -window-mode borderless -logFile "%~dp0Northwood-VR.log"
