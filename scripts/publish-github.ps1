param([string]$RepositoryName = 'DigitalGoodsHub')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
foreach ($tool in @('git', 'gh', 'dotnet')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Instale $tool antes de publicar." }
}
gh auth status
if ($LASTEXITCODE -ne 0) { throw 'Execute gh auth login e tente novamente.' }
dotnet test DigitalGoodsHub.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw 'Testes falharam; publicação interrompida.' }
if (-not (Test-Path '.git')) {
    git init -b main
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao iniciar Git.' }
}
$remotes = @(git remote)
if ($remotes -contains 'origin') { throw 'Esta pasta já possui origin. Confira antes de publicar manualmente.' }
git add .
git commit -m 'feat: DigitalGoods Hub resilience MVP'
if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar commit.' }
gh repo create $RepositoryName --public --source . --remote origin --push --description 'C# .NET resilience lab: idempotency, Outbox/Inbox, RabbitMQ, Redis and provider reconciliation'
if ($LASTEXITCODE -ne 0) { throw 'Publicação não concluída. Confira se o nome já existe ou se há permissão.' }
