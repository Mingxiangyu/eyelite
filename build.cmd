@echo off
setlocal
cd /d "%~dp0"

rem ============================================================
rem  /check mode: syntax gate only. Output goes to %TEMP% and
rem  EyeLite.exe is NEVER touched, so it is safe to run while the app
rem  is running (its file is locked by the running process).
rem
rem  Why this exists: QA or the user is usually running EyeLite.exe, so
rem  a real rebuild fails on the file lock. But "does the source still
rem  compile / are the embedded resource names right?" must be
rem  verifiable at any time - otherwise a one-line typo is only found
rem  after they stop, and a whole verification round ran on a bad build.
rem  build-check.cmd in the repo root is the entry point for this mode.
rem
rem  NOTE: keep this file pure ASCII. cmd.exe reads .bat through the OEM
rem  code page; UTF-8 comments here caused parse errors (tested for real).
rem ============================================================
set "CHECK=0"
if /i "%~1"=="/check" set "CHECK=1"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto NOCSC

set "OUT=EyeLite.exe"
if "%CHECK%"=="1" set "OUT=%TEMP%\eyelite-syntax-check.exe"

rem /dev: build to EyeLite-dev.exe, so a test build never overwrites
rem EyeLite.exe. NOTE: the single-instance mutex name is a fixed string
rem ("EyeLite_SingleInstance_v1"), so this binary STILL shares the lock -
rem it cannot run while any EyeLite.exe instance is up. Use it to keep
rem the shipping file intact, not to run alongside a user session.
if /i "%~1"=="/dev" set "OUT=EyeLite-dev.exe"

echo ============================================
echo   EyeLite build script
if "%CHECK%"=="1" echo   MODE: /check (syntax gate, EyeLite.exe untouched)
echo ============================================
echo.

rem --- Close running instance, otherwise the exe file is locked ---
rem /check skips this: output lives in %TEMP%, nothing can collide,
rem and it must never kill a process that belongs to someone else.
if "%CHECK%"=="1" goto BUILD
if /i "%~1"=="/dev" goto BUILD

rem NOTE: must use findstr here, NOT find.
rem   The old line used find's /i switch. Under Git Bash / MSYS that name
rem   resolves to GNU find, which prints "find: '/i': No such file or
rem   directory" and returns errorlevel=1. The detection then silently says
rem   "not running" -> no prompt -> compile collides with the file lock
rem   (CS0016). Seen for real. findstr ships with Windows and is resolved
rem   inside cmd, so PATH order cannot break it.
tasklist /fi "imagename eq EyeLite.exe" 2>nul | findstr /I "EyeLite.exe" >nul
if errorlevel 1 goto BUILD

echo EyeLite is running. It must be closed before rebuilding.
echo (Run build-check.cmd if you only want to verify that the source compiles.)
set "KILLNOW="
set /p KILLNOW=Close it now? (Y/N):
if /i not "%KILLNOW%"=="Y" goto ABORT
taskkill /im EyeLite.exe /f >nul 2>nul
timeout /t 2 >nul

:BUILD
echo Compiler: %CSC%
echo Output:   %OUT%
echo Compiling...
echo.

rem --- embedded resources ---
rem Only the 3 actions actually wired to artwork (squat / chest-open /
rem march-in-place) plus the attribution text are embedded. Embedding all 9
rem would add ~900KB for images that are not drawn yet.
rem Keep names in sync with MoveForm.AssetResourceName() and
rem SettingsForm.LoadAttribution().
set "RES="
set "RES=%RES% /resource:assets\moves\squat\frame1.png,EyeLite.res.squat.1"
set "RES=%RES% /resource:assets\moves\squat\frame2.png,EyeLite.res.squat.2"
set "RES=%RES% /resource:assets\moves\squat\frame3.png,EyeLite.res.squat.3"
set "RES=%RES% /resource:assets\moves\chest-open\frame1.png,EyeLite.res.chest-open.1"
set "RES=%RES% /resource:assets\moves\chest-open\frame2.png,EyeLite.res.chest-open.2"
set "RES=%RES% /resource:assets\moves\chest-open\frame3.png,EyeLite.res.chest-open.3"
set "RES=%RES% /resource:assets\moves\march-in-place\frame1.png,EyeLite.res.march-in-place.1"
set "RES=%RES% /resource:assets\moves\march-in-place\frame2.png,EyeLite.res.march-in-place.2"
set "RES=%RES% /resource:assets\moves\march-in-place\frame3.png,EyeLite.res.march-in-place.3"
set "RES=%RES% /resource:assets\ATTRIBUTION.md,EyeLite.res.ATTRIBUTION.md"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- /codepage:65001 /out:"%OUT%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "src\Program.cs" %RES%

if errorlevel 1 goto FAIL

echo.
echo ============================================
echo   [OK] Build succeeded
for %%F in ("%OUT%") do echo   File: %%~nxF   Size: %%~zF bytes
echo ============================================
echo.
echo   ---- Build fingerprint (include this when reporting a problem) ----
for %%F in ("src\Program.cs") do echo   Source: %%~tF   %%~zF bytes
for %%F in ("%OUT%") do echo   Output: %%~tF   %%~zF bytes
certutil -hashfile "src\Program.cs" MD5 2>nul | findstr /v "CertUtil"
echo   ------------------------------------------------------------------
echo.

if "%CHECK%"=="1" goto CHECKCLEAN
if /i "%~1"=="/dev" goto END

set "RUNNOW="
set /p RUNNOW=Start EyeLite now? (Y/N):
if /i "%RUNNOW%"=="Y" start "" "EyeLite.exe"
if /i "%RUNNOW%"=="Y" echo Started. Look at the tray icon near the clock.
goto END

:CHECKCLEAN
rem Delete the gate artifact: no litter, and it must never be mistaken
rem for a shippable exe.
del /q "%OUT%" >nul 2>nul
echo [/check] Syntax gate passed. Temporary output removed; EyeLite.exe untouched.
goto END

:NOCSC
echo.
echo [ERROR] compiler not found. This must run on Windows.
goto END

:ABORT
echo.
echo Build cancelled. Close EyeLite first (tray - right click - Exit), then run again.
goto END

:FAIL
echo.
echo [ERROR] Compile failed. Copy everything above and send it back.

:END
echo.
pause
endlocal
