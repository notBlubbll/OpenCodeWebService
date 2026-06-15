@echo off
setlocal EnableDelayedExpansion
cd /d %~dp0

call build.cmd
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $svc=(Get-CimInstance Win32_Service | Where-Object { $_.PathName -and ($_.PathName.Contains('OpenCodeWebService.exe')) } | Select-Object -First 1); if (-not $svc) { Write-Host 'No installed service points to .dist\OpenCodeWebService.exe' -ForegroundColor Red; exit 1 } Write-Host ('Found service: ' + $svc.Name + ' (' + $svc.DisplayName + ') - State: ' + $svc.State); if ($svc.State -eq 'Running') { Write-Host 'Service is already running. Stop and restart it manually to load the newly published binary.' -ForegroundColor Yellow } else { Start-Service -Name $svc.Name -ErrorAction Stop; Write-Host ('Started service ' + $svc.Name) -ForegroundColor Green } } catch { Write-Host $_ -ForegroundColor Red; exit 1 }"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
