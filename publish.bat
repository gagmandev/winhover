@echo off
setlocal
cd /d "%~dp0"
echo ============================================
echo   AmoWinTrans - publish single-file exe
echo ============================================
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 goto :fail
echo.
echo OK. Output: dist\AmoWinTrans.exe
pause
exit /b 0
:fail
echo.
echo PUBLISH FAILED.
pause
exit /b 1
