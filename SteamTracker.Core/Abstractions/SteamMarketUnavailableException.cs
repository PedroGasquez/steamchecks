using System.Net;

namespace SteamTracker.Core.Abstractions;

/// <summary>
/// A Steam Market não conseguiu responder (rate limit ou erro de servidor).
/// Diferente de "item não existe", que é representado por resultado nulo.
/// </summary>
public sealed class SteamMarketUnavailableException(HttpStatusCode statusCode)
    : Exception($"Steam Market indisponível (HTTP {(int)statusCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
