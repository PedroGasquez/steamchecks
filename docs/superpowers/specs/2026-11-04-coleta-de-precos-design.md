# Coleta de preços: cadastro de itens + Worker gravando histórico

**Data:** 2026-11-04 · **Dia do backlog:** Semana 1, Dia 8 · **Status:** design aprovado, aguardando revisão da spec

## 1. Objetivo

Sair do "preço sob demanda" e passar a ter **histórico de preços de verdade**:

- o usuário cadastra itens a rastrear via `POST /api/items`;
- o Worker, periodicamente, consulta a Steam para cada item ativo e grava um
  `PriceSnapshot` no banco;
- o histórico pode ser consultado via `GET /api/items/{id}/snapshots`.

### Critérios de sucesso

1. `POST /api/items` cria um `TrackedItem` somente se o item existir no Steam
   Market, e grava o preço obtido na validação como primeiro snapshot.
2. A cada ciclo, o Worker grava um snapshot por item ativo com preço disponível,
   sem derrubar o processo quando a Steam ou o banco falham.
3. `GET /api/items/{id}/snapshots` devolve o histórico do mais recente para o
   mais antigo.
4. Tudo coberto por testes automatizados que rodam sem Postgres e sem rede.

### Fora de escopo

- Avaliação e disparo de alertas (`Alert` já existe; fica para um dia próprio).
- `GET /api/items` (listagem) e desativação de item.
- Moeda por item.
- Aplicar a migration contra um Postgres real (pendência mantida no PROGRESS.md).

## 2. Decisões tomadas

| Decisão | Escolha | Motivo |
|---|---|---|
| Moeda | Global, em config (`SteamMarket:Currency`, padrão `7` = BRL) | Não mexe no domínio nem gera migration; evita a limitação conhecida do `ParsePrice` com USD. |
| Validação no POST | Consulta a Steam uma vez antes de salvar | Um nome digitado errado não fica em loop no Worker para sempre. |
| Cadência do Worker | Ciclo a cada 15 min, 3 s entre itens (ambos em config) | Conservador pelo risco de 429/shadow-ban; ~96 pontos/item/dia. |
| Acesso a dados | Repositórios específicos no Core, implementados com EF na Infrastructure | Segue a regra "Core define contratos, Infrastructure implementa"; lógica testável com fakes. |
| Onde fica a lógica do ciclo | `PriceCollector` no projeto Worker | Precisa de `ILogger`/`IOptions`; colocá-lo no Core forçaria o domínio a depender de `Microsoft.Extensions.*`. |
| Testes de persistência | SQLite em memória | Sem Postgres disponível; exercita mapeamento real (complex type, conversão json, índice único). |

## 3. Componentes

### 3.1 Core (sem dependências externas)

**`Core/Abstractions/ITrackedItemRepository.cs`**

```csharp
public interface ITrackedItemRepository
{
    Task<TrackedItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(int appId, string marketHashName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackedItem>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task AddAsync(TrackedItem item, CancellationToken cancellationToken = default); // persiste imediatamente
}
```

**`Core/Abstractions/IPriceSnapshotRepository.cs`**

```csharp
public interface IPriceSnapshotRepository
{
    Task AddAsync(PriceSnapshot snapshot, CancellationToken cancellationToken = default); // persiste imediatamente
    // Ordenado por CapturedAt decrescente, no máximo `limit` itens.
    Task<IReadOnlyList<PriceSnapshot>> GetByItemAsync(Guid trackedItemId, int limit, CancellationToken cancellationToken = default);
}
```

Cada `AddAsync` chama `SaveChangesAsync`. Não há Unit of Work: nenhum fluxo
deste ciclo precisa de transação entre agregados.

**`Core/Abstractions/SteamMarketUnavailableException.cs`**: exceção lançada pelo
client quando a Steam responde 429 ou 5xx (já depois do retry) sem indicar
"item inexistente". Carrega o `HttpStatusCode`.

### 3.2 Infrastructure

- `Persistence/Repositories/TrackedItemRepository.cs` e
  `PriceSnapshotRepository.cs`: implementações EF, registradas como *scoped*
  em `AddInfrastructure`.
- `Steam/SteamMarketOptions.cs`: `int Currency = 7`, ligado à seção
  `"SteamMarket"`, registrado em `AddInfrastructure`. Fica aqui porque Api
  (validação do POST) e Worker (coleta) usam a mesma moeda.
- `Steam/SteamMarketClient.cs`: muda o tratamento de respostas (seção 4.1).

### 3.3 Worker

- `PriceCollectionOptions.cs`: `TimeSpan Interval = 15 min`,
  `TimeSpan DelayBetweenItems = 3 s`, seção `"PriceCollection"`.
- `PriceCollector.cs`: executa **um** ciclo de coleta (seção 4.2). Registrado
  como *scoped*.
- `Worker.cs`: laço de tempo (seção 4.3).

### 3.4 Api

- `Endpoints/ItemEndpoints.cs`: `MapItemEndpoints()` com
  `MapGroup("/api/items")`, contendo o GET de preço sob demanda já existente
  (movido do `Program.cs`, comportamento inalterado) e os dois endpoints novos.
- `Endpoints/ItemContracts.cs`: DTOs de request/response (`record`).
- `Program.cs`: passa a chamar `app.MapItemEndpoints()` e ganha
  `public partial class Program;` para o `WebApplicationFactory`.

## 4. Comportamento

### 4.1 `SteamMarketClient`: separar "não existe" de "indisponível"

Resposta final da Steam (depois do pipeline de retry):

| Resposta | Resultado |
|---|---|
| 2xx com `success:true` e ao menos um campo de mercado | `MarketPriceResult` |
| 2xx com `success:true` sem nenhum campo de mercado (os três ausentes/vazios) | `null` (item inexistente ou sem dados de mercado; comportamento real da Steam confirmado em 2026-11-12) |
| Corpo JSON `{"success":false}`, qualquer status | `null` (item não existe / sem mercado) |
| 429 ou 5xx sem esse corpo | lança `SteamMarketUnavailableException` |
| Outros status não-2xx (400, 404…) | `null` |

A regra do `success:false` vale com qualquer status porque não foi possível
confirmar se a Steam responde 200 ou 500 para item inexistente (sem rede na
sessão de design). **Pendência:** se for 500, o retry gasta 3 tentativas a cada
nome inválido. Confirmar com uma chamada real e, se for o caso, ajustar
`IsTransientFailure` num ciclo futuro.

Exceções vindas do pipeline (`BrokenCircuitException`,
`TimeoutRejectedException`, `HttpRequestException`) continuam propagando sem
tradução.

### 4.2 `PriceCollector.CollectAsync`

```csharp
public sealed record CollectionResult(int Saved, int Skipped, int Failed);
Task<CollectionResult> CollectAsync(CancellationToken cancellationToken);
```

1. Carrega os itens ativos (`GetActiveAsync`).
2. Para cada item, sequencialmente, aguardando `DelayBetweenItems` **entre**
   itens (não após o último), chama
   `GetPriceAsync(item.AppId, item.MarketHashName, options.Currency)`:

| Resultado | Ação | Conta como |
|---|---|---|
| `null` | log *warning* | Skipped |
| resultado sem `LowestPrice` | log *information* ("sem preço neste ciclo") | Skipped |
| resultado com `LowestPrice` | grava `PriceSnapshot` | Saved |
| `SteamMarketUnavailableException`, `HttpRequestException`, `TimeoutRejectedException` | log *error*, segue para o próximo | Failed |
| `BrokenCircuitException` | log *warning*, **aborta o restante do ciclo** | o item atual e os restantes: Skipped |
| `OperationCanceledException` com o token cancelado | propaga | — |

3. Loga um resumo: `Coleta concluída: {Saved} salvos, {Skipped} pulados, {Failed} falhas`.

Circuito aberto significa que a Steam está bloqueando; continuar só aumenta o
risco de shadow-ban.

### 4.3 `Worker`

- Executa o primeiro ciclo imediatamente ao iniciar; depois, a cada
  `Interval`, via `PeriodicTimer`.
- Cria um `IServiceScope` por ciclo e resolve o `PriceCollector` dele
  (`DbContext` é scoped; o Worker é singleton).
- Envolve cada ciclo em `try/catch` (exceto cancelamento do shutdown): desde o
  .NET 8, uma exceção não tratada em `BackgroundService` derruba o host. Um
  erro vira log *error* e o próximo ciclo tenta de novo.

### 4.4 `POST /api/items`

Request: `{ "appId": 730, "marketHashName": "Prisma Case", "displayName": null, "type": null }`.
`displayName` padrão = `marketHashName`; `type` padrão = `ItemType.Unknown`.

| Situação | Resposta |
|---|---|
| `appId <= 0` ou `marketHashName` vazio | **400** `ValidationProblem` |
| item já rastreado (`ExistsAsync`) | **409** `Problem` |
| Steam devolve `null` | **422** `Problem` "item não encontrado no Steam Market" |
| `SteamMarketUnavailableException`, `BrokenCircuitException`, `TimeoutRejectedException`, `HttpRequestException` | **503** `Problem` "Steam indisponível, tente novamente mais tarde" |
| sucesso | **201** com `TrackedItemResponse`; `Location: /api/items/{id}/snapshots` |

No sucesso, se o resultado da validação tiver `LowestPrice`, ele é gravado como
primeiro `PriceSnapshot`. Sem chamada extra à Steam.

Corrida entre `ExistsAsync` e o insert é barrada pelo índice único; no pior
caso, 500 em vez de 409. Aceito para um tracker pessoal.

`TrackedItemResponse`: `{ id, appId, marketHashName, displayName, type, createdAt, isActive }`.

### 4.5 `GET /api/items/{id}/snapshots?limit=100`

- `limit` padrão 100, ajustado ao intervalo [1, 500].
- Item inexistente → **404**.
- Sucesso → **200** com `PriceSnapshotResponse[]`
  (`{ lowestPrice, medianPrice, volume, capturedAt }`), do mais recente para o
  mais antigo. `lowestPrice`/`medianPrice` serializam `Money` (`{ amount, currency }`).

Respostas sempre via DTO: entidades têm navegações (ciclo no JSON) e não devem
vazar.

## 5. Configuração

`appsettings.json` da Api e do Worker ganham:

```json
"SteamMarket": { "Currency": 7 }
```

`appsettings.json` do Worker ganha também:

```json
"PriceCollection": { "Interval": "00:15:00", "DelayBetweenItems": "00:00:03" }
```

## 6. Testes

Todos no projeto `SteamTracker.Tests`, sem Postgres e sem rede.

| Alvo | Técnica | Casos |
|---|---|---|
| `PriceCollector` | fakes escritos à mão para repositórios e `ISteamMarketClient`; `DelayBetweenItems = 0` | grava um snapshot por item com preço; pula `null`; pula sem `LowestPrice`; segue após `SteamMarketUnavailableException`; aborta com `BrokenCircuitException`; usa a moeda configurada; contagens do `CollectionResult` |
| `SteamMarketClient` | `HttpMessageHandler` fake, sem pipeline de resiliência | 200 sucesso → preço; 200 `success:false` → `null`; 500 `success:false` → `null`; 429 → exceção; 503 sem corpo → exceção; 404 → `null` |
| Repositórios | SQLite em memória (`EnsureCreated`) | `GetActiveAsync` ignora inativos; `ExistsAsync`; `GetByItemAsync` ordena decrescente e respeita `limit`; ida e volta de `Money` (complex type + json); índice único rejeita duplicata |
| Endpoints | `WebApplicationFactory<Program>` + SQLite em memória + `ISteamMarketClient` fake | POST 400 / 409 / 422 / 503 / 201 (com primeiro snapshot gravado); GET snapshots 404 / 200 ordenado com `limit` |

Pacotes novos, **somente** no projeto de testes:
`Microsoft.EntityFrameworkCore.Sqlite` 9.0.x e
`Microsoft.AspNetCore.Mvc.Testing` 9.0.x. O projeto de testes passa a
referenciar `SteamTracker.Api` e `SteamTracker.Worker`.

Pegadinha conhecida do EF Core 9: ao substituir o Npgsql pelo SQLite no
`WebApplicationFactory`, remover também o
`IDbContextOptionsConfiguration<SteamTrackerDbContext>`, além do
`DbContextOptions<SteamTrackerDbContext>`. Sem isso, o EF acusa dois providers
registrados.

## 7. Riscos e pendências

- **Migration nunca aplicada em Postgres real**: os testes usam SQLite; algo
  específico do Postgres (ex.: `jsonb`) só aparece ao rodar
  `dotnet ef database update`. Continua como primeira tarefa quando houver
  Postgres disponível.
- **Status da Steam para item inexistente** não confirmado (seção 4.1).
- **Sem migration nova neste ciclo**: nenhuma entidade muda.
