$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Instale o SDK .NET 10 ou use Docker: docker compose up --build -d' }
dotnet build DigitalGoodsHub.slnx
if ($LASTEXITCODE -ne 0) { throw 'Build falhou.' }
$root = (Get-Location).Path
$env:ConnectionStrings__Sqlite = "Data Source=$root/provider.db;Default Timeout=30"
$provider = Start-Process dotnet -ArgumentList @('run','--no-build','--project','src/DigitalGoodsHub.ProviderSimulator','--urls','http://localhost:5081') -PassThru -NoNewWindow
$env:ConnectionStrings__Sqlite = "Data Source=$root/digitalgoods.db;Default Timeout=30"
try {
    Write-Host 'Demo: http://localhost:5080 | Ctrl+C para encerrar'
    dotnet run --no-build --project src/DigitalGoodsHub.Api --urls http://localhost:5080
} finally { Stop-Process -Id $provider.Id -ErrorAction SilentlyContinue }
