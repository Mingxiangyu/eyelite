@echo off
REM ============================================================
REM  打包发版：编译 -> 生成 dist\EyeLite-<版本>.zip
REM  双击即可。产物在 dist\ 目录下。
REM ============================================================
setlocal
cd /d "%~dp0"

call build.cmd
if errorlevel 1 (
    echo.
    echo [FAIL] 编译失败，未打包。
    pause
    exit /b 1
)

python "tools\make-release.py"
echo.
pause
