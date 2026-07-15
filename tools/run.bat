@echo off
:: run.bat — Непомітний ланчер для hard-duck.ps1
:: Знаходить PowerShell-скрипт у тій самій теці та виконує його
:: з -NoProfile (без скриптів профілю), -ExecutionPolicy Bypass
:: та -WindowStyle Hidden (без миготіння консолі для користувача).
::
:: Перед запуском задайте змінну середовища INFISICAL_TOKEN:
::   set INFISICAL_TOKEN=st.xxx.yyy.zzz
:: або налаштуйте її через System Properties > Environment Variables.

set "SCRIPTDIR=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%SCRIPTDIR%hard-duck.ps1"
