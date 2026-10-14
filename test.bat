@echo off
cd /d "%~dp0"
dotnet run --project tests\CompressionLab.SelfTest --configuration Release
pause
