# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY .config/dotnet-tools.json .config/
COPY SteamTracker.Core/SteamTracker.Core.csproj SteamTracker.Core/
COPY SteamTracker.Infrastructure/SteamTracker.Infrastructure.csproj SteamTracker.Infrastructure/
COPY SteamTracker.Api/SteamTracker.Api.csproj SteamTracker.Api/
COPY SteamTracker.Worker/SteamTracker.Worker.csproj SteamTracker.Worker/
RUN dotnet restore SteamTracker.Api/SteamTracker.Api.csproj \
 && dotnet restore SteamTracker.Worker/SteamTracker.Worker.csproj \
 && dotnet tool restore

COPY . .
RUN dotnet publish SteamTracker.Api -c Release --no-restore -o /out/api \
 && dotnet publish SteamTracker.Worker -c Release --no-restore -o /out/worker

RUN RID=x64; if [ "$TARGETARCH" = "arm64" ]; then RID=arm64; fi; \
    dotnet ef migrations bundle \
      --project SteamTracker.Infrastructure \
      --startup-project SteamTracker.Api \
      --self-contained -r linux-$RID \
      -o /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "SteamTracker.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
USER app
ENTRYPOINT ["dotnet", "SteamTracker.Worker.dll"]

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS migrator
WORKDIR /app
COPY --from=build /out/efbundle .
USER app
ENTRYPOINT ./efbundle --connection "$ConnectionStrings__SteamTracker"
