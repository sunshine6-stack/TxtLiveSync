@echo off
rem ---------------------------------------------------------------
rem  TxtLiveSync launcher
rem  Finds a Python that can actually `import tkinter`, then starts
rem  the app. Drag a .txt onto this file to open it directly.
rem ---------------------------------------------------------------
setlocal enabledelayedexpansion
set "APP=%~dp0TxtLiveSync.pyw"
set "LIST=%TEMP%\txtlivesync_candidates.txt"

if not exist "%APP%" (
  echo.
  echo   TxtLiveSync.pyw not found next to this launcher.
  echo.
  pause
  exit /b 1
)

if exist "%LIST%" del "%LIST%" >nul 2>&1

rem --- collect candidates (order = preference) ---

rem 1) python.exe on PATH, skipping the Microsoft Store stub
for /f "delims=" %%P in ('where python 2^>nul') do (
  set "CAND=%%P"
  if "!CAND:WindowsApps=!"=="!CAND!" echo !CAND!>>"%LIST%"
)

rem 2) normal per-user / machine-wide installs
for /d %%D in ("%LOCALAPPDATA%\Programs\Python\Python*") do (
  if exist "%%~fD\python.exe" echo %%~fD\python.exe>>"%LIST%"
)
for /d %%D in ("%PROGRAMFILES%\Python*") do (
  if exist "%%~fD\python.exe" echo %%~fD\python.exe>>"%LIST%"
)
if defined PROGRAMW6432 for /d %%D in ("%PROGRAMW6432%\Python*") do (
  if exist "%%~fD\python.exe" echo %%~fD\python.exe>>"%LIST%"
)
for %%D in ("%USERPROFILE%\miniconda3" "%USERPROFILE%\anaconda3" "%LOCALAPPDATA%\anaconda3") do (
  if exist "%%~fD\python.exe" echo %%~fD\python.exe>>"%LIST%"
)

rem 3) the Python runtimes that already ship with this machine's tooling
for /d %%D in ("%USERPROFILE%\.workbuddy\binaries\python\versions\3*") do (
  if exist "%%~fD\python.exe" echo %%~fD\python.exe>>"%LIST%"
)
for /d %%D in ("C:\Python3*") do (
  if exist "%%~fD\python.exe" echo %%~fD\python.exe>>"%LIST%"
)

rem --- pick the first one where tkinter really imports ---
rem (a runtime without tkinter dies instantly and the window just
rem  flashes, so it must be validated, not guessed)

set "PY="
if exist "%LIST%" (
  for /f "usebackq delims=" %%P in ("%LIST%") do (
    if not defined PY (
      "%%P" -c "import tkinter" >nul 2>&1
      if !errorlevel! equ 0 set "PY=%%P"
    )
  )
)
if exist "%LIST%" del "%LIST%" >nul 2>&1

if not defined PY (
  echo.
  echo   No usable Python with tkinter was found on this machine.
  echo.
  echo   Install Python 3.10+ from https://www.python.org/downloads/
  echo   and keep the "tcl/tk and IDLE" option ticked, then run this again.
  echo.
  pause
  exit /b 1
)

rem A console window is unavoidable: pythonw.exe is blocked by the
rem endpoint security policy on some managed machines, python.exe is not.
cd /d "%~dp0"
if "%~1"=="" (
  start "TxtLiveSync" /min "%PY%" "%APP%"
) else (
  start "TxtLiveSync" /min "%PY%" "%APP%" "%~1"
)
exit /b 0
