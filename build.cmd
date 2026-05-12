@echo off
dotnet publish -c Release -o .dist
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
echo Published to .dist\OpenCodeWebService.exe
