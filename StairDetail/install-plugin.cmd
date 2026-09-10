@echo off
setlocal
title WanLuo Architecture Stair Plugin Installer

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-plugin.ps1"
set "INSTALL_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%INSTALL_EXIT_CODE%"=="0" (
    echo Installation failed. Close AutoCAD/TArch and try again.
) else (
    echo Installation complete. Start AutoCAD 2022/TArch and enter WLSTAIR.
)
echo.
pause
exit /b %INSTALL_EXIT_CODE%

