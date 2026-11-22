@echo off

REM =====================
REM build.bat
REM =====================

for /f %%i in ('git -c "safe.directory=%CD%" rev-parse HEAD 2^>nul') do set "BUILD_COMMIT=%%i"
if not defined BUILD_COMMIT set "BUILD_COMMIT=local"
set "BUILD_VERSION=0.0.0-git.%BUILD_COMMIT:~0,12%"
set "BUILD_PROPERTIES=-p:Version=%BUILD_VERSION% -p:InformationalVersion=%BUILD_VERSION%+%BUILD_COMMIT%"

echo Building commit %BUILD_COMMIT% as %BUILD_VERSION%...
echo Building solution in Release mode...
dotnet build TaikoModManager.sln -c Release %BUILD_PROPERTIES%

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
    %BUILD_PROPERTIES% ^
    -o ".\Release\TaikoModManager"

if ERRORLEVEL 1 (
    echo Publish failed. Exiting.
    pause
    exit /b 1
)

echo.
echo Done! Output is in Release\TaikoModManager (build %BUILD_VERSION%)
pause
