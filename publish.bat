@echo off
taskkill /IM NetClipboard.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish"
pause
