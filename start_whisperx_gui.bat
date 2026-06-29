@echo off
chcp 65001 >nul
set PYTHONUTF8=1
set PYTHONIOENCODING=utf-8
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo Python venv was not found: .venv\Scripts\python.exe
    pause
    exit /b 1
)

echo Starting WhisperX GUI...
".venv\Scripts\python.exe" app.py
