@echo off
setlocal
cd /d "%~dp0"
if errorlevel 1 goto :error

rem Use uv from PATH if available.
set "UV_EXE=uv"
where uv >nul 2>&1
if not errorlevel 1 goto :prepare

rem Otherwise install uv into the project directory.
set "UV_EXE=%~dp0.tools\uv.exe"
if exist "%UV_EXE%" goto :prepare

echo Installing uv...
set "UV_INSTALL_DIR=%~dp0.tools"
set "UV_NO_MODIFY_PATH=1"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; irm https://astral.sh/uv/install.ps1 | iex"
if errorlevel 1 goto :error
if not exist "%UV_EXE%" goto :error

:prepare
rem Create an environment; download Python 3.12 if necessary.
if exist ".venv\Scripts\python.exe" goto :dependencies

echo Preparing Python 3.12 environment...
"%UV_EXE%" venv --python 3.12 .venv
if errorlevel 1 goto :error

:dependencies
rem Recheck requirements on every launch.
echo Checking dependencies...
"%UV_EXE%" pip install --python ".venv\Scripts\python.exe" -r requirements.txt
if errorlevel 1 goto :error

echo Open http://localhost:8080 in your browser.
".venv\Scripts\python.exe" run.py %*
set "APP_EXIT_CODE=%ERRORLEVEL%"
pause
exit /b %APP_EXIT_CODE%

:error
echo Application setup failed. See the messages above.
pause
exit /b 1