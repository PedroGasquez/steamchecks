# Coleta de preços: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cadastrar itens via `POST /api/items` (validados na Steam), ter o Worker gravando `PriceSnapshot` periodicamente e expor o histórico via `GET /api/items/{id}/snapshots`.

**Architecture:** Contratos de repositório e a exceção `SteamMarketUnavailableException` no Core; implementações EF e `SteamMarketOptions` na Infrastructure; `PriceCollector` (um ciclo) + `Worker` (laço de tempo) no projeto Worker; endpoints em `Api/Endpoints/`. Testes sem Postgres e sem rede: fakes à mão, SQLite em memória e `WebApplicationFactory`.

**Tech Stack:** .NET 9, ASP.NET Core minimal APIs, EF Core 9 (Npgsql em produção, SQLite nos testes), Polly v8 via `Microsoft.Extensions.Http.Resilience`, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-04-coleta-de-precos-design.md`

## Global Constraints

- `TargetFramework` continua `net9.0` em todos os projetos; pacotes EF ficam na linha **9.0.x** (Npgsql 9.0.4 não aceita EF 10).
- `SteamTracker.Core` não ganha nenhum `PackageReference`.
- Pacotes novos **somente** em `SteamTracker.Tests`: `Microsoft.EntityFrameworkCore.Sqlite` 9.0.x e `Microsoft.AspNetCore.Mvc.Testing` 9.0.x.
- Moeda: `SteamMarket:Currency`, padrão `7` (BRL). Cadência: `PriceCollection:Interval = 00:15:00`, `PriceCollection:DelayBetweenItems = 00:00:03`.
- `DateTime.UtcNow` sempre; entidades só mudam por métodos de domínio.
- Testes no estilo existente: namespace `SteamTracker.Tests.<Área>`, nomes `Metodo_Cenario_Resultado` em português.
- Comando de verificação geral: `dotnet test --nologo` na raiz. Baseline: 28 testes passando.
- Commits terminam com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Exceção num ciclo (ex.: banco fora do ar)** não pode derrubar o Worker; o próximo ciclo deve rodar. → teste na Task 4.
2. **Nome com espaço, `|`, `™` e parênteses** (`StatTrak™ AK-47 | Redline (Field-Tested)`) precisa ir url-encoded para a Steam e ser gravado exatamente igual. → testes nas Tasks 1 e 5.
3. **`marketHashName` só com espaços** (`"   "`) deve dar 400, não 500 por exceção do construtor da entidade. → caso no teste de 400 da Task 5.
4. **`limit` fora do intervalo** (`0`, `-5`, `1000`) deve ser ajustado para [1, 500], não gerar erro. → teste na Task 5.
5. **Validação no POST sem `LowestPrice`** (o "throttling brando" do Dia 4) deve criar o item com 201, mas sem snapshot. → teste na Task 5.

---

## File Structure

| Arquivo | Responsabilidade |
|---|---|
| `SteamTracker.Core/Abstractions/SteamMarketUnavailableException.cs` (novo) | Steam respondeu 429/5xx sem indicar item inexistente |
| `SteamTracker.Core/Abstractions/ITrackedItemRepository.cs` (novo) | contrato de acesso a `TrackedItem` |
| `SteamTracker.Core/Abstractions/IPriceSnapshotRepository.cs` (novo) | contrato de acesso a `PriceSnapshot` |
| `SteamTracker.Infrastructure/Steam/SteamMarketClient.cs` (modificar) | separar "não existe" de "indisponível" |
| `SteamTracker.Infrastructure/Steam/SteamMarketOptions.cs` (novo) | moeda global |
| `SteamTracker.Infrastructure/Persistence/MoneyJsonConversion.cs` (novo) | serializar `Money?` ↔ json corretamente (corrige bug do Dia 7) |
| `SteamTracker.Infrastructure/Persistence/Configurations/{PriceSnapshot,Alert}Configuration.cs` (modificar) | usar `MoneyJsonConversion` |
| `SteamTracker.Infrastructure/Persistence/Repositories/{TrackedItem,PriceSnapshot}Repository.cs` (novos) | implementações EF |
| `SteamTracker.Infrastructure/DependencyInjection.cs` (modificar) | registrar repositórios e `SteamMarketOptions` |
| `SteamTracker.Worker/PriceCollectionOptions.cs`, `PriceCollector.cs` (novos) | um ciclo de coleta |
| `SteamTracker.Worker/Worker.cs`, `Program.cs`, `appsettings.json` (modificar) | laço de tempo e configuração |
| `SteamTracker.Api/Endpoints/ItemEndpoints.cs`, `ItemContracts.cs` (novos) | endpoints e DTOs |
| `SteamTracker.Api/Program.cs`, `appsettings.json` (modificar) | mapear endpoints, `public partial class Program` |
| `SteamTracker.Tests/Fakes/*.cs` (novos) | fakes compartilhados |
| `SteamTracker.Tests/Support/SqliteTestDatabase.cs` (novo) | `DbContext` em SQLite em memória |
| `SteamTracker.Tests/{Steam,Persistence,Worker,Api}/*Tests.cs` (novos) | testes por área |

---

### Task 1: `SteamMarketClient` separa "item não existe" de "Steam indisponível"

**Files:**
- Create: `SteamTracker.Core/Abstractions/SteamMarketUnavailableException.cs`
- Modify: `SteamTracker.Infrastructure/Steam/SteamMarketClient.cs` (método `GetPriceAsync`)
- Test: `SteamTracker.Tests/Steam/SteamMarketClientResponseTests.cs`, `SteamTracker.Tests/Fakes/StubHttpMessageHandler.cs`

**Interfaces:**
- Produces: `public sealed class SteamMarketUnavailableException(HttpStatusCode statusCode) : Exception` com `public HttpStatusCode StatusCode { get; }` e mensagem `$"Steam Market indisponível (HTTP {(int)statusCode})."`.
- Produces: `StubHttpMessageHandler(HttpStatusCode status, string? body)`, que guarda `public Uri? LastRequestUri`.

- [ ] **Step 1: Escrever os testes que falham**

Montar o client com `new SteamMarketClient(new HttpClient(handler) { BaseAddress = new Uri("https://steamcommunity.com/") }, NullLogger<SteamMarketClient>.Instance)`.

```csharp
[Fact] GetPriceAsync_200ComSucesso_RetornaPreco
  // body: {"success":true,"lowest_price":"R$ 11,42","median_price":"R$ 12,00","volume":"1.234"}
  // Assert: result!.LowestPrice == new Money(11.42m, "BRL"); Volume == 1234

[Theory] [InlineData(HttpStatusCode.OK)] [InlineData(HttpStatusCode.InternalServerError)]
GetPriceAsync_CorpoSuccessFalse_RetornaNull(HttpStatusCode status)
  // body: {"success":false}  → Assert.Null(result)

[Theory] [InlineData(HttpStatusCode.TooManyRequests)] [InlineData(HttpStatusCode.ServiceUnavailable)]
GetPriceAsync_429ou5xxSemCorpo_LancaSteamMarketUnavailable(HttpStatusCode status)
  // body: null  → var ex = await Assert.ThrowsAsync<SteamMarketUnavailableException>(...); Assert.Equal(status, ex.StatusCode)

[Fact] GetPriceAsync_500ComHtml_LancaSteamMarketUnavailable
  // body: "<html>erro</html>" (não é JSON) → lança SteamMarketUnavailableException

[Fact] GetPriceAsync_404_RetornaNull

[Fact] GetPriceAsync_NomeComCaracteresEspeciais_EnviaUrlEncoded
  // nome: "StatTrak™ AK-47 | Redline (Field-Tested)"
  // Assert: handler.LastRequestUri!.Query contém Uri.EscapeDataString(nome)
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --nologo --filter "FullyQualifiedName~SteamMarketClientResponseTests"`
Expected: erro de compilação (`SteamMarketUnavailableException` não existe).

- [ ] **Step 3: Implementar**

Criar a exceção. Em `GetPriceAsync`, quando `!response.IsSuccessStatusCode`: ler o corpo como string e tentar desserializar para `SteamPriceOverviewResponse` (envolvendo em `try/catch (JsonException)`). Se vier `Success == false` → `return null`. Senão, se o status é 429 ou ≥ 500 → `throw new SteamMarketUnavailableException(response.StatusCode)`. Senão → log *warning* existente + `return null`. O caminho 2xx não muda.

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test --nologo`
Expected: todos passam (28 anteriores + 8 novos).

- [ ] **Step 5: Commit**

```bash
git add SteamTracker.Core/Abstractions/SteamMarketUnavailableException.cs SteamTracker.Infrastructure/Steam/SteamMarketClient.cs SteamTracker.Tests/Steam/SteamMarketClientResponseTests.cs SteamTracker.Tests/Fakes/StubHttpMessageHandler.cs
git commit -m "feat: SteamMarketClient distingue item inexistente de Steam indisponivel"
```

---

### Task 2: Repositórios EF + correção do `Money?` em json

**Files:**
- Create: `SteamTracker.Core/Abstractions/ITrackedItemRepository.cs`, `IPriceSnapshotRepository.cs`
- Create: `SteamTracker.Infrastructure/Persistence/MoneyJsonConversion.cs`
- Create: `SteamTracker.Infrastructure/Persistence/Repositories/TrackedItemRepository.cs`, `PriceSnapshotRepository.cs`
- Modify: `SteamTracker.Infrastructure/Persistence/Configurations/PriceSnapshotConfiguration.cs`, `AlertConfiguration.cs` (lambdas do `HasConversion`)
- Modify: `SteamTracker.Infrastructure/DependencyInjection.cs` (registrar repositórios como scoped)
- Modify: `SteamTracker.Tests/SteamTracker.Tests.csproj` (+ `Microsoft.EntityFrameworkCore.Sqlite` 9.0.x)
- Test: `SteamTracker.Tests/Support/SqliteTestDatabase.cs`, `SteamTracker.Tests/Persistence/RepositoryTests.cs`

**Interfaces:**
- Produces (Core), exatamente como na spec §3.1:
  - `ITrackedItemRepository`: `GetByIdAsync(Guid, CancellationToken = default) → Task<TrackedItem?>`, `ExistsAsync(int appId, string marketHashName, CancellationToken = default) → Task<bool>`, `GetActiveAsync(CancellationToken = default) → Task<IReadOnlyList<TrackedItem>>`, `AddAsync(TrackedItem, CancellationToken = default) → Task`.
  - `IPriceSnapshotRepository`: `AddAsync(PriceSnapshot, CancellationToken = default) → Task`, `GetByItemAsync(Guid trackedItemId, int limit, CancellationToken = default) → Task<IReadOnlyList<PriceSnapshot>>`.
- Produces (Tests): `sealed class SqliteTestDatabase : IDisposable` com `SqliteConnection Connection` (aberta em `"DataSource=:memory:"`) e `SteamTrackerDbContext CreateContext()` (já com `EnsureCreated` feito no construtor).
- Produces (Infrastructure): `internal static class MoneyJsonConversion` com `string? Serialize(Money? money)` e `Money? Deserialize(string? json)`.

**Contexto do bug:** `JsonSerializer.Deserialize<Money>` devolve `default(Money)` (Amount 0, Currency null), porque `Money` é struct com propriedades só de leitura e o STJ usa o construtor sem parâmetros. Confirmado com um projeto de teste na sessão de planejamento. `MoneyJsonConversion` serializa via um `private sealed record MoneyJson(decimal Amount, string Currency)` e reconstrói com `new Money(...)`. O formato gravado (`{"Amount":..,"Currency":".."}`) é o mesmo de antes, então **não há migration**.

- [ ] **Step 1: Adicionar o pacote SQLite ao projeto de testes**

Run: `dotnet add SteamTracker.Tests package Microsoft.EntityFrameworkCore.Sqlite --version 9.0.*`
Expected: pacote 9.0.x adicionado; `dotnet build` limpo.

- [ ] **Step 2: Escrever os testes que falham**

Cada teste usa um `SqliteTestDatabase` novo e um `CreateContext()` separado para escrever e para ler, para não ler o cache do change tracker.

```csharp
[Fact] GetActiveAsync_IgnoraItensInativos
  // 2 itens, um com Deactivate() → retorna só o ativo

[Fact] ExistsAsync_MesmoAppIdENome_RetornaTrue  // e false para outro nome/appId

[Fact] GetByIdAsync_IdInexistente_RetornaNull

[Fact] GetByItemAsync_OrdenaDoMaisRecenteERespeitaLimit
  // 3 snapshots com CapturedAt distintos (gravar em sequência com pequeno intervalo, ou setar via
  // ctx.Entry(s).Property(x => x.CapturedAt).CurrentValue antes de salvar); limit 2
  // Assert: 2 resultados, [0].CapturedAt > [1].CapturedAt, e são os dois mais recentes

[Fact] PriceSnapshot_IdaEVoltaNoBanco_PreservaMoneyObrigatorioEOpcional
  // new PriceSnapshot(id, new Money(11.42m,"BRL"), new Money(12m,"BRL"), 1234)
  // Assert lido: LowestPrice == new Money(11.42m,"BRL"); MedianPrice == new Money(12m,"BRL"); Volume == 1234

[Fact] PriceSnapshot_MedianPriceNulo_VoltaNulo

[Fact] AddAsync_ItemDuplicado_LancaDbUpdateException  // índice único (AppId, MarketHashName)
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet test --nologo --filter "FullyQualifiedName~RepositoryTests"`
Expected: erro de compilação (interfaces e repositórios não existem).

- [ ] **Step 4: Implementar interfaces, repositórios, `MoneyJsonConversion` e registro no DI**

Repositórios recebem `SteamTrackerDbContext` no construtor; leituras com `AsNoTracking()`; `GetByItemAsync` faz `Where(TrackedItemId).OrderByDescending(CapturedAt).Take(limit)`. As duas configurations passam a usar `HasConversion(m => MoneyJsonConversion.Serialize(m), j => MoneyJsonConversion.Deserialize(j))`. Em `AddInfrastructure`: `services.AddScoped<ITrackedItemRepository, TrackedItemRepository>()` e o equivalente para snapshots.

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test --nologo`
Expected: todos passam. Se `PriceSnapshot_IdaEVolta...` falhar em `MedianPrice`, a conversão não foi trocada nas duas configurations.

- [ ] **Step 6: Confirmar que o modelo EF não mudou**

Run: `dotnet tool restore && dotnet ef migrations has-pending-model-changes --project SteamTracker.Infrastructure --startup-project SteamTracker.Api`
Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 7: Commit**

```bash
git add SteamTracker.Core/Abstractions SteamTracker.Infrastructure SteamTracker.Tests
git commit -m "feat: repositorios EF para TrackedItem e PriceSnapshot; corrige leitura de Money? em jsonb"
```

---

### Task 3: `PriceCollector` (um ciclo de coleta)

**Files:**
- Create: `SteamTracker.Infrastructure/Steam/SteamMarketOptions.cs`
- Modify: `SteamTracker.Infrastructure/DependencyInjection.cs` (`services.Configure<SteamMarketOptions>(configuration.GetSection("SteamMarket"))`)
- Create: `SteamTracker.Worker/PriceCollectionOptions.cs`, `SteamTracker.Worker/PriceCollector.cs`
- Modify: `SteamTracker.Tests/SteamTracker.Tests.csproj` (+ `ProjectReference` para `SteamTracker.Worker`)
- Test: `SteamTracker.Tests/Fakes/FakeSteamMarketClient.cs`, `FakeTrackedItemRepository.cs`, `FakePriceSnapshotRepository.cs`, `SteamTracker.Tests/Worker/PriceCollectorTests.cs`

**Interfaces:**
- Consumes: repositórios da Task 2; `SteamMarketUnavailableException` da Task 1.
- Produces:
  - `public sealed class SteamMarketOptions { public int Currency { get; set; } = 7; }` (namespace `SteamTracker.Infrastructure.Steam`).
  - `public sealed class PriceCollectionOptions { public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15); public TimeSpan DelayBetweenItems { get; set; } = TimeSpan.FromSeconds(3); }`
  - `public sealed record CollectionResult(int Saved, int Skipped, int Failed);`
  - `public sealed class PriceCollector(ISteamMarketClient client, ITrackedItemRepository items, IPriceSnapshotRepository snapshots, IOptions<SteamMarketOptions> steamOptions, IOptions<PriceCollectionOptions> collectionOptions, ILogger<PriceCollector> logger)` com `Task<CollectionResult> CollectAsync(CancellationToken cancellationToken)`.
  - Fakes (reaproveitados nas Tasks 4 e 5):
    - `FakeSteamMarketClient : ISteamMarketClient`: `List<(int AppId, string MarketHashName, int Currency)> Calls`; `Dictionary<string, Func<MarketPriceResult?>> Responses` (chave = `marketHashName`; chave ausente → `null`; o `Func` pode lançar).
    - `FakeTrackedItemRepository : ITrackedItemRepository`: `List<TrackedItem> Items`; `Queue<Exception> GetActiveFailures` (se não vazia, `GetActiveAsync` lança a próxima); `int GetActiveCalls`.
    - `FakePriceSnapshotRepository : IPriceSnapshotRepository`: `List<PriceSnapshot> Saved`.

- [ ] **Step 1: Escrever os testes que falham**

`DelayBetweenItems = TimeSpan.Zero` e `Currency = 7` em todos os testes; um preço válido é `new MarketPriceResult(new Money(10m,"BRL"), null, 5)`.

```csharp
[Fact] CollectAsync_ItensComPreco_GravaUmSnapshotPorItem
  // 2 itens com preço → Saved.Count == 2; result == new CollectionResult(2, 0, 0)

[Fact] CollectAsync_SteamRetornaNull_PulaItem             // result == (0, 1, 0); nada gravado
[Fact] CollectAsync_ResultadoSemLowestPrice_PulaItem      // MarketPriceResult(null, null, null) → (0, 1, 0)

[Fact] CollectAsync_SteamIndisponivelNumItem_ContinuaNosProximos
  // item A lança SteamMarketUnavailableException(HttpStatusCode.TooManyRequests), item B tem preço
  // → result == (1, 0, 1); Calls.Count == 2

[Fact] CollectAsync_CircuitoAberto_AbortaRestoDoCiclo
  // 3 itens; o 2º lança new BrokenCircuitException()
  // → result == (1, 2, 0); Calls.Count == 2 (o 3º nunca é consultado)

[Fact] CollectAsync_UsaMoedaConfigurada
  // Currency = 3 → todas as Calls têm Currency == 3

[Fact] CollectAsync_TokenCancelado_PropagaCancelamento
  // DelayBetweenItems = 10 min, 2 itens; cancelar o token depois da 1ª chamada (dentro do Func do 1º item)
  // → Assert.ThrowsAnyAsync<OperationCanceledException>
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --nologo --filter "FullyQualifiedName~PriceCollectorTests"`
Expected: erro de compilação (`PriceCollector` não existe).

- [ ] **Step 3: Implementar `SteamMarketOptions`, `PriceCollectionOptions` e `PriceCollector`**

Seguir a tabela da spec §4.2 linha a linha. Ordem dos `catch`: `BrokenCircuitException` primeiro (aborta: o item atual e os restantes somam em Skipped), depois `SteamMarketUnavailableException`/`HttpRequestException`/`TimeoutRejectedException` (Failed). Não capturar `OperationCanceledException`. Delay com `Task.Delay(DelayBetweenItems, cancellationToken)` só entre itens, e pular quando o delay é `TimeSpan.Zero`. Resumo final no log: `"Coleta concluída: {Saved} salvos, {Skipped} pulados, {Failed} falhas"`.

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test --nologo`
Expected: todos passam.

- [ ] **Step 5: Commit**

```bash
git add SteamTracker.Infrastructure SteamTracker.Worker SteamTracker.Tests
git commit -m "feat: PriceCollector executa um ciclo de coleta gravando PriceSnapshot"
```

---

### Task 4: `Worker` roda ciclos periódicos sem morrer em falhas

**Files:**
- Modify: `SteamTracker.Worker/Worker.cs` (substitui o template)
- Modify: `SteamTracker.Worker/Program.cs` (`services.Configure<PriceCollectionOptions>(builder.Configuration.GetSection("PriceCollection"))`, `services.AddScoped<PriceCollector>()`)
- Modify: `SteamTracker.Worker/appsettings.json` (+ `"SteamMarket": { "Currency": 7 }`, `"PriceCollection": { "Interval": "00:15:00", "DelayBetweenItems": "00:00:03" }`)
- Test: `SteamTracker.Tests/Worker/WorkerTests.cs`

**Interfaces:**
- Consumes: `PriceCollector`, `PriceCollectionOptions` e os fakes da Task 3.
- Produces: `public class Worker(IServiceScopeFactory scopeFactory, IOptions<PriceCollectionOptions> options, ILogger<Worker> logger) : BackgroundService`.

Nos testes, `SteamTracker.Worker` é ao mesmo tempo namespace e classe: usar `using WorkerService = SteamTracker.Worker.Worker;`.

- [ ] **Step 1: Escrever os testes que falham**

Montar um `ServiceCollection` com: `AddLogging()`, o `FakeTrackedItemRepository` como singleton registrado como `ITrackedItemRepository`, `FakePriceSnapshotRepository` idem, `FakeSteamMarketClient` como `ISteamMarketClient`, `Options.Create(new SteamMarketOptions())`, `Options.Create(new PriceCollectionOptions { Interval = TimeSpan.FromMilliseconds(50), DelayBetweenItems = TimeSpan.Zero })` e `AddScoped<PriceCollector>()`.

```csharp
[Fact] ExecuteAsync_RodaPrimeiroCicloAoIniciar
  // StartAsync; aguardar (poll até 5 s) GetActiveCalls >= 1; StopAsync

[Fact] ExecuteAsync_ExcecaoNumCiclo_ProximoCicloRoda
  // GetActiveFailures.Enqueue(new InvalidOperationException("banco fora do ar"))
  // StartAsync; aguardar até 5 s por GetActiveCalls >= 2; StopAsync
  // Assert: GetActiveCalls >= 2 e worker.ExecuteTask!.IsFaulted == false
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --nologo --filter "FullyQualifiedName~WorkerTests"`
Expected: falha de compilação (construtor do `Worker` atual tem outra assinatura).

- [ ] **Step 3: Implementar o `Worker`**

`ExecuteAsync`: roda um ciclo, depois `using var timer = new PeriodicTimer(options.Value.Interval)` e `while (await timer.WaitForNextTickAsync(stoppingToken))` roda outro. Cada ciclo: `await using var scope = scopeFactory.CreateAsyncScope()`, resolve `PriceCollector` e chama `CollectAsync(stoppingToken)`. Envolver em `try/catch (Exception ex) when (!stoppingToken.IsCancellationRequested)` com log *error* `"Ciclo de coleta falhou; tentando novamente no próximo intervalo"`.

- [ ] **Step 4: Atualizar `Program.cs` e `appsettings.json` do Worker**

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test --nologo`
Expected: todos passam.

- [ ] **Step 6: Commit**

```bash
git add SteamTracker.Worker SteamTracker.Tests
git commit -m "feat: Worker executa coleta periodica e sobrevive a falhas de ciclo"
```

---

### Task 5: Endpoints `POST /api/items` e `GET /api/items/{id}/snapshots`

**Files:**
- Create: `SteamTracker.Api/Endpoints/ItemContracts.cs`, `SteamTracker.Api/Endpoints/ItemEndpoints.cs`
- Modify: `SteamTracker.Api/Program.cs` (remover o `MapGet` inline, chamar `app.MapItemEndpoints()`, adicionar `public partial class Program;` no fim)
- Modify: `SteamTracker.Api/appsettings.json` (+ `"SteamMarket": { "Currency": 7 }`)
- Modify: `SteamTracker.Tests/SteamTracker.Tests.csproj` (+ `Microsoft.AspNetCore.Mvc.Testing` 9.0.x, + `ProjectReference` para `SteamTracker.Api`)
- Modify: `PROGRESS.md` (registrar o Dia 8)
- Test: `SteamTracker.Tests/Support/ApiFactory.cs`, `SteamTracker.Tests/Api/ItemEndpointsTests.cs`

**Interfaces:**
- Consumes: repositórios (Task 2), `SteamMarketOptions` (Task 3), `SteamMarketUnavailableException` (Task 1), `FakeSteamMarketClient` (Task 3), `SqliteTestDatabase` (Task 2).
- Produces:
  - `public sealed record CreateTrackedItemRequest(int AppId, string? MarketHashName, string? DisplayName, ItemType? Type);`
  - `public sealed record TrackedItemResponse(Guid Id, int AppId, string MarketHashName, string DisplayName, ItemType Type, DateTime CreatedAt, bool IsActive);`
  - `public sealed record PriceSnapshotResponse(Money LowestPrice, Money? MedianPrice, int? Volume, DateTime CapturedAt);`
  - `public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)` com `MapGroup("/api/items")` contendo: o GET de preço existente (`/{appId:int}/{marketHashName}/price`, nome `GetItemPrice`, comportamento inalterado), `POST /` e `GET /{id:guid}/snapshots`.
  - `public sealed class ApiFactory : WebApplicationFactory<Program>`, que expõe `FakeSteamMarketClient Steam` e `SqliteTestDatabase Database`.

- [ ] **Step 1: Adicionar pacote e referência**

Run: `dotnet add SteamTracker.Tests package Microsoft.AspNetCore.Mvc.Testing --version 9.0.*` e `dotnet add SteamTracker.Tests reference SteamTracker.Api`
Expected: `dotnet build` limpo.

- [ ] **Step 2: Escrever `ApiFactory`**

Em `ConfigureWebHost` → `ConfigureTestServices`: `RemoveAll<DbContextOptions<SteamTrackerDbContext>>()`, `RemoveAll<IDbContextOptionsConfiguration<SteamTrackerDbContext>>()` (sem essa remoção, o EF 9 acusa dois providers), `AddDbContext<SteamTrackerDbContext>(o => o.UseSqlite(Database.Connection))`, `RemoveAll<ISteamMarketClient>()` e `AddSingleton<ISteamMarketClient>(Steam)`. Descartar o `Database` no `Dispose`.

- [ ] **Step 3: Escrever os testes que falham**

Ler o JSON de resposta com `JsonDocument`/`JsonElement` (desserializar direto para `Money` cai no mesmo problema da Task 2). Preço válido: `new MarketPriceResult(new Money(11.42m,"BRL"), new Money(12m,"BRL"), 100)`.

```csharp
[Theory] [InlineData(0, "Prisma Case")] [InlineData(730, "")] [InlineData(730, "   ")] [InlineData(730, null)]
Post_DadosInvalidos_Retorna400(int appId, string? name)

[Fact] Post_ItemJaRastreado_Retorna409          // segundo POST igual
[Fact] Post_SteamRetornaNull_Retorna422_ENaoSalva
[Theory] [MemberData: SteamMarketUnavailableException(503), BrokenCircuitException, TimeoutRejectedException, HttpRequestException]
Post_SteamIndisponivel_Retorna503_ENaoSalva(Exception ex)

[Fact] Post_Sucesso_Retorna201_ComLocationEPrimeiroSnapshot
  // Location == $"/api/items/{id}/snapshots"; displayName == marketHashName; type == 0 (Unknown);
  // GET do Location devolve 1 snapshot com lowestPrice.amount == 11.42

[Fact] Post_ValidacaoSemLowestPrice_Retorna201_SemSnapshot   // MarketPriceResult(null, null, null)

[Fact] Post_NomeComCaracteresEspeciais_GravaExatamente
  // "StatTrak™ AK-47 | Redline (Field-Tested)" → resposta e Steam.Calls[0].MarketHashName iguais ao enviado

[Fact] GetSnapshots_ItemInexistente_Retorna404
[Fact] GetSnapshots_OrdenaDoMaisRecente_ERespeitaLimit       // 3 snapshots via repositório, ?limit=2

[Theory] [InlineData(0, 1)] [InlineData(-5, 1)] [InlineData(1000, 3)]
GetSnapshots_LimitForaDoIntervalo_AjustaSemErro(int limit, int esperado)
  // 3 snapshots gravados; 1000 → ajustado para 500 → devolve os 3
```

- [ ] **Step 4: Rodar e ver falhar**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ItemEndpointsTests"`
Expected: falha de compilação (`MapItemEndpoints`/contratos não existem).

- [ ] **Step 5: Implementar contratos, endpoints e `Program.cs`**

Seguir as tabelas da spec §4.4 e §4.5. Validar `appId > 0` e `!string.IsNullOrWhiteSpace(MarketHashName)` **antes** de construir a entidade, retornando `Results.ValidationProblem` com as chaves `"appId"` e `"marketHashName"`. Moeda vem de `IOptions<SteamMarketOptions>`. Respostas de erro usam `Results.Problem(statusCode: ..., title: ...)` com os títulos da spec. Sucesso: `Results.Created($"/api/items/{item.Id}/snapshots", TrackedItemResponse)`. `limit` com `Math.Clamp(limit ?? 100, 1, 500)`.

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test --nologo`
Expected: todos passam (28 do baseline + novos das Tasks 1 a 5), sem testes ignorados.

- [ ] **Step 7: Build da solução inteira**

Run: `dotnet build --nologo`
Expected: `0 Erro(s)` e nenhum aviso novo nos arquivos criados ou alterados neste plano.

- [ ] **Step 8: Atualizar `PROGRESS.md`**

Adicionar a seção "✅ Semana 1, Dia 8 — Coleta de preços" no estilo das anteriores: o que entrou (endpoints, Worker, repositórios), as decisões (moeda global, validação no POST, cadência, `PriceCollector` no Worker), o **bug do `Money?` em jsonb corrigido** e as pendências (migration nunca aplicada em Postgres real; status da Steam para item inexistente não confirmado). Atualizar a seção "⏭️ PRÓXIMO" (candidatos: validar no Postgres real, alertas, `GET /api/items`).

- [ ] **Step 9: Commit**

```bash
git add SteamTracker.Api SteamTracker.Tests PROGRESS.md
git commit -m "feat: endpoints POST /api/items e GET snapshots; atualiza PROGRESS (Dia 8)"
```
