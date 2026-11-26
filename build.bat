@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [Free.DBTools] dotnet CLI not found.
    echo Install the .NET SDK, or open Free.DBTools.sln with Visual Studio 2022.
    exit /b 1
)

dotnet build "Free.DBTools.sln" -c Release
if errorlevel 1 (
    echo.
    echo [Free.DBTools] Build failed.
    exit /b 1
)

echo.
echo [Free.DBTools] Build succeeded:
echo   %~dp0src\Free.DBTools\bin\Release\net48\Free.DBTools.exe
echo.
echo Keep the whole net48 folder together when distributing.

endlocal
