@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "FAILED=0"
for /f "tokens=1,* delims==" %%A in ('powershell.exe -NoProfile -NonInteractive -Command "$p=Join-Path $env:LOCALAPPDATA 'Jarvis/runtime-settings.json'; if(Test-Path -LiteralPath $p){$j=Get-Content -LiteralPath $p -Raw ^| ConvertFrom-Json; foreach($k in @('JARVIS_BLENDER','JARVIS_OLLAMA','JARVIS_UVX','JARVIS_OPENCODE')){if($j.$k){Write-Output ($k+'='+$j.$k)}}}"') do set "%%A=%%B"
echo Jarvis_X optional task dependencies
echo Built-in chat works without these tools.
call :tool "Ollama" ollama.exe Ollama.Ollama "%LOCALAPPDATA%\Programs\Ollama\ollama.exe"
call :tool "Blender" blender.exe BlenderFoundation.Blender "%ProgramFiles%\Blender Foundation\Blender *\blender.exe"
call :tool "uv" uvx.exe astral-sh.uv "%USERPROFILE%\.local\bin\uvx.exe"
call :tool "Node.js" npm.cmd OpenJS.NodeJS.LTS "%ProgramFiles%\nodejs\npm.cmd"
if defined JARVIS_OPENCODE if exist "%JARVIS_OPENCODE%" goto opencodeFound
if exist "%LOCALAPPDATA%\Jarvis\runtimes\OpenCode\node_modules\@opencode\cli\bin\opencode.exe" goto opencodeFound
where /q opencode.exe && goto opencodeFound
echo OpenCode is missing.
where /q npm.cmd
if errorlevel 1 (
  echo Open a new terminal after installing Node.js and run this file again.
) else (
  choice /c YN /n /m "Install OpenCode 2.4.1 for this user? [Y/N] "
  if not errorlevel 2 (
    call npm.cmd install --prefix "%LOCALAPPDATA%\Jarvis\runtimes\OpenCode" @opencode/cli@2.4.1
    if errorlevel 1 set "FAILED=1"
  )
)
goto pinokio
:opencodeFound
echo OpenCode found.
:pinokio
if exist "%USERPROFILE%\.pinokio\config.json" (echo Pinokio configuration found.) else (
  echo Pinokio is optional. Install it from https://pinokio.co/ for ComfyUI management.
)
where /q uvx.exe || goto finish
choice /c YN /n /m "Cache the pinned Blender, Desktop and Build connectors? [Y/N] "
if errorlevel 2 goto finish
uvx --python 3.14 --from mcp-for-blender==2.3.1 python -c "import blender_mcp.server"
if errorlevel 1 set "FAILED=1"
uvx --python 3.14 --from windows-mcp==0.9.0 python -c "import windows_mcp.uia"
if errorlevel 1 set "FAILED=1"
:finish
echo Reopen Jarvis and review the resolved paths in Settings.
pause
exit /b %FAILED%

:tool
if "%~2"=="blender.exe" if defined JARVIS_BLENDER if exist "%JARVIS_BLENDER%" goto found
if "%~2"=="ollama.exe" if defined JARVIS_OLLAMA if exist "%JARVIS_OLLAMA%" goto found
if "%~2"=="uvx.exe" if defined JARVIS_UVX if exist "%JARVIS_UVX%" goto found
where /q "%~2" && goto found
if exist "%~4" goto found
if "%~2"=="blender.exe" for /d %%D in ("%ProgramFiles%\Blender Foundation\Blender *") do if exist "%%~fD\blender.exe" goto found
echo %~1 is missing.
choice /c YN /n /m "Install %~1? [Y/N] "
if errorlevel 2 exit /b 0
where /q winget.exe
if errorlevel 1 (
  echo WinGet is unavailable. Install or update App Installer and retry.
  set "FAILED=1"
  exit /b 0
)
winget install --exact --id "%~3" --source winget
if errorlevel 1 (
  echo %~1 installation failed.
  set "FAILED=1"
)
exit /b 0
:found
echo %~1 found.
exit /b 0
