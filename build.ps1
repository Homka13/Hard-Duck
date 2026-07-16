Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "Building Hard-Duck EXE locally..." -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

dotnet publish src/Hard-Duck.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[ERROR] Build failed!" -ForegroundColor Red
    Exit $LASTEXITCODE
}

Write-Host ""
Write-Host "[OK] Build succeeded." -ForegroundColor Green
Write-Host "Executables are located in: src\bin\Release\net8.0-windows\win-x64\publish\"
Write-Host ""
