using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ShiroBot.DiscordAdapter;

internal sealed class BoundedCache<T>(int capacity = 2048)
{
    private readonly ConcurrentDictionary<string, T> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _keys = new();
    public void Set(string key, T value)
    {
        if (_values.TryAdd(key, value)) _keys.Enqueue(key);
        else _values[key] = value;
        while (_values.Count > capacity && _keys.TryDequeue(out var oldest)) _values.TryRemove(oldest, out _);
    }
    public bool TryGet(string key, out T value) => _values.TryGetValue(key, out value!);
    public void Clear() { _values.Clear(); _keys.Clear(); }
}

internal static class ButtonCodec
{
    public static string Encode(string id, string data) => "sb:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { id, data }))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static (string Id, string Data) Decode(string value)
    {
        if (value.StartsWith("sb:", StringComparison.Ordinal))
        {
            try
            {
                var text = value[3..].Replace('-', '+').Replace('_', '/');
                text = text.PadRight((text.Length + 3) / 4 * 4, '=');
                var pair = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(text));
                if (pair is { Length: 2 } && pair.All(x => x is not null)) return (pair[0], pair[1]);
            }
            catch (Exception ex) when (ex is FormatException or JsonException) { }
        }
        return (value, value);
    }
}

internal sealed record ResourceBytes(byte[] Bytes, string FileName);
internal static class ResourceReader
{
    private const int MaxBytes = 50 * 1024 * 1024;
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(2) };
    public static void Validate(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        if (Path.IsPathFullyQualified(uri) || uri.StartsWith("data:", StringComparison.Ordinal)
            || uri.StartsWith("base64://", StringComparison.Ordinal)) return;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "file") return;
        throw new NotSupportedException("资源必须是 HTTP(S)、本地绝对路径、file URI、data URI 或 base64:// 数据。");
    }
    public static async Task<ResourceBytes> ReadAsync(string uri, string? fileName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(uri);
        if (uri.StartsWith("data:", StringComparison.Ordinal) || uri.StartsWith("base64://", StringComparison.Ordinal))
        {
            var encoded = uri.StartsWith("base64://", StringComparison.Ordinal) ? uri[9..] : DataBody(uri);
            if (encoded.Length > (MaxBytes + 2L) / 3 * 4) throw new NotSupportedException("资源超过 50 MiB 本地读取限制。");
            return new(Convert.FromBase64String(encoded), SafeName(fileName ?? "upload.bin"));
        }
        if (Uri.TryCreate(uri, UriKind.Absolute, out var remote) && remote.Scheme is "http" or "https")
        {
            using var response = await Downloads.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            return new(await ReadLimitedAsync(input, token), SafeName(fileName ?? Path.GetFileName(remote.LocalPath)));
        }
        var path = uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(uri).LocalPath : uri;
        await using var file = File.OpenRead(path);
        return new(await ReadLimitedAsync(file, token), SafeName(fileName ?? Path.GetFileName(path)));
    }
    private static string DataBody(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("data URI 必须使用 base64 编码。");
        return uri[(comma + 1)..];
    }
    private static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/')).Replace("\r", "").Replace("\n", "");
        return string.IsNullOrWhiteSpace(name) ? "upload.bin" : name;
    }
    private static async Task<byte[]> ReadLimitedAsync(Stream input, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > MaxBytes) throw new NotSupportedException("资源超过 50 MiB 本地读取限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
}
