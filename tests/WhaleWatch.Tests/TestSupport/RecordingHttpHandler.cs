using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WhaleWatch.Tests.TestSupport;

public sealed class RecordingHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _respond;

    public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

    public RecordingHttpHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)>? respond = null)
    {
        _respond = respond ?? (_ => (HttpStatusCode.OK, "{\"result\":{}}"));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body));

        var (status, responseBody) = _respond(request);
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}
