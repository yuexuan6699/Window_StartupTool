@echo off
REM StartupTool - Single File Build Script
cd /d "%~dp0"

echo ============================================
echo   StartupTool - Single File Build
echo ============================================
echo.

echo [1/2] Publishing StartupTool (single file)...
dotnet publish StartupTool\StartupTool.csproj -c Release -r win-x64 --self-contained true -o build_temp -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo [2/2] Organizing output directory...
if exist "build" rmdir /s /q "build"
mkdir "build"

REM Copy single file exe
copy /y "build_temp\StartupTool.exe" "build\" > nul

REM Copy config if exists
if exist "config" (
    xcopy /y /e /i "config" "build\config\" > nul
)

REM Cleanup temp directory
rmdir /s /q "build_temp"

echo.
echo Files copied to build\ directory.
echo.
echo Run install.ps1 (as admin) to install service.
echo Or run build\StartupTool.exe to test.
echo.

pause
