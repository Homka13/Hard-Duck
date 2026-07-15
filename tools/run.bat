@echo off
:: run.bat — Непомітний ланчер для nosuha.ps1
:: Знаходить PowerShell-скрипт у тій самій теці та виконує його
:: з -NoProfile (без скриптів профілю), -ExecutionPolicy Bypass
:: та -WindowStyle Hidden (без миготіння консолі для користувача).

set "SCRIPTDIR=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%SCRIPTDIR%nosuha.ps1"
