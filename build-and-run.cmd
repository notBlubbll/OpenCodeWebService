@echo off
call build.cmd
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
echo Running .dist\OpenCodeWebService.exe ...
.dist\OpenCodeWebService.exe
