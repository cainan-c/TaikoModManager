@echo off

REM =====================
REM build.bat
REM =====================

echo Building solution in Release mode...
dotnet build TaikoModManager.sln -c Release

if ERRORLEVEL 1 (
    echo Build failed. Exiting.
    pause
    exit /b 1
)

echo.
echo Publishing for x64 (net8.0-windows)...
dotnet publish .\TaikoModManager\TaikoModManager.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained false ^
    -o ".\Release\TaikoModManager"

if ERRORLEVEL 1 (
    echo Publish failed. Exiting.
    pause
    exit /b 1
)

echo.
echo Done! Output is in bin\Release\TaikoModManager-1.0.0
pause
