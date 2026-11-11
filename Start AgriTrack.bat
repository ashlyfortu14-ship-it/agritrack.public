@echo off
setlocal
title AgriTrack Server
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET SDK was not found. Install it from https://dotnet.microsoft.com/download
  pause
  exit /b 1
)

if not exist "api\AgriTrack.Api.csproj" (
  echo The "api" folder was not found next to this file.
  pause
  exit /b 1
)

echo Starting AgriTrack at http://localhost:3000
echo Keep this window open. Press Ctrl+C to stop the server.
start "" /b powershell -NoProfile -WindowStyle Hidden -Command "for($i=0;$i -lt 120;$i++){try{Invoke-WebRequest http://localhost:3000/api/health -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process 'http://localhost:3000'; exit}catch{};Start-Sleep -Milliseconds 500}"

dotnet run --project api
pause
