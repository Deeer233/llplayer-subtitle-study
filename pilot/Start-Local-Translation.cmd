@echo off
setlocal DisableDelayedExpansion
set "check="
if /i "%~1"=="--check" set "check=-CheckOnly"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "%~dp0..\..\pilot\start-study-candidate.ps1" -DynamicPort -Player "%~dp0LLPlayer.exe" -Server "%~dp0..\llama-b11193\mirror-verified-bin\llama-server.exe" -Model "%~dp0..\model-qwen3-pilot\Qwen3-4B-Q4_K_M.gguf" %check%
set "result=%errorlevel%"
if not "%result%"=="0" if not defined check pause
exit /b %result%
