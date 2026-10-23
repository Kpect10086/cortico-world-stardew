@echo off
chcp 65001 >nul
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-client.ps1"
if errorlevel 1 pause
