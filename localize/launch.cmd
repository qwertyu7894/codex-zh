@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0CodexZh.ps1" -Launch
if errorlevel 1 (
  echo.
  echo If this failed, run install first:
  echo   powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0CodexZh.ps1" -Install
  pause
)
