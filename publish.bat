@echo off
taskkill /IM NetClipboard.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul
for /f %%v in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "([xml](Get-Content '%~dp0NetClipboard.csproj')).Project.PropertyGroup.Version"') do set APP_VERSION=%%v
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%~dp0publish\NetClipboard.exe'; $o='%~dp0publish\NetClipboard.exe.sha256'; $s=[System.IO.File]::OpenRead($p); try { $h=[BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($s)).Replace('-','').ToLowerInvariant(); Set-Content -Encoding ascii -NoNewline -Path $o -Value ($h + '  NetClipboard.exe') } finally { $s.Dispose() }"
dotnet build "%~dp0Installer\NetClipboard.Installer.wixproj" -c Release -p:ProductVersion=%APP_VERSION%
pause
