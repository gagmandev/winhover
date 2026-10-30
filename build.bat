@echo off
setlocal
cd /d "%~dp0"
echo ============================================
echo   AmoWinTrans - build (Release)
echo ============================================
dotnet build -c Release
if errorlevel 1 goto :fail
echo.
echo OK. Output: bin\Release\net8.0-windows\AmoWinTrans.exe
pause
exit /b 0
:fail
echo.
echo BUILD FAILED.
pause
exit /b 1
