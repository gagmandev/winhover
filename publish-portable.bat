@echo off
setlocal
cd /d "%~dp0"
echo ==================================================
echo   AmoWinTrans - portable build (no runtime needed)
echo ==================================================
echo.
echo This produces a single self-contained exe (~66 MB).
echo Copy it to any Windows 10/11 x64 machine and just run it.
echo.
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist-standalone
if errorlevel 1 goto :fail
echo.
echo OK. Output: dist-standalone\AmoWinTrans.exe
pause
exit /b 0
:fail
echo.
echo PUBLISH FAILED.
pause
exit /b 1
