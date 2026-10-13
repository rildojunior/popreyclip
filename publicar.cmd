@echo off
rem Gera dist\PoPreyClip.exe, o app com interface (arquivo unico, nao precisa do .NET instalado).
cd /d "%~dp0"
set "DOTNET=dotnet"
where dotnet >nul 2>nul || set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
"%DOTNET%" publish src\PoPreyClip.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
if errorlevel 1 (echo Falha ao gerar o executavel. & pause & exit /b 1)
echo.
echo Pronto: %~dp0dist\PoPreyClip.exe
pause
