# PROGRESS — Steam Price Tracker v2

Arquivo de estado do projeto para retomar o trabalho em qualquer sessão ou
ferramenta (Claude Code, outro chat, ou eu mesmo depois). Atualize ao fim de
cada dia de trabalho.

**Repo:** https://github.com/PedroGasquez/steamchecks
**Backlog completo:** ver `backlog-steam-tracker-v2.md` na raiz do repo.

---

## Stack confirmada

- .NET 9
- PostgreSQL (via `Npgsql.EntityFrameworkCore.PostgreSQL`, a entrar na Semana 3)
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

---

## ⏭️ PRÓXIMO: Semana 1, Dia 5

Definir o próximo passo com o backlog (`backlog-steam-tracker-v2.md`) — não
detalhado ainda nesta sessão. Candidatos plausíveis dado o histórico: testes
automatizados do parser de preço (`ParsePrice`/`ParseVolume`) e do endpoint,
ou avançar pra persistência (EF Core + PostgreSQL, adiantando a Semana 3).

---

## Como retomar

Em qualquer ferramenta nova, diga:
> "Estamos no Dia 4 do backlog. Leia PROGRESS.md e backlog-steam-tracker-v2.md
> no repo e continue daí."
