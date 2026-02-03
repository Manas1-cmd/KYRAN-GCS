@echo off
echo ============================================
echo   KYRAN GCS — Build and Package
echo ============================================
echo.

:: 1. Publish
echo [1/3] Publishing Release build...
dotnet publish -c Release
if errorlevel 1 (
    echo ERROR: Build failed!
    pause
    exit /b 1
)

echo.
echo [2/3] Build complete!
echo   Output: bin\Release\net8.0-windows\win-x64\publish\
echo.

:: 2. Check Inno Setup
set INNO_PATH=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set INNO_PATH=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set INNO_PATH=%ProgramFiles%\Inno Setup 6\ISCC.exe

if "%INNO_PATH%"=="" (
    echo [3/3] Inno Setup not found — creating ZIP instead...
    echo.
    
    :: Fallback: ZIP
    if not exist "installer\output" mkdir "installer\output"
    
    powershell -Command "Compress-Archive -Path 'bin\Release\net8.0-windows\win-x64\publish\*' -DestinationPath 'installer\output\KYRAN-GCS-Portable.zip' -Force"
    
    echo.
    echo Done! Portable ZIP: installer\output\KYRAN-GCS-Portable.zip
    echo.
    echo To create installer: install Inno Setup 6 from https://jrsoftware.org/isdl.php
    echo Then run: "%%ProgramFiles(x86)%%\Inno Setup 6\ISCC.exe" installer\kyran-setup.iss
) else (
    echo [3/3] Building installer with Inno Setup...
    "%INNO_PATH%" installer\kyran-setup.iss
    if errorlevel 1 (
        echo ERROR: Installer build failed!
        pause
        exit /b 1
    )
    echo.
    echo Done! Installer: installer\output\KYRAN-GCS-Setup-1.0.0.exe
)

echo.
pause
