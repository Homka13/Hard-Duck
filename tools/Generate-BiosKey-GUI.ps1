Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text = "Генерація ключа для BIOS-паролів"
$form.Size = [System.Drawing.Size]::new(560, 420)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false

$titleLbl = New-Object System.Windows.Forms.Label
$titleLbl.Text = "Одноразова генерація ключа (замок + ключ)"
$titleLbl.Font = [System.Drawing.Font]::new("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
$titleLbl.AutoSize = $true
$titleLbl.Location = [System.Drawing.Point]::new(20, 15)
$form.Controls.Add($titleLbl)

$descLbl = New-Object System.Windows.Forms.Label
$descLbl.Text = "Виконати ОДИН РАЗ. Публічний файл (.cer) кладеться поруч з Hard-Duck.exe і їде на всі машини.`r`nПриватний файл (.pfx) лишається тільки у вас - нікуди більше не копіювати."
$descLbl.Location = [System.Drawing.Point]::new(20, 50)
$descLbl.Size = [System.Drawing.Size]::new(510, 45)
$descLbl.ForeColor = [System.Drawing.Color]::DimGray
$form.Controls.Add($descLbl)

$folderLbl = New-Object System.Windows.Forms.Label
$folderLbl.Text = "Папка для збереження обох файлів:"
$folderLbl.Location = [System.Drawing.Point]::new(20, 105)
$folderLbl.AutoSize = $true
$form.Controls.Add($folderLbl)

$folderBox = New-Object System.Windows.Forms.TextBox
$folderBox.Location = [System.Drawing.Point]::new(20, 128)
$folderBox.Size = [System.Drawing.Size]::new(400, 25)
$folderBox.Text = Join-Path ([Environment]::GetFolderPath("Desktop")) "bios-escrow"
$form.Controls.Add($folderBox)

$browseBtn = New-Object System.Windows.Forms.Button
$browseBtn.Text = "Огляд..."
$browseBtn.Location = [System.Drawing.Point]::new(430, 126)
$browseBtn.Size = [System.Drawing.Size]::new(100, 27)
$form.Controls.Add($browseBtn)
$browseBtn.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Оберіть папку для ключів"
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $folderBox.Text = $dlg.SelectedPath
    }
})

$pwdLbl = New-Object System.Windows.Forms.Label
$pwdLbl.Text = "Пароль для захисту приватного файлу (.pfx):"
$pwdLbl.Location = [System.Drawing.Point]::new(20, 170)
$pwdLbl.AutoSize = $true
$form.Controls.Add($pwdLbl)

$pwdBox1 = New-Object System.Windows.Forms.TextBox
$pwdBox1.Location = [System.Drawing.Point]::new(20, 193)
$pwdBox1.Size = [System.Drawing.Size]::new(510, 25)
$pwdBox1.UseSystemPasswordChar = $true
$form.Controls.Add($pwdBox1)

$pwdLbl2 = New-Object System.Windows.Forms.Label
$pwdLbl2.Text = "Повторіть пароль:"
$pwdLbl2.Location = [System.Drawing.Point]::new(20, 225)
$pwdLbl2.AutoSize = $true
$form.Controls.Add($pwdLbl2)

$pwdBox2 = New-Object System.Windows.Forms.TextBox
$pwdBox2.Location = [System.Drawing.Point]::new(20, 248)
$pwdBox2.Size = [System.Drawing.Size]::new(510, 25)
$pwdBox2.UseSystemPasswordChar = $true
$form.Controls.Add($pwdBox2)

$statusLbl = New-Object System.Windows.Forms.Label
$statusLbl.Text = ""
$statusLbl.Location = [System.Drawing.Point]::new(20, 285)
$statusLbl.Size = [System.Drawing.Size]::new(510, 45)
$statusLbl.Font = [System.Drawing.Font]::new("Segoe UI", 9)
$form.Controls.Add($statusLbl)

$genBtn = New-Object System.Windows.Forms.Button
$genBtn.Text = "Згенерувати"
$genBtn.Location = [System.Drawing.Point]::new(20, 335)
$genBtn.Size = [System.Drawing.Size]::new(510, 38)
$genBtn.Font = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($genBtn)

$genBtn.Add_Click({
    $folder = $folderBox.Text.Trim()
    $pwd1 = $pwdBox1.Text
    $pwd2 = $pwdBox2.Text

    if ([string]::IsNullOrWhiteSpace($folder)) {
        [System.Windows.Forms.MessageBox]::Show("Вкажіть папку.", "Помилка", "OK", "Warning") | Out-Null
        return
    }
    if ($pwd1.Length -lt 8) {
        [System.Windows.Forms.MessageBox]::Show("Пароль для .pfx має бути хоча б 8 символів.", "Помилка", "OK", "Warning") | Out-Null
        return
    }
    if ($pwd1 -ne $pwd2) {
        [System.Windows.Forms.MessageBox]::Show("Паролі не збігаються.", "Помилка", "OK", "Warning") | Out-Null
        return
    }

    $cerPath = Join-Path $folder "bios-encrypt-public.cer"
    $pfxPath = Join-Path $folder "bios-escrow-private.pfx"

    if ((Test-Path $cerPath) -or (Test-Path $pfxPath)) {
        $overwrite = [System.Windows.Forms.MessageBox]::Show(
            "У цій папці вже є bios-encrypt-public.cer і/або bios-escrow-private.pfx.`r`n`r`nУВАГА: якщо ці файли вже використовувались для шифрування BIOS-паролів на реальних машинах, перезапис створить НОВУ пару - стару private.pfx після цього викидати НЕ можна, вона й далі потрібна для вже зашифрованих файлів.`r`n`r`nВсе одно перезаписати?",
            "Файли вже існують", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($overwrite -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    }

    try {
        $genBtn.Enabled = $false
        $statusLbl.ForeColor = [System.Drawing.Color]::DimGray
        $statusLbl.Text = "Генерую..."
        [System.Windows.Forms.Application]::DoEvents()

        if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }

        $cert = New-SelfSignedCertificate -Subject "CN=BIOS Password Escrow" -CertStoreLocation Cert:\CurrentUser\My `
            -KeyUsage KeyEncipherment, DataEncipherment -Type DocumentEncryptionCert -KeyExportPolicy Exportable

        Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

        $securePwd = New-Object System.Security.SecureString
        foreach ($c in $pwd1.ToCharArray()) { $securePwd.AppendChar($c) }
        Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null

        $pwdBox1.Text = ""; $pwdBox2.Text = ""
        $pwd1 = $null; $pwd2 = $null

        $statusLbl.ForeColor = [System.Drawing.Color]::Green
        $statusLbl.Text = "Готово! Обидва файли створено в $folder"

        [System.Windows.Forms.MessageBox]::Show(
            "Готово.`r`n`r`nbios-encrypt-public.cer - скопіюйте в папку з Hard-Duck.exe, яка піде на всі машини.`r`n`r`nbios-escrow-private.pfx - НІКУДИ не копіюйте. Заберіть у ваш захищений vault, пароль запишіть окремо від файлу.",
            "Ключі згенеровано", "OK", "Information") | Out-Null

        Start-Process explorer.exe -ArgumentList $folder
    } catch {
        $statusLbl.ForeColor = [System.Drawing.Color]::Red
        $statusLbl.Text = "Помилка: $($_.Exception.Message)"
    } finally {
        $genBtn.Enabled = $true
    }
})

[void]$form.ShowDialog()
