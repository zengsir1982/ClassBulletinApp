@echo off
setlocal
cd /d "%~dp0"

REM Locate the Inno Setup compiler (ISCC.exe)
set ISCC=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo [ERROR] Inno Setup compiler (ISCC.exe) was not found.
  echo Please install Inno Setup 6 first: https://jrsoftware.org/isdl.php
  echo After installing, run this script again.
  pause
  exit /b 1
)

echo Compiling installer with %ISCC% ...
"%ISCC%" "ClassBulletinApp.iss"
if errorlevel 1 (
  echo [ERROR] Build failed. See output above.
  pause
  exit /b 1
)

echo.
echo Done. The installer (setup exe) is in the Output\ folder.
pause
