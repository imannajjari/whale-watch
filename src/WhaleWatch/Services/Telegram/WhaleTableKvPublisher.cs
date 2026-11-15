using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Models.Telegram;

namespace WhaleWatch.Services.Telegram;

public sealed class WhaleTableKvPublisher(HttpClient httpClient, CloudflareKvSettings settings, TextWriter? warningLog = null)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly CloudflareKvSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly TextWriter _warningLog = warningLog ?? Console.Error;

    public string BuildUrl() =>
        $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(_settings.AccountId)}"
        + $"/storage/kv/namespaces/{Uri.EscapeDataString(_settings.NamespaceId)}"
        + $"/values/{Uri.EscapeDataString(_settings.Key)}";

    // Never throws (except for cancellation): a publishing problem must not fail the poll that already saved its data.
    public async Task<bool> PublishCurrentTableAsync(ISmartMoneyRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            string table = await TelegramWhaleTableFormatter.FormatPositionsTableAsync(repository, cancellationToken);
            return await PublishAsync(table, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _warningLog.WriteLine($"[Cloudflare Warning] Could not build the whale table: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> PublishAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, BuildUrl());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiToken);
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Whale table published to Cloudflare KV.");
                return true;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            _warningLog.WriteLine($"[Cloudflare Warning] KV write failed: HTTP {(int)response.StatusCode}: {body}");
            return false;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _warningLog.WriteLine($"[Cloudflare Warning] KV write failed: {ex.Message}");
            return false;
        }
    }
}
