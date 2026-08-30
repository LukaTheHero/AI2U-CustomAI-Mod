@echo off
title AI2U Custom AI Endpoint - Installer
echo.
echo  ============================================
echo   AI2U Custom AI Endpoint - automatic setup
echo  ============================================
echo.
echo  This will find your AI2U installation, set up
echo  BepInEx for it, and install the mod.
echo.
echo  Nothing outside your game folder is touched.
echo.
pause
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1"
echo.
pause
