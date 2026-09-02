using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MyAlbum.Core.Services;

/// <summary>
/// A normalized place name in a five-level hierarchy: 国家 → 省/州 → 市 → 区/县/街道 →
/// 周边知名地标/景点 (e.g. 四川大学, 香港理工大学). Empty fields mean "no such level".
/// </summary>
public sealed record NormalizedAddress(
    string Country, string Province, string City, string District, string Landmark)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Country);

    public string Display => string.Join(" · ",
        new[] { Country, Province, City, District, Landmark }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Minimal OpenAI-compatible chat-completions client used to normalize reverse-geocoded place
/// names into the structured five-level address. Reads config from <see cref="LlmConfig"/>.
/// </summary>
public sealed class LlmService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private const string SystemPrompt =
        "你是地址规范化助手。把用户给出的地点名称规范为五级结构化字段：" +
        "country 国家、province 省/州/自治区、city 市、district 区/县/街道、landmark 该地点周边的知名地标或景点（如四川大学、香港理工大学；没有则留空）。" +
        "规则：直辖市（北京/天津/上海/重庆/柏林等）province 留空、city 填市名；特别行政区或小国（香港/澳门等）country 填其名、其余留空；国外保持三级（country/province/city）。" +
        "只返回一个 JSON 对象，结构固定为：{\"results\":[{\"input\":\"原始地点名\",\"country\":\"\",\"province\":\"\",\"city\":\"\",\"district\":\"\",\"landmark\":\"\"}]}，" +
        "每个原始地点对应 results 数组中的一项，input 必须等于原始地点名原样。不要输出任何多余文字。";

    public bool IsConfigured => LlmConfig.IsConfigured;

    /// <summary>
    /// Normalizes a batch of raw place names (keys) into their structured addresses.
    /// Returns a map keyed by the original name for every name the model resolved, plus the
    /// raw model text for diagnostics. Transient failures (HTTP 429 / 5xx, timeouts, malformed
    /// output) are retried with exponential backoff (honoring the server's Retry-After when
    /// present) so a large library batch is not lost to a single rate-limit hiccup.
    /// </summary>
    public sealed record LlmNormalizeOutcome(Dictionary<string, NormalizedAddress> Map, string? Raw);

    public async Task<LlmNormalizeOutcome> NormalizeAsync(
        IReadOnlyList<string> places,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, NormalizedAddress>(places.Count);
        if (places.Count == 0 || !LlmConfig.IsConfigured)
        {
            return new LlmNormalizeOutcome(result, null);
        }

        var json = JsonSerializer.Serialize(places);
        var body = new
        {
            model = LlmConfig.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = "请规范化这些地点：" + json },
            },
        };
        string url = BuildUrl();

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LlmConfig.ApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var reply = await response.Content.ReadAsStringAsync(ct);
                    var content = ParseContent(reply);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return new LlmNormalizeOutcome(ParseAddresses(content), content);
                    }
                    // Success status but unparsable body → retry once more, then give up loudly.
                    if (attempt >= MaxRetries)
                    {
                        throw new InvalidOperationException(
                            "LLM 返回 200 但 content 无法解析（模型输出可能被截断，或不含 choices[0].message.content）。");
                    }
                }
                else if (attempt >= MaxRetries)
                {
                    // Rate-limited / server error exhausted retries — surface instead of swallow.
                    var status = (int)response.StatusCode;
                    if (status == 429)
                    {
                        throw new InvalidOperationException(
                            "LLM 请求在重试 3 次后仍被限流（HTTP 429）。请降低「设置 → 并行数」或减少批大小，或更换不限流的密钥。");
                    }
                    if (status >= 500)
                    {
                        throw new InvalidOperationException($"LLM 服务器错误 HTTP {status}，重试 3 次后仍失败。");
                    }
                    throw new InvalidOperationException(
                        $"LLM 请求失败 HTTP {status}：请检查 API 密钥、模型名（{LlmConfig.Model}）与 BaseUrl（{LlmConfig.BaseUrl}）是否正确。");
                }
                else if ((int)response.StatusCode != 429 && (int)response.StatusCode < 500)
                {
                    // 4xx other than 429 is a request problem (bad key / unknown model /
                    // unsupported response_format) — surface it instead of silently skipping.
                    throw new InvalidOperationException(
                        $"LLM 请求失败 HTTP {(int)response.StatusCode}：请检查 API 密钥、模型名（{LlmConfig.Model}）与 BaseUrl（{LlmConfig.BaseUrl}）是否正确。");
                }

                // Rate-limited / server error → back off, honoring Retry-After.
                var delay = RetryDelayMs(attempt, response);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { throw; }
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxRetries)
                {
                    throw new InvalidOperationException("LLM 请求网络失败（超时或无法连接）：" + ex.Message, ex);
                }
                try { await Task.Delay(RetryDelayMs(attempt, null), ct); } catch (OperationCanceledException) { throw; }
            }
        }
    }

    private const int MaxRetries = 3;

    private static int RetryDelayMs(int attempt, HttpResponseMessage? response)
    {
        if (response is not null && response.Headers.RetryAfter?.Delta is { } delta)
        {
            return Math.Clamp((int)delta.TotalMilliseconds, 200, 8000);
        }
        return attempt switch { 1 => 1000, 2 => 2000, _ => 4000 };
    }

    private static string BuildUrl()
    {
        string url = LlmConfig.BaseUrl;
        if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + "/chat/completions";
        }
        return url;
    }

    private static Dictionary<string, NormalizedAddress> ParseAddresses(string content)
    {
        var result = new Dictionary<string, NormalizedAddress>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Preferred shape: {"results":[{"input":"...","country":...}]}.
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var input = Get(item, "input");
                    if (string.IsNullOrWhiteSpace(input))
                    {
                        continue;
                    }
                    result[input] = new NormalizedAddress(
                        Get(item, "country"), Get(item, "province"), Get(item, "city"),
                        Get(item, "district"), Get(item, "landmark"));
                }
                if (result.Count > 0)
                {
                    return result;
                }
            }

            // Backward-compatible shape: a flat object keyed by the original place name.
            foreach (var prop in root.EnumerateObject())
            {
                var v = prop.Value;
                if (v.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                result[prop.Name] = new NormalizedAddress(
                    Get(v, "country"), Get(v, "province"), Get(v, "city"), Get(v, "district"), Get(v, "landmark"));
            }
        }
        catch (JsonException)
        {
            // malformed model output — return what we have
        }
        return result;
    }

    private static string? ParseContent(string reply)
    {
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }

    private static string Get(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }
        return "";
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            UseProxy = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        return client;
    }
}
