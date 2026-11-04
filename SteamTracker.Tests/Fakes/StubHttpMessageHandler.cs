using System.Net;
using System.Text;

namespace SteamTracker.Tests.Fakes;

/// <summary>Handler que devolve sempre a mesma resposta e guarda a última URI requisitada.</summary>
public sealed class StubHttpMessageHandler(HttpStatusCode status, string? body) : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;

        var response = new HttpResponseMessage(status);
        if (body is not null)
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return Task.FromResult(response);
    }
}
