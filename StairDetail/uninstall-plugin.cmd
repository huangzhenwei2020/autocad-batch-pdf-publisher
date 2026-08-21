@echo off
setlocal
title WanLuo Architecture Stair Plugin Uninstaller

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall-plugin.ps1"
set "UNINSTALL_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%UNINSTALL_EXIT_CODE%"=="0" (
    echo Uninstall failed. Close AutoCAD/TArch and try again.
) else (
    echo Uninstall complete.
)
echo.
pause
exit /b %UNINSTALL_EXIT_CODE%

