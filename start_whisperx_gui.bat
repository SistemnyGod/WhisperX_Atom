@echo off
chcp 65001 >nul
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo Python venv was not found: .venv\Scripts\python.exe
    pause
    exit /b 1
)

echo Starting WhisperX GUI...
".venv\Scripts\python.exe" app.py
