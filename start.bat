@echo off
cd /d "%~dp0"
dotnet run --project src\CompressionLab.Web --configuration Release --no-launch-profile -- %*
pause
