@echo off
chcp 65001 >nul
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0resources\zh_subtitles\install.ps1" -Action Restore
set "subtitleExitCode=%errorlevel%"
echo.
if not "%subtitleExitCode%"=="0" echo 请按上面的提示处理后重试；详细说明见“先读我-安装教程.txt”。
pause
exit /b %subtitleExitCode%
