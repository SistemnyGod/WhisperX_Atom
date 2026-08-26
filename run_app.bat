@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
set "LAUNCHER=%ROOT%scripts\launch-desktop.ps1"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

if not exist "%LAUNCHER%" (
    echo Desktop launcher was not found:
    echo %LAUNCHER%
    pause
    exit /b 1
)
if not exist "%PS%" set "PS=powershell.exe"

"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%LAUNCHER%" %*
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo.
    echo WhisperX Atom Desktop failed to start. Exit code: %EXIT_CODE%
    pause
)
endlocal & exit /b %EXIT_CODE%
