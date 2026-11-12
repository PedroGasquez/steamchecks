# Deploy na OCI: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rodar Postgres, Api e Worker 24/7 numa VM ARM Always Free da OCI via Docker Compose, com deploy por script e Api acessível só por túnel SSH.

**Architecture:** Um `Dockerfile` multi-stage (alvos `api`, `worker`, `migrator`) e um `docker-compose.prod.yml` validados primeiro no Docker local; scripts `deploy/bootstrap.sh` (uma vez, na VM) e `deploy/deploy.sh` (no PC, envia o `HEAD` via `git archive | ssh`); VM criada pelo dono no console; verificação pós-deploy registrada no PROGRESS.md.

**Tech Stack:** Docker Engine + Compose v2, imagens `mcr.microsoft.com/dotnet/{sdk,aspnet,runtime,runtime-deps}:9.0`, `postgres:17`, EF Core 9 migrations bundle, Bash, OpenSSH, Ubuntu 24.04 aarch64.

**Spec:** `docs/superpowers/specs/2026-11-11-deploy-oci-design.md`

## Global Constraints

- **Nenhuma mudança no código de produção** da aplicação (`SteamTracker.*/*.cs`, `*.csproj`, `appsettings*.json`).
- Imagens .NET na linha **9.0**; `postgres:17`.
- Api publicada só em `127.0.0.1:8080`; Postgres sem `ports`; única porta aberta na VM: 22.
- Usuário/banco do Postgres de produção: `steamtracker` / `steamtracker`; senha em `POSTGRES_PASSWORD` no `~/steamtracker/.env` da VM (`chmod 600`), gerada com `openssl rand -hex 32` só se o arquivo não existir.
- Connection string: `Host=postgres;Port=5432;Database=steamtracker;Username=steamtracker;Password=${POSTGRES_PASSWORD}`.
- Ambiente: `ASPNETCORE_ENVIRONMENT=Production` (api), `DOTNET_ENVIRONMENT=Production` (worker).
- Logs: `json-file`, `max-size: 10m`, `max-file: 3` em todos os serviços.
- Chave SSH: `~/.ssh/steamtracker_oci` (`ed25519`, sem passphrase); usuário remoto `ubuntu`; código em `~/steamtracker/src`, segredo em `~/steamtracker/.env`.
- Nenhum segredo de produção no git, no chat ou no PC. `.env` no `.gitignore`.
- Scripts `.sh` com fim de linha **LF** (via `.gitattributes`).
- Commits terminam com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Scripts com CRLF** (checkout no Windows com `autocrlf`) quebram no Ubuntu e no Git Bash com `$'\r': command not found`. → `.gitattributes` com `*.sh text eol=lf` e checagem `file`/`grep -c $'\r'` na Task 3.
2. **Redeploy não pode trocar a senha nem apagar o banco.** → Task 5 compara o hash do `.env` e a contagem de linhas de `TrackedItems` antes e depois de um segundo `deploy.sh`.
3. **Migration falhando** (senha errada, banco fora) não pode deixar Api/Worker subirem nem o script reportar sucesso. → Task 2 roda o compose com senha divergente e espera `migrate` com exit ≠ 0, `api` não iniciada; Task 3 faz o `deploy.sh` sair ≠ 0 nesse caso.
4. **Porta 8080 alcançável de fora.** → Task 2 confere `docker port` (só `127.0.0.1`); Task 5 tenta `curl http://<ip>:8080` de fora.
5. **Reboot da VM.** → Task 5 reinicia a VM e confere que `postgres`, `api` e `worker` voltam sem intervenção.

---

## File Structure

| Arquivo | Responsabilidade |
|---|---|
| `Dockerfile` (novo) | build multi-stage: `build`, `api`, `worker`, `migrator` |
| `.dockerignore` (novo) | contexto de build enxuto |
| `docker-compose.prod.yml` (novo) | serviços de produção e ordem de subida |
| `.env.example` (novo) | nomes das variáveis, sem valores reais |
| `.gitignore` (modificar) | ignorar `.env` |
| `.gitattributes` (novo) | `*.sh text eol=lf` |
| `deploy/bootstrap.sh` (novo) | instalar Docker na VM (idempotente) |
| `deploy/deploy.sh` (novo) | enviar `HEAD`, garantir `.env`, `compose up --build` |
| `deploy/README.md` (novo) | runbook operacional |
| `PROGRESS.md` (modificar) | registro do deploy e da verificação |

---

### Task 1: `Dockerfile` multi-stage e `.dockerignore`

**Files:**
- Create: `Dockerfile`, `.dockerignore`

**Interfaces:**
- Produces: alvos `api` (escuta 8080), `worker`, `migrator` (`ENTRYPOINT` shell: `./efbundle --connection "$ConnectionStrings__SteamTracker"`).

- [ ] **Step 1: Escrever `.dockerignore`**

Conteúdo exato da spec §4.2: `**/bin`, `**/obj`, `.git`, `.superpowers`, `docs`, `**/*.md`, `.env`, `deploy`, `SteamTracker.Tests`.

- [ ] **Step 2: Escrever o `Dockerfile`**

- Estágio `build` (`sdk:9.0`, `WORKDIR /src`): copiar `.config/dotnet-tools.json` e os quatro `.csproj` (Core, Infrastructure, Api, Worker) mantendo as pastas; `dotnet restore` de Api e de Worker; `dotnet tool restore`; copiar o restante do código; `dotnet publish SteamTracker.Api -c Release --no-restore -o /out/api`; idem Worker para `/out/worker`; bundle com `dotnet ef migrations bundle --project SteamTracker.Infrastructure --startup-project SteamTracker.Api --self-contained -r linux-$RID -o /out/efbundle`, onde `RID` é `arm64` se `TARGETARCH=arm64`, senão `x64` (declarar `ARG TARGETARCH`).
- `api`: `aspnet:9.0`, `WORKDIR /app`, copiar `/out/api`, `USER app`, `ENTRYPOINT ["dotnet", "SteamTracker.Api.dll"]`.
- `worker`: `runtime:9.0`, copiar `/out/worker`, `USER app`, `ENTRYPOINT ["dotnet", "SteamTracker.Worker.dll"]`.
- `migrator`: `runtime-deps:9.0`, copiar `/out/efbundle`, `USER app`, `ENTRYPOINT ./efbundle --connection "$ConnectionStrings__SteamTracker"`.

- [ ] **Step 3: Buildar os três alvos localmente**

Run: `docker build --target api -t steamtracker-api:local .` e o mesmo para `worker` e `migrator`.
Expected: três builds concluídos; o segundo e o terceiro reaproveitam o estágio `build` do cache (`CACHED`).

- [ ] **Step 4: Conferir conteúdo das imagens**

Run: `docker run --rm --entrypoint ls steamtracker-api:local /app | grep -c "SteamTracker.Tests"` → `0`; `docker run --rm --entrypoint id steamtracker-worker:local` → usuário `app`, não `root`.

- [ ] **Step 5: Commit**

```bash
git add Dockerfile .dockerignore
git commit -m "build: Dockerfile multi-stage com alvos api, worker e migrator"
```

---

### Task 2: `docker-compose.prod.yml`, `.env.example`, `.gitignore` — validação local

**Files:**
- Create: `docker-compose.prod.yml`, `.env.example`
- Modify: `.gitignore` (+ `.env`)

**Interfaces:**
- Consumes: alvos da Task 1.
- Produces: serviços `postgres`, `migrate`, `api`, `worker`; volume `steamtracker-pgdata`; variáveis lidas do `--env-file`: `POSTGRES_PASSWORD` (obrigatória), `SteamMarket__Currency`, `PriceCollection__Interval`, `PriceCollection__DelayBetweenItems` (opcionais).

- [ ] **Step 1: Escrever `docker-compose.prod.yml`** conforme spec §4.3 e Global Constraints. Decisão sobre as variáveis opcionais: **não** usar `environment: SteamMarket__Currency: ${SteamMarket__Currency:-}` (variável ausente viraria string vazia e sobrescreveria o `appsettings.json`). Em vez disso, `api` e `worker` recebem o próprio `.env` via `env_file: - ${STEAMTRACKER_ENV_FILE:?defina STEAMTRACKER_ENV_FILE}`: só as chaves presentes no arquivo chegam ao container. `environment` fica só com os valores fixos (ambiente e connection string). `postgres` e `migrate` não usam `env_file`.
- [ ] **Step 2: Escrever `.env.example`** (`POSTGRES_PASSWORD=` + as três opcionais comentadas, com os valores do `appsettings.json` como exemplo) e adicionar `.env` ao `.gitignore`.

Todos os comandos `docker compose` desta task rodam com `STEAMTRACKER_ENV_FILE=<scratchpad>/test.env` e `--env-file <scratchpad>/test.env`; `test.env` começa com só `POSTGRES_PASSWORD=teste123`.

- [ ] **Step 3: Validar sintaxe**

Run: `docker compose -f docker-compose.prod.yml config -q`
Expected: sem saída, exit 0. Sem `STEAMTRACKER_ENV_FILE` definida: erro citando a variável.

- [ ] **Step 4: Subir localmente (caminho feliz)**

Run: `docker compose -p steamtracker-prodtest -f docker-compose.prod.yml --env-file <test.env> up -d --build --wait api worker` (projeto `-p` separado do compose de desenvolvimento).
Expected: `migrate` com `Exited (0)`; `postgres`, `api`, `worker` `Up`.

- [ ] **Step 5: Conferir comportamento**
  - `docker port steamtracker-prodtest-api-1` → só `127.0.0.1:8080`.
  - `curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8080/api/items/00000000-0000-0000-0000-000000000001/snapshots` → `404`.
  - `docker compose -p steamtracker-prodtest -f docker-compose.prod.yml logs worker` contém `Coleta concluída: 0 salvos, 0 pulados, 0 falhas`.
  - `docker inspect` de um container → `LogConfig` com `max-size=10m`, `max-file=3`.
  - `docker compose exec worker printenv PriceCollection__Interval` → vazio/exit 1 (ausente no `.env`, vale o `appsettings.json`).

- [ ] **Step 6: Migration falhando bloqueia a subida (Review Focus 3)**

Derrubar mantendo o volume (`down`), trocar `POSTGRES_PASSWORD` no `test.env` para `outra` e rodar `up -d` de novo.
Expected: `migrate` com exit ≠ 0 (autenticação falha porque o volume já tem a senha antiga); `api` e `worker` não ficam `Up`.

- [ ] **Step 7: Limpar** — `docker compose -p steamtracker-prodtest -f docker-compose.prod.yml down -v` e apagar o `test.env`.

- [ ] **Step 8: Commit**

```bash
git add docker-compose.prod.yml .env.example .gitignore
git commit -m "build: compose de producao com migrate de uso unico e api so em 127.0.0.1"
```

---

### Task 3: Scripts de deploy e runbook

**Files:**
- Create: `deploy/bootstrap.sh`, `deploy/deploy.sh`, `deploy/README.md`, `.gitattributes`

**Interfaces:**
- Consumes: `docker-compose.prod.yml` (Task 2).
- Produces: `./deploy/deploy.sh <ip> [chave]` (padrão `~/.ssh/steamtracker_oci`); `bootstrap.sh` executado como `ssh ... 'bash -s' < deploy/bootstrap.sh`.

- [ ] **Step 1: `.gitattributes`** com `*.sh text eol=lf`; renormalizar (`git add --renormalize .`) se necessário.
- [ ] **Step 2: `deploy/bootstrap.sh`** — `set -euo pipefail`; idempotente; instala Docker Engine + `docker-compose-plugin` pelo repositório apt oficial da Docker para Ubuntu (chave em `/etc/apt/keyrings/docker.asc`, arquitetura via `dpkg --print-architecture`); `systemctl enable --now docker`; `usermod -aG docker ubuntu`; imprime `docker --version` e `docker compose version`.
- [ ] **Step 3: `deploy/deploy.sh`** — `set -euo pipefail`; seguir spec §4.6 passo a passo. Detalhes fixados:
  - aviso de árvore suja via `git status --porcelain` (não bloqueia);
  - `git archive --format=tar HEAD | ssh -i "$KEY" ubuntu@"$IP" 'rm -rf ~/steamtracker/src && mkdir -p ~/steamtracker/src && tar -x -C ~/steamtracker/src'`;
  - criação condicional do `.env` (`umask 077`, `openssl rand -hex 32`);
  - `cd ~/steamtracker/src && STEAMTRACKER_ENV_FILE="$HOME/steamtracker/.env" docker compose -f docker-compose.prod.yml --env-file "$HOME/steamtracker/.env" up -d --build`; o `up` sai ≠ 0 quando o `migrate` falha, então capturar o código (`|| up_rc=$?`) em vez de deixar o `set -e` encerrar antes dos logs;
  - mostra `docker compose ps -a` e `logs --tail 20 migrate`;
  - confere `docker inspect -f '{{.State.ExitCode}}'` do `migrate`; ≠ 0 (ou `up_rc` ≠ 0) → mensagem e `exit 1`.
- [ ] **Step 4: `deploy/README.md`** com as seções da spec §4.7 (comandos copiáveis).
- [ ] **Step 5: Verificar scripts**

Run: `bash -n deploy/bootstrap.sh && bash -n deploy/deploy.sh`; `shellcheck deploy/*.sh` se disponível (senão registrar que não foi rodado); `git ls-files --eol deploy/*.sh` → `i/lf w/lf` (Review Focus 1).
Expected: sem erros de sintaxe; LF no índice e na cópia de trabalho.

- [ ] **Step 6: `./deploy/deploy.sh` sem argumentos** → mensagem de uso e exit ≠ 0.

- [ ] **Step 7: Commit**

```bash
git add .gitattributes deploy/
git commit -m "build: scripts de bootstrap e deploy para a VM da OCI e runbook"
```

---

### Task 4: Chave SSH, VM e primeiro deploy (interativa — exige o dono)

**Files:** nenhum no repositório.

- [ ] **Step 1: Gerar a chave** — `mkdir -p ~/.ssh && ssh-keygen -t ed25519 -N "" -C steamtracker-oci -f ~/.ssh/steamtracker_oci`; mostrar só `~/.ssh/steamtracker_oci.pub`.
- [ ] **Step 2: Dono cria a VM** seguindo spec §6.2 e informa o IP público. (Parar e esperar.)
- [ ] **Step 3: Primeiro acesso** — `ssh -i ~/.ssh/steamtracker_oci -o StrictHostKeyChecking=accept-new ubuntu@<ip> 'uname -m; lsb_release -ds'` → `aarch64`, `Ubuntu 24.04...`.
- [ ] **Step 4: Bootstrap** — `ssh ... 'sudo bash -s' < deploy/bootstrap.sh`; depois `ssh ... 'docker ps'` (nova sessão, grupo aplicado) sem erro de permissão.
- [ ] **Step 5: Deploy** — `./deploy/deploy.sh <ip>` → `migrate` exit 0, demais `Up`. O primeiro build na VM pode levar vários minutos.

---

### Task 5: Verificação pós-deploy e registro

**Files:**
- Modify: `PROGRESS.md`

- [ ] **Step 1: Steam a partir da VM** — `ssh ... "curl -s --max-time 20 -w '\nHTTP %{http_code}\n' -A 'SteamTrackerV2/1.0' 'https://steamcommunity.com/market/priceoverview/?appid=730&currency=7&market_hash_name=Prisma%20Case'"` e o mesmo com `Item%20Que%20Nao%20Existe%20XYZ`. Registrar status e corpo.
- [ ] **Step 2: Estado** — `docker compose ps -a` na VM: `migrate` exit 0; `postgres`, `api`, `worker` `Up`.
- [ ] **Step 3: Pelo túnel** — `ssh -i ... -N -L 8080:127.0.0.1:8080 ubuntu@<ip>` em background; `POST /api/items` com corpo `{"appId":730,"marketHashName":"Prisma Case"}` enviado de arquivo UTF-8 → 201 (ou 503 se a Steam não respondeu no Step 1); se 201, `GET` do `Location` mostra 1 snapshot. Encerrar o túnel.
- [ ] **Step 4: Worker** — `docker compose logs worker` contém `Coleta concluída:`.
- [ ] **Step 5: Porta fechada de fora** — `curl -s --max-time 10 -o /dev/null -w "%{http_code}" http://<ip>:8080/` do PC → `000` (Review Focus 4).
- [ ] **Step 6: Redeploy preserva segredo e dados (Review Focus 2)** — anotar `sha256sum ~/steamtracker/.env` e `SELECT count(*) FROM "TrackedItems"`; rodar `./deploy/deploy.sh <ip>` de novo; os dois valores iguais.
- [ ] **Step 7: Reboot (Review Focus 5)** — `ssh ... 'sudo reboot'`; aguardar o SSH voltar; `docker compose ps -a` igual ao Step 2 sem intervenção.
- [ ] **Step 8: PROGRESS.md** — seção "Deploy na OCI": o que foi criado, shape/região informada pelo dono (sem IP), resultado de cada step acima, resposta sobre o status da Steam para item inexistente, pendências (backup, GitHub Actions). Atualizar "PRÓXIMO" e "Como retomar".
- [ ] **Step 9: Commit**

```bash
git add PROGRESS.md
git commit -m "docs: registra deploy na OCI e verificacao pos-deploy"
```
