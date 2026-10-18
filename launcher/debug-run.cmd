@echo off
rem Runs Coral Launcher with errors written to debug-output.txt
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0CoralLauncher.ps1" -Debug > "%~dp0debug-output.txt" 2>&1
