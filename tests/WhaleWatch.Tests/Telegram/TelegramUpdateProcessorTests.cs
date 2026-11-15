using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.Telegram;
using WhaleWatch.Models.Telegram;
using WhaleWatch.Services.SmartMoney;
using WhaleWatch.Services.Telegram;
using Xunit;

namespace WhaleWatch.Tests.Telegram;

public sealed class TelegramUpdateProcessorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tg_updates_{Guid.NewGuid():N}.db");

    private static TelegramSettings Settings() => new() { Enabled = true, BotToken = "123:tok", ChatId = "42" };

    private async Task<SqliteSmartMoneyRepository> RepoAsync()
    {
        var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        await repo.InitializeAsync();
        return repo;
    }

    [Fact]
    public async Task WhenAWebhookIsSet_GetUpdatesIsNotCalledAndNothingIsSent()
    {
        var http = new RoutingHandler(url => url.Contains("getWebhookInfo")
            ? Json("{\"ok\":true,\"result\":{\"url\":\"https://whale-bot.example.workers.dev/webhook\"}}")
            : Json("{\"ok\":true,\"result\":[]}"));
        using var client = new HttpClient(http);
        var notifier = new RecordingNotifier();
        using var repo = await RepoAsync();
        using var log = new StringWriter();

        await new TelegramUpdateProcessor(client, Settings(), notifier, repo, log).ProcessPendingUpdatesAsync();

        Assert.DoesNotContain(http.Urls, u => u.Contains("getUpdates"));
        Assert.Empty(notifier.Messages);
        Assert.Contains("webhook", log.ToString());
    }

    [Fact]
    public async Task WhenNoWebhookIsSet_AStatusMessageGetsTheTableAndIsAcknowledged()
    {
        var http = new RoutingHandler(url =>
            url.Contains("getWebhookInfo") ? Json("{\"ok\":true,\"result\":{\"url\":\"\"}}")
            : url.Contains("getUpdates?offset=") ? Json("{\"ok\":true,\"result\":[]}")
            : Json("{\"ok\":true,\"result\":[{\"update_id\":5,\"message\":{\"text\":\"/status\",\"chat\":{\"id\":42}}}]}"));
        using var client = new HttpClient(http);
        var notifier = new RecordingNotifier();
        using var repo = await RepoAsync();
        using var log = new StringWriter();

        await new TelegramUpdateProcessor(client, Settings(), notifier, repo, log).ProcessPendingUpdatesAsync();

        Assert.Single(notifier.Messages);
        Assert.Contains(http.Urls, u => u.EndsWith("getUpdates?offset=6"));
    }

    [Fact]
    public async Task WhenGetUpdatesFails_ItLogsTheStatusInsteadOfFailingSilently()
    {
        var http = new RoutingHandler(url => url.Contains("getWebhookInfo")
            ? Json("{\"ok\":true,\"result\":{\"url\":\"\"}}")
            : new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") });
        using var client = new HttpClient(http);
        using var repo = await RepoAsync();
        using var log = new StringWriter();

        await new TelegramUpdateProcessor(client, Settings(), new RecordingNotifier(), repo, log).ProcessPendingUpdatesAsync();

        Assert.Contains("getUpdates failed: HTTP 409", log.ToString());
        Assert.DoesNotContain("123:tok", log.ToString());
    }

    [Fact]
    public async Task WhenTheWebhookStateCannotBeRead_ItFallsBackToGetUpdates()
    {
        var http = new RoutingHandler(url => url.Contains("getWebhookInfo")
            ? throw new HttpRequestException("boom")
            : Json("{\"ok\":true,\"result\":[]}"));
        using var client = new HttpClient(http);
        using var repo = await RepoAsync();
        using var log = new StringWriter();

        await new TelegramUpdateProcessor(client, Settings(), new RecordingNotifier(), repo, log).ProcessPendingUpdatesAsync();

        Assert.Contains(http.Urls, u => u.Contains("getUpdates"));
        Assert.Contains("Could not read the webhook state", log.ToString());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class RoutingHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Urls.Add(url);
            return Task.FromResult(route(url));
        }
    }

    private sealed class RecordingNotifier : ITelegramNotifier
    {
        public List<string> Messages { get; } = [];

        public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }

        public Task<bool> SendMessageWithKeyboardAsync(string message, object replyMarkup, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }
    }
}
