# Steam Price Tracker v2

Rastreador de preços do Steam Community Market com monitoramento
contínuo, histórico de preços e alertas por Discord/email.

## Por que v2?

- **Sem monitoramento contínuo** — serverless não roda processos em
  background; o preço só atualizava quando o usuário abria a página
- **Sem histórico** — localStorage não escala nem permite análise
  temporal
- **Sem alertas** — impossível notificar queda de preço sem um worker

A v2 resolve isso com uma arquitetura adequada ao problema.

## Arquitetura

SteamTracker.Api            → REST API (ASP.NET Core)
SteamTracker.Worker         → monitoramento em background
SteamTracker.Core           → domínio (entidades, regras)
SteamTracker.Infrastructure → EF Core, Steam API client, notificações

## Stack

ASP.NET Core 8 · Worker Service · EF Core + SQL Server · Polly ·
Serilog · Blazor · Docker · GitHub Actions

## Status

🚧 Em desenvolvimento — veja as [issues](../../issues) para o roadmap.