# Deploy na Oracle Cloud (OCI): VM ARM gratuita com Docker Compose

**Data:** 2026-11-11 · **Status:** design aprovado, aguardando revisão da spec

## 1. Objetivo

Colocar o SteamTracker rodando 24/7 na conta **Always Free** da OCI: o Worker
coletando preços continuamente (a razão de existir da v2) e a Api acessível
para o dono do projeto, com o histórico gravado num Postgres no próprio
servidor.

### Critérios de sucesso

1. Uma VM ARM gratuita roda `postgres`, `api` e `worker` via Docker Compose,
   com a migration aplicada por um container de uso único antes de Api e Worker
   subirem.
2. Um único comando no PC do dono (`./deploy/deploy.sh <ip>`) envia o código
   commitado, reconstrói as imagens e reinicia os serviços.
3. A Api **não** é alcançável pela internet: só por túnel SSH. A única porta
   aberta na VM é a 22.
4. Nenhum segredo de produção vai para o git, para o chat ou para o PC do dono.
5. Containers voltam sozinhos após reboot da VM ou queda do processo.
6. Verificação pós-deploy (seção 7) executada e registrada no PROGRESS.md.

### Fora de escopo

- GitHub Actions / deploy automático no merge.
- HTTPS, domínio, autenticação na Api, exposição pública.
- Backup automático do Postgres (próximo passo sugerido: `pg_dump` diário para
  o Object Storage gratuito da OCI).
- Serviços gerenciados pagos (Container Instances, OCI Database with PostgreSQL).
- Mudanças no código de produção da aplicação.

## 2. Decisões tomadas

| Decisão | Escolha | Motivo |
|---|---|---|
| Conta | Always Free já existente | Custo zero. |
| Hospedagem | 1 VM `VM.Standard.A1.Flex` (Ampere ARM), 2 OCPU / 12 GB, Ubuntu 24.04 aarch64 | Cabe folgado; mesmo `docker compose` usado localmente. AMD micro (1 GB) é pequeno demais. |
| Exposição da Api | Só túnel SSH (`ssh -L`), Api em `127.0.0.1:8080` da VM | A Api não tem autenticação; zero risco sem mudar código. |
| Atualizações | Script manual `deploy/deploy.sh` | Sem segredos no GitHub; Actions pode vir depois. |
| Build das imagens | Na própria VM (nativo `arm64`) | Evita compilação cruzada; imagens .NET 9 são multi-arquitetura. |
| Migrations em produção | EF migrations bundle num serviço `migrate` de uso único | Roda uma vez antes de Api e Worker; nada migra "sem querer" no startup. |
| Segredos | `~/steamtracker/.env` na VM (`chmod 600`), senha gerada na VM | Nunca passa pelo PC nem pelo chat. |
| Chave SSH | `ed25519` sem passphrase em `~/.ssh/steamtracker_oci` no PC do dono | Permite automatizar o deploy; a privada nunca sai do PC. Custo: quem acessa o PC acessa a VM. |

## 3. Divisão de responsabilidades

- **Dono do projeto:** cria a VM no console da OCI (seção 6.2), cola a chave
  pública e informa o IP público. Claude não cria contas nem digita credenciais.
- **Claude:** gera o par de chaves SSH, escreve os arquivos, valida localmente,
  executa bootstrap, deploy e verificação via SSH.

## 4. Artefatos

### 4.1 `Dockerfile` (raiz) — multi-stage com três alvos

| Estágio | Base | Conteúdo |
|---|---|---|
| `build` | `mcr.microsoft.com/dotnet/sdk:9.0` | Copia `.config/dotnet-tools.json` e os `.csproj` de Core, Infrastructure, Api e Worker primeiro (cache do restore), depois o código; `dotnet publish` de Api e Worker em `-c Release`; `dotnet tool restore` + `dotnet ef migrations bundle --project SteamTracker.Infrastructure --startup-project SteamTracker.Api --self-contained -r linux-<arch>` gerando `efbundle` |
| `api` | `mcr.microsoft.com/dotnet/aspnet:9.0` | Publish da Api; escuta em 8080 (padrão das imagens .NET 8+) |
| `worker` | `mcr.microsoft.com/dotnet/runtime:9.0` | Publish do Worker |
| `migrator` | `mcr.microsoft.com/dotnet/runtime-deps:9.0` | Só o `efbundle`; `ENTRYPOINT` em forma shell executa `./efbundle --connection "$ConnectionStrings__SteamTracker"` (a variável vem do compose) |

- O RID do bundle (`linux-arm64` na VM, `linux-x64` no teste local) é derivado
  da arquitetura de build (`TARGETARCH`), não fixo.
- Containers rodam como o usuário não-root `app` das imagens .NET.
- O restore/publish é feito por projeto (Api e Worker), não pela `.sln`; o
  projeto `SteamTracker.Tests` não é compilado nem entra nas imagens finais.

### 4.2 `.dockerignore`

Exclui `**/bin`, `**/obj`, `.git`, `.superpowers`, `docs`, `**/*.md`, `.env`,
`deploy`, `SteamTracker.Tests`.

### 4.3 `docker-compose.prod.yml` (raiz)

| Serviço | Configuração |
|---|---|
| `postgres` | `postgres:17`; `POSTGRES_USER=steamtracker`, `POSTGRES_DB=steamtracker`, `POSTGRES_PASSWORD=${POSTGRES_PASSWORD}`; volume nomeado `steamtracker-pgdata`; healthcheck `pg_isready`; **sem `ports`**; `restart: unless-stopped` |
| `migrate` | build alvo `migrator`; `depends_on: postgres: condition: service_healthy`; recebe a connection string; `restart: "no"` |
| `api` | build alvo `api`; `depends_on: migrate: condition: service_completed_successfully`; `ports: "127.0.0.1:8080:8080"`; `ASPNETCORE_ENVIRONMENT=Production`; `restart: unless-stopped` |
| `worker` | build alvo `worker`; `depends_on: migrate: condition: service_completed_successfully`; `DOTNET_ENVIRONMENT=Production`; sem `ports`; `restart: unless-stopped` |

- Connection string de `migrate`, `api` e `worker`:
  `Host=postgres;Port=5432;Database=steamtracker;Username=steamtracker;Password=${POSTGRES_PASSWORD}`
  (em `api`/`worker` via `ConnectionStrings__SteamTracker`).
- Rotação de logs em todos os serviços: driver `json-file`, `max-size: 10m`,
  `max-file: 3`.
- Variáveis opcionais do `.env` repassadas a `api`/`worker` para ajuste sem
  rebuild: `SteamMarket__Currency`, `PriceCollection__Interval`,
  `PriceCollection__DelayBetweenItems` (sem valor no `.env` → vale o
  `appsettings.json`).

### 4.4 `.env.example` (raiz) e `.gitignore`

`.env.example` lista `POSTGRES_PASSWORD=` e as variáveis opcionais comentadas.
`.env` entra no `.gitignore`.

### 4.5 `deploy/bootstrap.sh` (roda uma vez na VM)

Idempotente. Instala Docker Engine + plugin Compose pelo repositório apt oficial
da Docker (Ubuntu, `arm64`), habilita o serviço `docker` no boot e adiciona o
usuário `ubuntu` ao grupo `docker`. Não abre portas no firewall.

### 4.6 `deploy/deploy.sh` (roda no PC do dono, Git Bash)

Uso: `./deploy/deploy.sh <ip> [caminho-da-chave]` (chave padrão
`~/.ssh/steamtracker_oci`, usuário `ubuntu`).

1. Avisa (sem bloquear) se houver mudanças não commitadas: só o `HEAD` é enviado.
2. `git archive --format=tar HEAD` → via SSH → `~/steamtracker/src` na VM, que é
   esvaziado antes (o `.env` e o volume do banco ficam fora dessa pasta).
3. Se `~/steamtracker/.env` não existir, cria com
   `POSTGRES_PASSWORD=$(openssl rand -hex 32)` e `chmod 600`. Se existir, não toca.
4. `docker compose -f docker-compose.prod.yml --env-file ~/steamtracker/.env up -d --build`.
5. Mostra `docker compose ps` e as últimas linhas de log do `migrate`.
6. Sai com código ≠ 0 se o `migrate` não terminar com sucesso.

### 4.7 `deploy/README.md` (runbook)

Primeiro uso (chave, criar VM, bootstrap), deploy, túnel
(`ssh -i ~/.ssh/steamtracker_oci -N -o ExitOnForwardFailure=yes -L 18080:127.0.0.1:8080 ubuntu@<ip>`), logs,
reinício, `psql` no banco (`docker compose exec postgres psql -U steamtracker`),
e onde mora o `.env`.

## 5. Validação local (antes da nuvem)

Com o Docker local: subir `docker-compose.prod.yml` com um `.env` temporário de
teste (fora do repo) e confirmar: build dos três alvos, `migrate` sai com 0,
`api` e `worker` sobem, `GET /api/items/{id}/snapshots` responde 404 para Guid
aleatório via `127.0.0.1:8080`. Derrubar e remover o volume de teste ao final.
Se o Postgres de desenvolvimento (`docker-compose.yml`) estiver rodando, o de
produção não conflita (não publica porta), mas a porta 8080 local precisa
estar livre.

## 6. Primeiro deploy

### 6.1 Chave SSH
Claude gera `~/.ssh/steamtracker_oci` (`ed25519`, sem passphrase) e mostra a
chave **pública** para o dono colar no console.

### 6.2 Criação da VM (dono, console da OCI)
Compute → Instances → Create instance: nome `steamtracker`; imagem Canonical
Ubuntu 24.04 (aarch64); shape `VM.Standard.A1.Flex` 2 OCPU / 12 GB; VCN padrão
com sub-rede pública e IPv4 público; colar a chave pública; boot volume padrão.
Se aparecer "Out of capacity": tentar outro availability domain ou repetir mais
tarde. Informar o IP público.

### 6.3 Bootstrap e deploy
`ssh` para aceitar a host key, rodar `deploy/bootstrap.sh` na VM, reconectar
(grupo `docker`), rodar `./deploy/deploy.sh <ip>`.

## 7. Verificação pós-deploy

1. **Steam a partir da VM:** `curl` em `priceoverview` para `Prisma Case` e para
   um item inexistente — registra status e corpo (responde a pendência aberta
   desde o Dia 8).
2. `docker compose ps`: `migrate` com exit 0; `postgres`, `api`, `worker` up.
3. Pelo túnel: `POST /api/items` com `Prisma Case` → 201 (ou 503 se a Steam não
   responder da VM); `GET /api/items/{id}/snapshots` mostra o primeiro preço.
4. Log do Worker contém `Coleta concluída:` após o primeiro ciclo.
5. De fora: `curl http://<ip>:8080` não responde (timeout/recusado).
6. `sudo reboot` da VM → após voltar, os quatro estados do item 2 se repetem
   sem intervenção.

Se a Steam não responder da VM, o deploy é considerado válido mesmo assim: o
Worker registra falhas no log até ela voltar; os itens 3 (com 201) e 4 ficam
pendentes no PROGRESS.md.

## 8. Riscos

- **Capacidade Ampere esgotada** na região: pode atrasar a criação da VM.
- **Steam bloqueando IPs de nuvem**: possível; a verificação 7.1 detecta.
- **Disco/volume único**: sem backup neste ciclo; perda da VM perde o histórico.
- **Chave sem passphrase** no PC do dono: aceito explicitamente.
- **Recuperação de VM ociosa pela Oracle**: contas Always Free podem ter
  instâncias com uso muito baixo recuperadas; o Worker rodando a cada 15 min
  gera algum uso, mas não há garantia — anotar se acontecer.
