# Executar o DigitalGoods Hub

Este guia permite clonar e executar o projeto em outra máquina. Para entender regras e arquitetura, consulte [README.md](README.md).

## Escolha o modo de execução

| Modo | Requisitos | Serviços utilizados |
|---|---|---|
| Docker | Git, Docker Engine/Desktop com containers Linux e Docker Compose v2 | API, integrador, PostgreSQL, Redis e RabbitMQ |
| Local | Git e SDK .NET 10 | API, integrador, SQLite, cache em memória e polling |

Com Docker, não é necessário instalar .NET, PostgreSQL, Redis ou RabbitMQ no host. O SDK é usado no estágio de build da imagem. A primeira execução precisa de acesso à internet para baixar imagens e pacotes NuGet.

No Windows, o Docker Desktop precisa estar iniciado e configurado para containers Linux, com os requisitos de virtualização/WSL2 da instalação atendidos. No Linux, o usuário precisa conseguir executar Docker. Evite executar os modos Docker e local ao mesmo tempo: eles usam as mesmas portas.

Python 3 é opcional e necessário somente para os scripts `smoke.py` e `restart-smoke.py`. VS Code e a extensão C# Dev Kit são opcionais para edição/debug.

## 1. Clonar o repositório

Copie a URL real no botão **Code** do repositório GitHub. Substitua `URL_DO_REPOSITORIO` antes de executar:

```powershell
git clone "URL_DO_REPOSITORIO" DigitalGoodsHub
cd DigitalGoodsHub
```

No Windows, se quiser usar `C:\Dev`, abra o terminal nessa pasta antes de clonar. Caso tenha recebido um ZIP, extraia-o e abra a pasta que contém `compose.yaml` e `DigitalGoodsHub.slnx`.

Não é preciso criar bancos, cadastrar produtos ou configurar credenciais de um provedor real: a inicialização cria as tabelas e o catálogo de demonstração.

## 2. Executar com Docker

Verifique a instalação:

```powershell
docker version
docker compose version
```

Na raiz do projeto:

```powershell
docker compose config --quiet
docker compose up --build -d
docker compose ps
```

Aguarde a inicialização dos cinco serviços. O primeiro build pode demorar, pois baixa imagens e dependências. Confira os logs se algum serviço encerrar:

```powershell
docker compose logs --tail 80 api provider postgres redis rabbitmq
```

## 3. Verificar a aplicação

| Recurso | Endereço |
|---|---|
| Interface | http://localhost:5080 |
| Health da API | http://localhost:5080/health |
| Catálogo | http://localhost:5080/api/products |
| Diagnósticos | http://localhost:5080/api/diagnostics |
| Health do integrador | http://localhost:5081/health |
| Operações do integrador | http://localhost:5081/provider/operations |
| RabbitMQ Management | http://localhost:15672 |

No PowerShell:

```powershell
Invoke-RestMethod http://localhost:5080/health
Invoke-RestMethod http://localhost:5081/health
```

Ambos devem retornar `status: healthy`. O health verifica o banco de cada serviço; para testar o fluxo completo, execute um cenário pela interface ou o smoke test.

RabbitMQ Management: usuário `demo`, senha `demo-local-only`. As mesmas credenciais fictícias aparecem no Compose para permitir a execução local reproduzível. Não são credenciais pessoais nem de produção.

## 4. Usar a interface

1. Selecione um produto.
2. Use o telefone fictício já preenchido.
3. Selecione um cenário e clique em **Pagar e solicitar recarga**.
4. Observe estado, tentativas e resultado da API.
5. Repita com a mesma chave e conteúdo para verificar o replay. Para criar outra operação, clique em **Nova chave**.

A tela atualiza os resultados automaticamente. Nenhuma cobrança ou recarga real é realizada.

## Alternativa: executar sem Docker

Instale o **SDK .NET 10**; apenas o runtime não permite compilar. Verifique:

```powershell
dotnet --list-sdks
```

Windows, na raiz:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

Linux/macOS:

```bash
bash scripts/start-local.sh
```

O script compila e inicia API e integrador. Abra http://localhost:5080. Os bancos `digitalgoods.db` e `provider.db` ficam na pasta do projeto e são ignorados pelo Git. Ctrl+C encerra a execução.

Este modo usa SQLite, cache em memória e polling da Outbox. **Redis, RabbitMQ e PostgreSQL não participam desse modo.**

## Testes

Testes automatizados, com SDK .NET 10 instalado:

```powershell
dotnet test DigitalGoodsHub.slnx -c Release
```

Teste HTTP com os dois serviços iniciados, usando Docker ou modo local:

```powershell
python scripts/smoke.py --wait 180
```

Se no Windows o comando disponível for `py`, use `py -3 scripts/smoke.py --wait 180`.

O smoke cria dados fictícios e verifica concorrência, replay, conflito de conteúdo, seis cenários, unicidade de operações externas e consultas ao catálogo. Não é um benchmark de alta carga.

Teste opcional de queda real, com build Release e SDK .NET 10:

```powershell
dotnet build DigitalGoodsHub.slnx -c Release
python scripts/restart-smoke.py
```

Esse teste inicia seus próprios serviços SQLite nas portas `5180` e `5181`, encerra o processo da API após efeito externo e verifica a retomada. Pode levar cerca de 60s pela expiração da lease. Não valida o broker RabbitMQ.

O workflow CI executa build/testes, sobe Compose e roda o smoke no GitHub Actions. Confira o resultado da execução na aba **Actions**; a existência do workflow não significa que passou.

## Configuração

| Chave | Uso | Padrão local |
|---|---|---|
| `ConnectionStrings__Postgres` | Banco PostgreSQL da API | Não definida; usa SQLite |
| `ConnectionStrings__Sqlite` | Banco SQLite do serviço | `Data Source=digitalgoods.db;Default Timeout=30` |
| `Redis` | Endereço do cache compartilhado | Não definido; usa memória |
| `RabbitMQ` | URI AMQP | Não definida; usa polling |
| `ProviderUrl` | URL base do integrador | `http://localhost:5081/` |
| `DisableWorker` | Desabilitar processamento em testes | Não definido |

O Compose e os scripts locais fornecem os valores necessários para seus respectivos modos. Os bancos do integrador e da API devem permanecer separados.

## Parar, retomar e atualizar

```powershell
# Parar sem remover os containers
docker compose stop

# Retomar
docker compose start

# Remover os containers preservando os volumes
docker compose down

# Após atualizar o código
git pull
docker compose up --build -d
```

PostgreSQL, RabbitMQ e o banco do integrador têm volumes persistentes. Para **apagar todos os dados de demonstração** e reiniciar do zero, use `docker compose down -v` e depois suba novamente. Use isso somente quando quiser perder os dados locais desse laboratório.

## Problemas comuns

| Sintoma | O que verificar |
|---|---|
| Docker não conecta | Inicie Docker Desktop/daemon e confira `docker version` |
| Porta ocupada | Encerre outro processo/container nas portas 5080, 5081 ou 15672 |
| API não abre | Aguarde o build, confira `docker compose ps` e logs da API |
| Pagamento fica Approved/Processing | Confira logs do worker na API e do integrador; após queda real, a lease pode levar até 60s para expirar |
| Health funciona, mas processamento falha | Health verifica só o banco; confira RabbitMQ, integrador e logs |
| SDK ausente ou versão antiga | Instale SDK .NET 10, ou use o modo Docker |
| Falha ao restaurar pacotes | Verifique internet, acesso ao NuGet e logs de build |
| Erro de tabela após mudar o modelo | O MVP usa EnsureCreated, não migrations; recrie apenas os dados descartáveis do laboratório |

## Arquivos e dados locais

O `.gitignore` exclui bancos SQLite, `.env`, diretórios de build e material privado de preparação. Não substitua as credenciais de demonstração do Compose por credenciais reais para publicar o código. As rotas de simulação e diagnóstico não têm autenticação; mantenha este laboratório local.
