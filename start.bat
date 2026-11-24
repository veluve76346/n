@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "MAIN=%ROOT%\tools\run.ps1"

if not exist "%MAIN%" (
    echo [-] missing: "%MAIN%"
    pause
    exit /b 1
)

net session >nul 2>&1
if not errorlevel 1 goto run

echo [*] requesting administrator rights...
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" 2>nul
exit /b

:run
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%MAIN%" -Root "%ROOT%" -Elevated
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (echo [-] exit code %RC%) else (echo [+] done)
pause
endlocal & exit /b %RC%
