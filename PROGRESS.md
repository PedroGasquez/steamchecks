# PROGRESS — Steam Price Tracker v2

Arquivo de estado do projeto para retomar o trabalho em qualquer sessão ou
ferramenta (Claude Code, outro chat, ou eu mesmo depois). Atualize ao fim de
cada dia de trabalho.

**Repo:** https://github.com/PedroGasquez/steamchecks
**Backlog completo:** ver `backlog-steam-tracker-v2.md` na raiz do repo.

---

## Stack confirmada

- .NET 9
- PostgreSQL 17 (via `Npgsql.EntityFrameworkCore.PostgreSQL`; local via `docker-compose.yml`)
- ASP.NET Core (API) + Worker Service
- EF Core · Polly · Serilog · Blazor · Docker · GitHub Actions
- Deploy alvo: Railway (a decidir na Semana 6)

---

## Arquitetura (Clean Architecture)

Quatro projetos na solution `SteamTracker`:

```
SteamTracker.Core           → domínio puro. Não depende de ninguém.
SteamTracker.Infrastructure → EF Core, Steam client, notificações. Refs: Core.
SteamTracker.Api            → REST API. Refs: Core + Infrastructure.
SteamTracker.Worker         → monitoramento background. Refs: Core + Infrastructure.
```

**Regra de dependência:** o Core define contratos (interfaces); a Infrastructure
implementa. O domínio nunca conhece detalhes de HTTP, banco, ou JSON.

---

## Decisões de design tomadas (e o porquê)

- **`Money` é `readonly record struct`**, não class: value object, comparado por
  valor, imutável, sempre válido (validação no construtor). Usa `decimal`, nunca
  `double` (dinheiro não admite erro de ponto flutuante).
- **Entidades com setters privados**: estado só muda por métodos com significado
  (`Deactivate()`, `MarkTriggered()`), não por atribuição solta. Construtor
  privado sem parâmetros para o EF Core; construtor público garante objeto válido.
- **`DateTime.UtcNow` sempre** — nunca horário local (evita bug de fuso).
- **Contrato `ISteamMarketClient` mora no Core**, implementação na Infrastructure
  (inversão de dependência).
- **`MarketPriceResult` retorna tipos do domínio** (`Money`), não o JSON cru da
  Steam. A tradução do JSON acontece dentro do client, escondida do Core.
- **Client retorna `null` quando item não existe** — "não encontrado" é resultado
  esperado, não exceção.
- **Parsing de preço isolado em método testável** — a Steam manda `"R$ 4,50"`
  (string formatada), e converter pra `decimal` confiável é a parte espinhosa.

### Limitação conhecida (anotar como issue)
O parser de preço cobre bem o formato brasileiro (BRL: milhar com ponto, decimal
com vírgula). Outras moedas formatam diferente (USD usa ponto decimal). Quando
suportar múltiplas moedas, `ParsePrice` precisa evoluir.

### Contexto da Steam API (validado por pesquisa)
Não há API oficial de mercado. O endpoint `/market/priceoverview/` é não-oficial,
sem documentação formal, com rate limits baixos — uso agressivo leva a 429 e
possível shadow-ban. **Isso justifica** Polly + rate limiter + circuit breaker da
Semana 2 (não são enfeite, são resposta a uma restrição real).
Parâmetros: `appid` (730 = CS2), `market_hash_name`, `currency` (1=USD, 7=BRL, 3=EUR).
Retorno: `lowest_price`, `median_price`, `volume` — todos string formatada.

---

## Progresso por dia

### ✅ Semana 1, Dia 1 — Setup
- Solution com 4 projetos + referências entre eles.
- README com narrativa v1 → v2.
- Repo conectado ao GitHub (branch `main`).

### ✅ Semana 1, Dia 2 — Domínio
- Enums: `ItemType`, `AlertType`, `NotificationChannel` (em `Core/Enums/`).
- Value object `Money` (em `Core/ValueObjects/`).
- Entidades `TrackedItem`, `PriceSnapshot`, `Alert` (em `Core/Entities/`).

### ✅ Semana 1, Dia 3 — Cliente Steam
- `ISteamMarketClient` + `MarketPriceResult` (em `Core/Abstractions/`).
- DTO `SteamPriceOverviewResponse` (em `Infrastructure/Steam/`, internal sealed).
- `SteamMarketClient` implementado (em `Infrastructure/Steam/`): HttpClient
  tipado, parsing de preço com `[GeneratedRegex]`, tradução pro domínio.
- Pacotes adicionados à Infrastructure: `Microsoft.Extensions.Http`,
  `Microsoft.Extensions.Options`.
- **Compila limpo. Commitado e enviado.**

### ✅ Semana 1, Dia 4 — Primeiro endpoint vivo
- `DependencyInjection.cs` na Infrastructure: `AddInfrastructure()` registra
  `SteamMarketClient` como HttpClient tipado, com `BaseAddress` e header
  `User-Agent` (sem ele a Steam retorna vazio/bloqueia).
- `Program.cs` da Api: removido o boilerplate `weatherforecast` do template;
  adicionado `GET /api/items/{appId}/{marketHashName}/price?currency=`,
  injeta `ISteamMarketClient`, retorna 404 se `null`, 200 com o preço se achou.
- **Testado ponta a ponta**: `GET /api/items/730/Prisma Case/price?currency=7`
  devolveu preço real da Steam em BRL (`{"lowestPrice":{"amount":11.42,...}}`).
- Observação: a Steam às vezes responde `{"success":true}` sem os campos de
  preço para itens/consultas recentes — parece throttling brando por item,
  não erro HTTP. Nosso parser já trata isso como "sem dado" (`Money?` nulo)
  em vez de quebrar, então não é bug — é o comportamento não-oficial da API
  que já estava documentado como risco conhecido.
- **Compila limpo. Ainda não commitado** (ver `git status`).

### ✅ Semana 1, Dia 5 — Resiliência (Polly)
- Motivação: durante o teste do Dia 4, a própria Steam devolveu
  `{"success":true}` sem os campos de preço no meio de testes normais —
  confirmando na prática o risco de rate limit já documentado.
- Pacote `Microsoft.Extensions.Http.Resilience` (v10.8.0, construído sobre
  Polly v8) adicionado à Infrastructure.
- `AddInfrastructure` encadeia `.AddResilienceHandler("steam-market", ...)`
  no `HttpClient` tipado com 3 estratégias:
  - **Retry**: até 3 tentativas, backoff exponencial com jitter, dispara em
    429 (Too Many Requests), 5xx, ou exceção de transporte.
  - **Circuit breaker**: `MinimumThroughput = 4` e `FailureRatio = 0.5` numa
    janela de 30s (limiar baixo de propósito — é um tracker pessoal de baixo
    volume, não um scraper; os defaults do Microsoft.Extensions.Http.Resilience
    exigem tráfego alto demais pra disparar nesse caso de uso).
  - **Timeout** por tentativa: 10s.
- Predicado de falha transitória (`IsTransientFailure`) é explícito, não usa
  os presets padrão do pacote — mantém a decisão de "o que é falha" visível
  e alinhada ao risco documentado (429/shadow-ban), não genérica.
- **Testado ponta a ponta de novo** (endpoint ainda funciona igual no
  caminho feliz). Não foi forçado um 429 real de propósito, pra não piorar
  o risco de shadow-ban do IP de teste.
- Compila limpo. Commitado.

### ✅ Semana 1, Dia 6 — Testes automatizados
- Novo projeto `SteamTracker.Tests` (xUnit), referenciando Core e
  Infrastructure, adicionado à solution.
- **Parser de preço/volume**: `ParsePrice`/`ParseVolume` (em
  `SteamMarketClient`) mudaram de `private` pra `internal static`, com
  `InternalsVisibleTo("SteamTracker.Tests")` no csproj da Infrastructure —
  evita reflection, mantém a superfície pública intacta.
  - Cobertura: formato brasileiro (milhar com ponto, decimal com vírgula),
    entrada nula/vazia/sem dígitos, volume com separador de milhar.
  - Um teste (`ParsePrice_FormatoAmericano_LimitacaoConhecida`) caracteriza
    de propósito o comportamento **errado** atual em moedas tipo USD (ponto
    como decimal) — documenta a limitação já anotada acima em vez de
    escondê-la, e vai falhar (sinalizando trabalho pendente) quando o parser
    ganhar suporte a múltiplas moedas.
- **Pipeline de resiliência**: `DependencyInjection.ConfigureSteamResilience`
  foi quebrado em `CreateRetryOptions()` e `CreateCircuitBreakerOptions()`
  (também `internal static`), permitindo montar pipelines de teste sem
  `HttpClient` real e sem esperar os delays de produção (retry usa 2s/4s/8s
  de backoff — nos testes, as opções são reconstruídas com delay de 1ms,
  reusando o predicado real `IsTransientFailure`).
  - Cobertura: classificação de status transiente (429/5xx → true, 2xx/4xx
    exceto 429 → false; exceção → sempre true); valores de configuração
    (MaxRetryAttempts, backoff, jitter, FailureRatio, MinimumThroughput);
    retry esgota tentativas em falha persistente e não retenta em sucesso;
    circuit breaker abre após ultrapassar o limiar e rejeita chamada
    seguinte com `BrokenCircuitException`.
- **28 testes, todos passando, suite roda em ~300ms** (sem timers reais nem
  chamada de rede — só a configuração de produção reaproveitada com delays
  reduzidos).
- Compila limpo (solução inteira, 5 projetos). Commitado.

### ✅ Semana 1, Dia 7 — Persistência (EF Core + PostgreSQL)
- Adiantada a Semana 3 do stack original. Pacotes: `Npgsql.EntityFrameworkCore.PostgreSQL`
  9.0.4 na Infrastructure (a versão mais nova, 10.x, só suporta net10 — presa
  em 9.0.4 pra bater com o TargetFramework net9.0 do resto da solução);
  `Microsoft.EntityFrameworkCore.Design` 9.0.1 na Api (só ela precisa, é o
  startup project usado pela ferramenta `dotnet ef`).
- **`dotnet-ef` instalado como ferramenta local** (`.config/dotnet-tools.json`,
  `dotnet tool restore` depois de clonar), não global — reprodutível em
  qualquer máquina sem depender de instalação prévia do dev.
- `SteamTrackerDbContext` (em `Infrastructure/Persistence/`) com
  `DbSet<TrackedItem>`, `DbSet<PriceSnapshot>`, `DbSet<Alert>`, configurações
  via `IEntityTypeConfiguration<T>` aplicadas por assembly scan.
- **Mapeamento de `Money` (value object, `readonly record struct`) foi a
  parte espinhosa do dia** — duas armadilhas reais do EF Core 9.x, não só
  escolha de API:
  - `ComplexProperty` (Complex Types, EF 8+) mapeia o `Money` **obrigatório**
    (`PriceSnapshot.LowestPrice`) em duas colunas reais (`Amount`, `Currency`)
    sem tabela própria — funciona bem pra campo obrigatório.
  - Pra `Money?` **opcional** (`PriceSnapshot.MedianPrice`,
    `Alert.TargetPrice`), nem Complex Type nem Owned Type serviram: Complex
    Type opcional não é suportado no EF Core 9.x pra propriedade de valor
    (erro explícito ao gerar migration, ver
    github.com/dotnet/efcore/issues/31376); Owned Type (`OwnsOne`) exige tipo
    referência, e `Money` é struct de propósito (decisão do Dia 2, não
    revisitada). Solução: `HasConversion` serializando o `Money?` inteiro
    como uma coluna `jsonb` — perde filtro nativo por `Amount` via SQL direto
    nessas duas colunas, aceitável porque nenhuma delas é hoje consultada
    fora de C# (`MedianPrice` é só exibido; `TargetPrice` é comparado pelo
    Worker em memória, não em query).
- Índice único em `TrackedItem(AppId, MarketHashName)` (mesmo item não pode
  ser rastreado duas vezes); índice em `PriceSnapshot(TrackedItemId,
  CapturedAt)` (consulta mais comum: histórico ordenado no tempo).
- `AddInfrastructure` passou a exigir `IConfiguration` (assinatura mudou —
  `Program.cs` da Api e do Worker atualizados) e lê `ConnectionStrings:SteamTracker`,
  lançando se não configurada. Connection string de desenvolvimento local
  (`Host=localhost;...;Password=postgres`) commitada em
  `appsettings.Development.json` da Api e do Worker — senha fraca de
  propósito, é só pra Postgres local/container, nunca produção.
- Migration inicial gerada: `dotnet ef migrations add InitialCreate --project
  SteamTracker.Infrastructure --startup-project SteamTracker.Api --output-dir
  Persistence/Migrations`.
- **Limitação conhecida**: não havia Docker nem Postgres local disponíveis
  nesta sessão pra rodar `dotnet ef database update` de verdade — a migration
  foi gerada e revisada manualmente (SQL faz sentido: tabelas, FKs com
  cascade, índices, tipos de coluna corretos), mas **nunca foi aplicada
  contra um banco real**. Primeira tarefa da próxima sessão com Postgres
  disponível: validar isso de ponta a ponta.
- Compila limpo (solução inteira, 5 projetos), suite de testes do Dia 6
  continua passando (28/28). Commitado.

### ✅ Semana 1, Dia 8 — Coleta de preços
- **O que entrou**: `POST /api/items` (valida o item na Steam e grava o primeiro
  `PriceSnapshot`), `GET /api/items/{id}/snapshots?limit=` (do mais recente ao
  mais antigo, `limit` ajustado a [1, 500]) e o GET de preço sob demanda movido
  para `Endpoints/ItemEndpoints.cs`. `PriceCollector` + `Worker` coletando
  preços periodicamente e sobrevivendo a falhas de ciclo. Repositórios EF
  (`TrackedItem`, `PriceSnapshot`) com interfaces no Core. Respostas sempre via
  DTO (`record`), nunca entidades.
- **Decisões**:
  - Moeda global em `SteamMarket:Currency` (padrão 7 = BRL) na Api e no Worker.
  - Validação no POST: 400 (dados inválidos), 409 (já rastreado), 422 (Steam
    não conhece o item), 503 (Steam indisponível). O GET de preço sob demanda
    também passou a devolver 503 nesses casos (antes devolvia 404 para 429/5xx,
    porque o cliente retornava null, e 500 não tratado para circuito aberto,
    timeout e erro de rede).
  - Cadência do Worker: ciclo a cada 15 min, 3 s entre itens (respeita o
    throttling da Steam).
  - `PriceCollector` fica no Worker; interfaces dos repositórios ficam no Core.
- **Bug encontrado e corrigido**: leitura de `Money?` em `jsonb` falhava (o
  `Money` é struct com construtor validado). Corrigido com `MoneyJsonConversion`;
  o formato gravado não mudou, então **sem migration nova**.
- `SteamMarketClient` agora lança `SteamMarketUnavailableException` em 429/5xx
  (antes tratava como "sem dado"), separando item inexistente de Steam fora do ar.
- **Ajustes da revisão final** (commit separado, entrou depois do merge do PR #2):
  corpo vazio/não-JSON numa resposta 2xx da Steam vira
  `SteamMarketUnavailableException` (antes estourava `JsonException`: 500 no POST
  e ciclo do Worker abortado); `Success` virou `bool?`, então um corpo de erro sem
  `success` em 429/5xx não é mais lido como "item inexistente"; o POST devolve 400
  para `displayName`/`marketHashName` acima de 255 caracteres e para `type`
  inexistente, e `displayName` vazio cai para o `marketHashName`; um erro real
  durante o shutdown do Worker não derruba mais o `ExecuteTask`.
- Testes: 79 passando (SQLite em memória + `WebApplicationFactory`, sem Postgres
  e sem rede). Compila limpo.
- **Pendências**: (1) a migration continua **nunca aplicada em Postgres real**
  (**resolvido no Dia 10**);
  (2) o status HTTP da Steam para item inexistente não foi confirmado. Se a
  Steam devolver 500 + `{"success":false}` para nomes desconhecidos, o retry e o
  circuit breaker (`IsTransientFailure` em
  `SteamTracker.Infrastructure/DependencyInjection.cs`; MinimumThroughput=4,
  FailureRatio=0.5, BreakDuration=30s) contam isso como falha: um único POST com
  nome errado faz 4 tentativas e abre o circuito por 30 s (o próximo POST correto
  só recebe 503 após ~14 s de backoff, pois o retry também trata
  `BrokenCircuitException` como transitória), e um item rastreado que passe a
  devolver 500+`success:false` pode abortar todos os ciclos do Worker para os
  itens seguintes. **Resolvido no Dia 9** (de forma defensiva, ver abaixo).

### ✅ Semana 1, Dia 9 — `success:false` não é falha transitória
- **Problema** (pendência 2 do Dia 8): se a Steam responder 500 +
  `{"success":false}` para item inexistente, o retry e o circuit breaker
  tratavam isso como falha — um nome digitado errado abria o circuito por 30 s
  e um item fora do mercado podia abortar os ciclos do Worker.
- **Tentativa de confirmar contra a API real (09/11)**: a Steam **não
  respondeu desta máquina** (timeout no `curl` e no PowerShell, dentro e fora
  do sandbox), enquanto o GitHub respondia normalmente. Pode ser instabilidade
  da Steam ou bloqueio do IP (o risco de shadow-ban já anotado). Não deu pra
  saber se o status de item inexistente é 200 ou 500.
- **Correção defensiva, que vale nos dois cenários**: o predicado da Polly
  (`IsTransientFailure` → `IsTransientFailureAsync` em `DependencyInjection.cs`)
  passa a ler o corpo de respostas 429/5xx; se for `{"success":false}`, a
  resposta é definitiva (item inexistente): sem retry e sem contar falha no
  circuit breaker. Qualquer outro 429/5xx continua transitório. Se a Steam
  devolver 200 nesse caso, nada muda. A checagem "corpo é `success:false`"
  virou `SteamPriceOverviewResponse.IsSuccessFalse`, usada também pelo client.
- Detalhe: o predicado roda antes do client ler a resposta.
  `ReadAsStringAsync` bufferiza o conteúdo, então o client ainda consegue ler
  o corpo — um teste com stream sem seek trava isso.
- Testes: 87 passando (+8: 429/500 com `success:false` não são transitórios;
  500 com HTML/JSON sem `success`/vazio continua transitório; corpo ainda
  legível depois do predicado; uma única tentativa no retry; 4 respostas
  `success:false` não abrem o circuito). Compila limpo.

### ✅ Semana 1, Dia 10 — Migration validada em Postgres real
- **`docker-compose.yml`** na raiz: Postgres 17 com as mesmas credenciais do
  `appsettings.Development.json` (senha fraca de propósito, só local), volume
  nomeado e healthcheck. Subir o banco do zero:
  `docker compose up -d` → `dotnet tool restore` →
  `dotnet ef database update --project SteamTracker.Infrastructure --startup-project SteamTracker.Api`.
- **Migration `InitialCreate` aplicada de verdade** (fecha a pendência aberta no
  Dia 7). Schema conferido no `psql`: `jsonb` em `MedianPrice`/`TargetPrice`,
  `numeric(18,2)` + `varchar(3)` no `Money` obrigatório, `timestamptz` nas
  datas, índice único `(AppId, MarketHashName)`, índice
  `(TrackedItemId, CapturedAt)`, FKs com cascade. O "Failed executing DbCommand"
  sobre `__EFMigrationsHistory` na primeira execução é esperado (o EF consulta
  a tabela antes de criá-la).
- **Gravação/leitura real** (programa descartável, sem Steam — ela segue sem
  responder desta máquina): `Money?` em `jsonb` volta com o valor certo
  (conteúdo cru `{"Amount": 12.00, "Currency": "BRL"}`), `null` volta `null`,
  moeda normalizada para maiúsculas, nome com `™` e `|` idêntico, datas com
  `Kind = Utc`, duplicata barrada pelo índice único (`23505`). Confirma no
  Postgres a correção do `MoneyJsonConversion` do Dia 8, antes só testada no
  SQLite.
- **API contra o Postgres**: `GET /api/items/{id}/snapshots` devolveu os
  snapshots em ordem decrescente com `capturedAt` em UTC (`...Z`); 404 para item
  inexistente; `POST /api/items` duplicado → 409 e inválido → 400. O POST de um
  item novo não foi testado (depende da Steam).
- Dica operacional: rodar a Api de dentro de `SteamTracker.Api/` (ou via
  `launchSettings`). `dotnet run --project ... --no-launch-profile` a partir da
  raiz usa a raiz como content root, não acha o `appsettings.Development.json`
  e falha com "Connection string 'SteamTracker' não configurada".
- Sem mudança de código de produção neste dia; suíte segue 87/87.

### ✅ Semana 1, Dia 11 — Empacotamento Docker e mini servidor no PC
- **Deploy preparado para a OCI** (spec `docs/superpowers/specs/2026-11-11-deploy-oci-design.md`,
  plano `docs/superpowers/plans/2026-11-11-deploy-oci.md`): `Dockerfile`
  multi-stage (alvos `api`, `worker`, `migrator` com EF migrations bundle; RID
  pelo `TARGETARCH`; usuário não-root), `docker-compose.prod.yml` (Postgres sem
  porta, `migrate` de uso único antes de Api/Worker, Api só em `127.0.0.1:8080`,
  logs rotacionados), `deploy/bootstrap.sh`, `deploy/deploy.sh` e runbook em
  `deploy/README.md`. Validado no Docker local, inclusive migration falhando
  (senha errada) barrando Api e Worker.
- **Rodando no PC como mini servidor** (decisão do dono: VM depois). `.env`
  com senha gerada em `~/steamtracker/.env`, fora do repo. `migrate` Exited (0),
  Api/Worker/Postgres de pé.
- **Steam voltou a responder desta máquina.** Ponta a ponta confirmado:
  `POST /api/items` com `Prisma Case` → 201, e o snapshot gravado no Postgres
  (`R$ 7,10` / mediana `R$ 7,06` / volume 6977).
- **Bug encontrado:** para item inexistente a Steam responde `200
  {"success":true}` **sem preços** (não `success:false`, como se supunha desde
  o Dia 8). O `SteamMarketClient` devolve um resultado com tudo `null` e o
  `POST /api/items` aceita o cadastro (201, sem snapshot).
- **Bug corrigido na origem** (`SteamMarketClient`): `success:true` sem nenhum
  dado de mercado (menor preço, mediana e volume todos ausentes) agora devolve
  `null`, que é o contrato já documentado ("item inexistente ou sem dados de
  mercado"). Efeito: cadastro → 422, `GET .../price` → 404, Worker → "pulado".
  Resposta parcial (só mediana/volume, item real sem oferta no momento) continua
  sendo aceita. +3 testes (90/90). Conferido contra a Steam real no mini
  servidor; item de teste removido do banco do PC.
- Pendente da OCI: criar a VM (Tasks 4–5 do plano: bootstrap, deploy,
  verificação na nuvem).

---

## ⏭️ PRÓXIMO: Semana 1, Dia 12

Candidatos: (1) alertas (`Alert`)
avaliados pelo Worker a cada coleta; (2) `GET /api/items` para listar os itens
rastreados; (3) deploy na VM da OCI quando o dono decidir. Dívidas menores da revisão: teste que monte o host real do Worker
(pegaria registro faltando no `Program.cs`) e desempate por `Id` na ordenação
dos snapshots com mesmo `CapturedAt`. O
arquivo `backlog-steam-tracker-v2.md` referenciado no topo deste documento
continua sem existir no repo — vale confirmar se existe em outro lugar ou
recriar o roadmap.

---

## Como retomar

Em qualquer ferramenta nova, diga:
> "Estamos no Dia 11 do backlog. Leia PROGRESS.md e backlog-steam-tracker-v2.md
> no repo e continue daí."
