@echo off
echo ═══════════════════════════════════════════
echo   Face Recognition PoC — Quick Start
echo ═══════════════════════════════════════════
echo.

REM Check if SQL Server is running
sc query MSSQL$SQLEXPRESS >nul 2>&1
if %errorlevel% neq 0 (
    echo ⚠️  SQL Server Express does not appear to be running.
    echo    Please start it from Services or SQL Server Configuration Manager.
    echo.
    echo    Service name: SQL Server (SQLEXPRESS)
    echo.
    pause
    exit /b 1
)

echo ✅ SQL Server Express is running
echo.
echo Starting Face Recognition app...
echo.

start "" "%~dp0FaceRecApp.WPF.exe"
