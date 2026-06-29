@echo off
chcp 65001 >nul
set PYTHONUTF8=1
set PYTHONIOENCODING=utf-8
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo Не найден ".venv\Scripts\python.exe"
    pause
    exit /b 1
)

echo Запуск автоматической транскрибации папки...
".venv\Scripts\python.exe" auto_transcribe_watch.py

echo.
echo Скрипт остановлен.
pause
