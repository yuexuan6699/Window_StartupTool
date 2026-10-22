@echo off
setlocal

rem ======================================================================
rem  StartupTool MSI build script
rem  Run    : tests\build.bat  (from anywhere; paths are resolved from here)
rem  Output : build\StartupTool-Setup.msi  (contains the published exe)
rem  Needs  : .NET 10 SDK + WiX v4
rem           dotnet tool install wix --version 4.0.5 --tool-path tools
rem  Note   : this batch file is ASCII-only on purpose, so cmd.exe parses
rem           it correctly regardless of the active code page.
rem           Quiet install: msiexec /i build\StartupTool-Setup.msi /qn
rem ======================================================================

rem This script lives in tests\, the repo root is its parent folder.
for %%i in ("%~dp0..") do set "ROOT=%%~fi"
set "PROJECT=%ROOT%\src\StartupTool.App\StartupTool.App.csproj"
set "PUBLISH=%ROOT%\publish"
set "OUT=%ROOT%\build"
set "WXS=%ROOT%\StartupTool.wxs"
set "MSI=%OUT%\StartupTool-Setup.msi"

echo [1/3] Cleaning publish and build folders...
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"

echo [2/3] Publishing Release / win-x64 / self-contained single file...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%PUBLISH%" ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true
if errorlevel 1 (
    echo.
    echo [FAILED] dotnet publish failed with exit code %errorlevel%
    exit /b %errorlevel%
)

if not exist "%PUBLISH%\StartupTool.exe" (
    echo.
    echo [FAILED] %PUBLISH%\StartupTool.exe was not produced
    exit /b 20
)

echo [3/3] Building MSI with WiX v4...
rem Search order: repo-local tools\wix.exe, then wix on PATH, then the dotnet global tool shim.
set "WIX="
if exist "%ROOT%\tools\wix.exe" set "WIX=%ROOT%\tools\wix.exe"
if not defined WIX (
    where wix >nul 2>&1
    if not errorlevel 1 set "WIX=wix"
)
if not defined WIX (
    if exist "%USERPROFILE%\.dotnet\tools\wix.exe" set "WIX=%USERPROFILE%\.dotnet\tools\wix.exe"
)
if not defined WIX (
    echo.
    echo [FAILED] wix was not found. Install WiX v4 into this repo first:
    echo     dotnet tool install wix --version 4.0.5 --tool-path tools
    exit /b 30
)

rem WiX 5+ requires accepting a different license; this script targets 4.x
set "WIXVER="
for /f "tokens=1 delims=+" %%v in ('call "%WIX%" --version 2^>nul') do if not defined WIXVER set "WIXVER=%%v"
echo Using %WIX% (version %WIXVER%)
echo %WIXVER% | findstr /b /c:"4." >nul
if errorlevel 1 (
    echo.
    echo [FAILED] WiX 4.x is required, found "%WIXVER%".
    echo     dotnet tool install wix --version 4.0.5 --tool-path tools
    exit /b 32
)

"%WIX%" build -arch x64 -o "%MSI%" "%WXS%"
if errorlevel 1 (
    echo.
    echo [FAILED] wix build failed with exit code %errorlevel%
    exit /b %errorlevel%
)

if not exist "%MSI%" (
    echo.
    echo [FAILED] %MSI% was not produced
    exit /b 31
)

echo.
echo Build succeeded: %MSI%
echo Install : double-click the MSI, or run  msiexec /i "%MSI%"
echo Uninstall: Apps and features, or run  msiexec /x "%MSI%"
exit /b 0
