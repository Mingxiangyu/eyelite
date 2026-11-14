@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto NOCSC

echo ============================================
echo   EyeLite build script
echo ============================================
echo.

rem --- Close running instance, otherwise the exe file is locked ---
tasklist /fi "imagename eq EyeLite.exe" 2>nul | find /i "EyeLite.exe" >nul
if errorlevel 1 goto BUILD

echo EyeLite is running. It must be closed before rebuilding.
set "KILLNOW="
set /p KILLNOW=Close it now? (Y/N):
if /i not "%KILLNOW%"=="Y" goto ABORT
taskkill /im EyeLite.exe /f >nul 2>nul
timeout /t 2 >nul

:BUILD
echo Compiler: %CSC%
echo Compiling...
echo.

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- /codepage:65001 /out:"EyeLite.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "src\Program.cs"

if errorlevel 1 goto FAIL

echo.
echo ============================================
echo   [OK] Build succeeded
for %%F in (EyeLite.exe) do echo   File: EyeLite.exe   Size: %%~zF bytes
echo ============================================
echo.
echo   ---- Build fingerprint (include this when reporting a problem) ----
for %%F in ("src\Program.cs") do echo   Source: %%~tF   %%~zF bytes
for %%F in ("EyeLite.exe") do echo   Output: %%~tF   %%~zF bytes
certutil -hashfile "src\Program.cs" MD5 2>nul | findstr /v "CertUtil"
echo   ------------------------------------------------------------------
echo.
set "RUNNOW="
set /p RUNNOW=Start EyeLite now? (Y/N):
if /i "%RUNNOW%"=="Y" start "" "EyeLite.exe"
if /i "%RUNNOW%"=="Y" echo Started. Look at the tray icon near the clock.
goto END

:NOCSC
echo.
echo [ERROR] csc.exe not found. This must run on Windows.
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
