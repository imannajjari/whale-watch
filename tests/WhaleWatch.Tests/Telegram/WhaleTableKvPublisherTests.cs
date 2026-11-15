using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Models.Telegram;
using WhaleWatch.Services.SmartMoney;
using WhaleWatch.Services.Telegram;
using Xunit;

namespace WhaleWatch.Tests.Telegram;

public sealed class WhaleTableKvPublisherTests
{
    private static CloudflareKvSettings Settings() => new()
    {
        ApiToken = "cf-secret-token",
        AccountId = "acct123",
        NamespaceId = "ns456"
    };

    [Fact]
    public void IsConfigured_RequiresTokenAccountAndNamespace()
    {
        Assert.True(Settings().IsConfigured);
        Assert.False(new CloudflareKvSettings().IsConfigured);
        Assert.False(new CloudflareKvSettings { ApiToken = "t", AccountId = "a" }.IsConfigured);
        Assert.False(new CloudflareKvSettings { ApiToken = "t", NamespaceId = "n" }.IsConfigured);
        Assert.False(new CloudflareKvSettings { AccountId = "a", NamespaceId = "n" }.IsConfigured);
    }

    [Fact]
    public async Task PublishAsync_PutsTheTextToTheKvValueEndpointWithTheBearerToken()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var publisher = new WhaleTableKvPublisher(http, Settings());

        bool ok = await publisher.PublishAsync("<b>table</b> \u0648\u0636\u0639");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acct123/storage/kv/namespaces/ns456/values/whale-table", handler.Url);
        Assert.Equal("Bearer", handler.AuthScheme);
        Assert.Equal("cf-secret-token", handler.AuthParameter);
        Assert.Equal("application/octet-stream", handler.ContentType);
        Assert.Equal("<b>table</b> \u0648\u0636\u0639", handler.Body);
    }

    [Fact]
    public async Task PublishAsync_WhenCloudflareRefuses_ReturnsFalseWithoutThrowingOrLeakingTheToken()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, "{\"errors\":[{\"message\":\"forbidden\"}]}");
        using var http = new HttpClient(handler);
        using var captured = new StringWriter();
        var publisher = new WhaleTableKvPublisher(http, Settings(), captured);

        Assert.False(await publisher.PublishAsync("text"));

        Assert.Contains("HTTP 403", captured.ToString());
        Assert.DoesNotContain("cf-secret-token", captured.ToString());
    }

    [Fact]
    public async Task PublishAsync_WhenTheNetworkFails_ReturnsFalse()
    {
        using var http = new HttpClient(new ThrowingHandler());
        using var captured = new StringWriter();
        var publisher = new WhaleTableKvPublisher(http, Settings(), captured);

        Assert.False(await publisher.PublishAsync("text"));
        Assert.Contains("simulated network failure", captured.ToString());
    }

    [Fact]
    public async Task PublishAsync_WhenTheHandlerThrowsSomethingUnexpected_StillReturnsFalse()
    {
        using var http = new HttpClient(new ThrowingHandler(new InvalidOperationException("unexpected")));
        using var captured = new StringWriter();

        Assert.False(await new WhaleTableKvPublisher(http, Settings(), captured).PublishAsync("text"));
        Assert.Contains("unexpected", captured.ToString());
    }

    [Fact]
    public async Task PublishCurrentTableAsync_WhenTheDatabaseCannotBeRead_WarnsAndDoesNotThrow()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        using var captured = new StringWriter();
        string badPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}", "x.db");
        using var repo = new SqliteSmartMoneyRepository($"Data Source={badPath}");

        bool ok = await new WhaleTableKvPublisher(http, Settings(), captured).PublishCurrentTableAsync(repo);

        Assert.False(ok);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("Could not build the whale table", captured.ToString());
    }

    [Fact]
    public async Task PublishCurrentTableAsync_PublishesTheRenderedTable()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        string dbPath = Path.Combine(Path.GetTempPath(), $"kv_publish_{Guid.NewGuid():N}.db");
        try
        {
            using var repo = new SqliteSmartMoneyRepository($"Data Source={dbPath}");
            await repo.InitializeAsync();
            await repo.SaveSnapshotPositionsAsync("0xw1", [new WalletPositionSnapshot("0xw1", "BTC", "Short", 2m, 70000m, 140000m, 5000m, 10m)]);

            bool ok = await new WhaleTableKvPublisher(http, Settings()).PublishCurrentTableAsync(repo);

            Assert.True(ok);
            Assert.Contains("BTC Short", handler.Body);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task PublishAsync_WhenNotConfiguredOrEmpty_SendsNothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        Assert.False(await new WhaleTableKvPublisher(http, new CloudflareKvSettings()).PublishAsync("text"));
        Assert.False(await new WhaleTableKvPublisher(http, Settings()).PublishAsync("  "));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody = "{}") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? AuthScheme { get; private set; }
        public string? AuthParameter { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Url = request.RequestUri?.ToString();
            AuthScheme = request.Headers.Authorization?.Scheme;
            AuthParameter = request.Headers.Authorization?.Parameter;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private sealed class ThrowingHandler(Exception? error = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw (error ?? new HttpRequestException("simulated network failure"));
    }
}
