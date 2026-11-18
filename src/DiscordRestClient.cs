using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShiroBot.DiscordAdapter;

/// <summary>Discord REST API v10 客户端(带简单 429 重试)。</summary>
public sealed class DiscordRestClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public DiscordRestClient(string botToken, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
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

    internal async Task<T> SendPayloadAsync<T>(HttpMethod method, string path, object payload,
        IReadOnlyList<ResourceBytes> files, CancellationToken token)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            if (files.Count == 0)
                return new HttpRequestMessage(method, path) { Content = JsonContent.Create(payload, options: JsonOptions) };
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(payload, JsonOptions), System.Text.Encoding.UTF8, "application/json"), "payload_json");
            for (var i = 0; i < files.Count; i++)
                form.Add(new ByteArrayContent(files[i].Bytes), $"files[{i}]", files[i].FileName);
            return new HttpRequestMessage(method, path) { Content = form };
        }, token);
        return await ReadAsync<T>(response, token);
    }

    /// <summary>
    ///     调用任意 REST 路由并返回响应 JSON；204 时为 Undefined。有文件时以 multipart 发送，body 作为 payload_json。
    ///     <paramref name="reason"/> 写入审计日志（X-Audit-Log-Reason）。
    /// </summary>
    internal async Task<JsonElement> SendAsync(HttpMethod method, string path, System.Text.Json.Nodes.JsonNode? body = null,
        IReadOnlyList<ResourceBytes>? files = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = body?.ToJsonString();
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(method, path.TrimStart('/'));
            if (files is { Count: > 0 })
            {
                var form = new MultipartFormDataContent();
                if (json is not null) form.Add(new StringContent(json, System.Text.Encoding.UTF8, "application/json"), "payload_json");
                for (var i = 0; i < files.Count; i++)
                    form.Add(new ByteArrayContent(files[i].Bytes), $"files[{i}]", files[i].FileName);
                request.Content = form;
            }
            else if (json is not null)
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(reason))
                request.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", Uri.EscapeDataString(reason));
            return request;
        }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return default;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
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
                    $"Discord API {request.Method} 请求失败: {(int)response.StatusCode} {body}", null, response.StatusCode);
            }

            return response;
        }
    }

    public void Dispose() => _http.Dispose();

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
        ?? throw new InvalidOperationException("Discord API 返回空响应。");
}
