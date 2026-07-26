using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShiroBot.DiscordAdapter;

/// <summary>Discord REST API v10 客户端(带简单 429 重试)。</summary>
public sealed class DiscordRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public DiscordRestClient(string botToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        _http = new HttpClient { BaseAddress = new Uri("https://discord.com/api/v10/") };
        _http.DefaultRequestHeaders.Authorization = new("Bot", botToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/ShirokaProject/ShiroBot, 0.1)");
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    public async Task<T> PostAsync<T>(string path, object payload, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload, options: JsonOptions) },
            cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    public async Task PostAsync(string path, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = payload is null ? null : JsonContent.Create(payload, options: JsonOptions)
            },
            cancellationToken);
    }

    public async Task PatchAsync(string path, object payload, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(payload, options: JsonOptions) },
            cancellationToken);
    }

    public async Task PutAsync(string path, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Put, path)
            {
                Content = payload is null ? null : JsonContent.Create(payload, options: JsonOptions)
            },
            cancellationToken);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, path), cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            var response = await _http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                response.Dispose();
                await Task.Delay(retryAfter, cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                throw new HttpRequestException(
                    $"Discord API {request.Method} {request.RequestUri} 失败: {(int)response.StatusCode} {body}");
            }

            return response;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
        ?? throw new InvalidOperationException("Discord API 返回空响应。");
}
