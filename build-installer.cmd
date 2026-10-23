@echo off
rem Builds the per-user Windows installer (publish + Inno Setup).
rem Output: artifacts\installer\NextcloudCompanion-Setup-<version>.exe
rem Pass -Configuration / -Runtime through to build-installer.ps1 if needed.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
set RESULT=%ERRORLEVEL%

if %RESULT% neq 0 (
    echo.
    echo Installer build FAILED with exit code %RESULT%.
) else (
    echo.
    echo Installer build succeeded.
)

rem Keep the window open when launched by double-click.
echo %CMDCMDLINE% | "%SystemRoot%\System32\find.exe" /i """%~0""" >nul && pause
exit /b %RESULT%
