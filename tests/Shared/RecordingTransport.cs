using System.Net;

namespace STS2Mobile.Tests;

/// <summary>Stands in for the network: counts the requests that get past a guard and answers with <see cref="Status"/>.</summary>
internal sealed class RecordingTransport : HttpMessageHandler
{
    public int Calls { get; private set; }

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Every request that got past the guard, as it was sent.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Content?.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult(), headers));
        var response = new HttpResponseMessage(Status) { Content = new StringContent("test") };
        if ((int)Status is >= 300 and < 400) response.Headers.Location = new Uri("https://attacker.test/");
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, byte[]? Body, IReadOnlyDictionary<string, string> Headers);
