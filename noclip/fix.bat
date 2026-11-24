@echo off
setlocal
cd /d "%~dp0"
if not exist "noclip.cs" (echo [-] no noclip.cs & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='noclip.cs'; $t=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8); $b=[Text.Encoding]::GetEncoding(1251).GetBytes($t); $f=[Text.Encoding]::UTF8.GetString($b); [IO.File]::WriteAllText($p,$f,(New-Object Text.UTF8Encoding($true))); Write-Host '[+] text fixed'"
if errorlevel 1 (echo [-] fix failed & pause & exit /b 1)
call "%~dp0build.bat"