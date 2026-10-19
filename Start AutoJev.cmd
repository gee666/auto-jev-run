@echo off
call "%~dp0AutoJev.cmd" start --open
if errorlevel 1 (pause & exit /b 1)
