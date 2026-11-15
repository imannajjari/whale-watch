using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.Telegram;
using WhaleWatch.Models.Telegram;
using WhaleWatch.Services.SmartMoney;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public sealed class TelegramSampleSenderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 30, 0, TimeSpan.Zero);

    private static TelegramSettings ValidSettings() => new() { Enabled = true, BotToken = "123:secret-token-value", ChatId = "42" };

    [Fact]
    public void BuildMessages_ReturnsThreeClearlyMarkedSamples()
    {
        var sender = new TelegramSampleSender(new RecordingNotifier(), new WhaleAlertFormatter("UTC"));

        var messages = sender.BuildMessages(Now);

        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.StartsWith("\U0001F9EA <b>TEST MESSAGE</b>", m));
        Assert.Contains("already open, first seen", messages[0]);
        Assert.Contains("<b>Market price:</b> $75.77", messages[0]);
        Assert.Contains("WHALE SHORT OPENED (3 fills)", messages[1]);
        Assert.Contains("<b>Trade time:</b> 2026-11-14 12:21:00 UTC", messages[1]);
        Assert.Contains("<b>Position now:</b> 86.60", messages[1]);
        Assert.Contains("<b>Liquidation:</b> $65,051.31", messages[1]);
        Assert.Contains("<b>Same run:</b> closed Long", messages[1]);
        Assert.Contains("WHALE CLOSED (detected between checks)", messages[2]);
        Assert.All(messages, m => Assert.Contains("0x000000...000001", m));
    }

    [Fact]
    public async Task SendAsync_SendsEveryMessageAndReturnsTheTotalItBuilt()
    {
        var notifier = new RecordingNotifier();
        var sender = new TelegramSampleSender(notifier, new WhaleAlertFormatter("UTC"));

        var (sent, total) = await sender.SendAsync(Now);

        Assert.Equal(sender.BuildMessages(Now).Count, total);
        Assert.Equal(total, sent);
        Assert.Equal(total, notifier.Messages.Count);
    }

    [Fact]
    public async Task RunAsync_WhenNotifierRefuses_ReturnsOneAndSaysSo()
    {
        var sender = new TelegramSampleSender(new RecordingNotifier { Result = false }, new WhaleAlertFormatter("UTC"));
        using var log = new StringWriter();

        int exit = await sender.RunAsync(ValidSettings(), Now, log);

        Assert.Equal(1, exit);
        Assert.Contains("0 of 3", log.ToString());
        Assert.Contains("not delivered", log.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenAllSent_ReturnsZero()
    {
        var sender = new TelegramSampleSender(new RecordingNotifier(), new WhaleAlertFormatter("UTC"));
        using var log = new StringWriter();

        Assert.Equal(0, await sender.RunAsync(ValidSettings(), Now, log));
        Assert.Contains("3 of 3", log.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenChatIdIsMissing_NamesTheProblemWithoutSendingOrPrintingTheToken()
    {
        var notifier = new RecordingNotifier();
        var sender = new TelegramSampleSender(notifier, new WhaleAlertFormatter("UTC"));
        var settings = ValidSettings();
        settings.ChatId = "";
        using var log = new StringWriter();

        int exit = await sender.RunAsync(settings, Now, log);

        Assert.Equal(1, exit);
        Assert.Empty(notifier.Messages);
        Assert.Contains("TELEGRAM_CHAT_ID is empty", log.ToString());
        Assert.DoesNotContain("TELEGRAM_BOT_TOKEN", log.ToString());
        Assert.DoesNotContain("secret-token-value", log.ToString());
    }

    [Fact]
    public void Diagnose_ListsEveryMissingPieceAndNeverEchoesValues()
    {
        var problems = TelegramSampleSender.Diagnose(new TelegramSettings { Enabled = false, BotToken = "", ChatId = " " });

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("TELEGRAM_BOT_TOKEN"));
        Assert.Contains(problems, p => p.StartsWith("TELEGRAM_CHAT_ID"));
        Assert.Contains(problems, p => p.StartsWith("Telegram.Enabled"));
        Assert.Empty(TelegramSampleSender.Diagnose(ValidSettings()));
    }

    [Theory]
    [InlineData("--telegram-sample", true)]
    [InlineData("--TELEGRAM-SAMPLE", true)]
    [InlineData("--smart-money-poll", false)]
    public void IsRequested_MatchesOnlyTheSwitch(string arg, bool expected)
    {
        Assert.Equal(expected, TelegramSampleSender.IsRequested(["-s", arg]));
    }

    private sealed class RecordingNotifier : ITelegramNotifier
    {
        public List<string> Messages { get; } = [];
        public bool Result { get; init; } = true;

        public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(Result);
        }

        public Task<bool> SendMessageWithKeyboardAsync(string message, object replyMarkup, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(Result);
        }
    }
}
