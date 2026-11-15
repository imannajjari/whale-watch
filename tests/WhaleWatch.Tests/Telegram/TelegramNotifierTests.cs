using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Models.Telegram;
using WhaleWatch.Services.Telegram;
using Xunit;

namespace WhaleWatch.Tests.Telegram;

public sealed class TelegramNotifierTests
{
    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return response;
        }
    }

    [Fact]
    public async Task SendMessageAsync_WhenDisabled_ReturnsFalseWithoutCallingHttp()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var settings = new TelegramSettings { Enabled = false, BotToken = "fake:token", ChatId = "123" };
        var notifier = new TelegramNotifier(client, settings);

        bool result = await notifier.SendMessageAsync("test");

        Assert.False(result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SendMessageAsync_WhenTokenOrChatMissing_ReturnsFalse()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var settings = new TelegramSettings { Enabled = true, BotToken = "", ChatId = "" };
        var notifier = new TelegramNotifier(client, settings);

        bool result = await notifier.SendMessageAsync("test");

        Assert.False(result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SendMessageAsync_WhenValid_SendsPostWithJsonPayload()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var settings = new TelegramSettings { Enabled = true, BotToken = "123456:ABC-DEF", ChatId = "987654" };
        var notifier = new TelegramNotifier(client, settings);

        bool result = await notifier.SendMessageAsync("<b>Hello</b>");

        Assert.True(result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("123456:ABC-DEF", handler.LastRequest.RequestUri?.ToString());

        string body = handler.CapturedBody!;
        Assert.Contains("987654", body);
        Assert.Contains("<b>Hello</b>", body);
    }
}
