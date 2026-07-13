<#
    Decrypt-BiosPassword.ps1
    Для тих 1-2 людей, у кого є PRIVATE-частина сертифіката "BIOS Password Escrow".
    Читає зашифрований файл, який Harden-Workstation.ps1 залишив для конкретної машини, і показує пароль.

    Приватний сертифікат має бути ІМПОРТОВАНИЙ у Cert:\CurrentUser\My на машині,
    з якої ви це запускаєте (Import-PfxCertificate -FilePath escrow.pfx -CertStoreLocation Cert:\CurrentUser\My).

    Приклад:
        .\Decrypt-BiosPassword.ps1 -EncryptedFilePath "\\server\share\bios-LAPTOP123-PF12AB34.txt"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$EncryptedFilePath
)

if (-not (Test-Path $EncryptedFilePath)) {
    Write-Host "[FAIL] Файл не знайдено: $EncryptedFilePath" -ForegroundColor Red
    exit 1
}

try {
    $plain = Unprotect-CmsMessage -Path $EncryptedFilePath -ErrorAction Stop
    Write-Host "`nФайл: $EncryptedFilePath"
    Write-Host "BIOS-пароль: " -NoNewline
    Write-Host $plain -ForegroundColor Yellow
    Write-Host "`nНЕ залишайте цей пароль у видимому терміналі довше, ніж треба (Clear-Host після використання).`n"
} catch {
    Write-Host "[FAIL] Не вдалось розшифрувати: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Перевірте, чи імпортований приватний сертифікат 'BIOS Password Escrow' у Cert:\CurrentUser\My на цій машині." -ForegroundColor Yellow
}
