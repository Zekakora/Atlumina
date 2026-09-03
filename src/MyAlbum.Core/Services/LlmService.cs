using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyAlbum.Core.Services;

/// <summary>
/// A normalized place name in a five-level hierarchy: 国家 → 省/州 → 市 → 区/县/街道 →
/// 周边知名地标/景点 (e.g. 四川大学, 香港理工大学). Empty fields mean "no such level".
/// </summary>
public sealed record NormalizedAddress(
    string? Country, string? Province, string? City, string? District, string? Landmark)
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
        "你是地址规范化助手。把各种非标准、多语言或模糊的照片位置文本，统一规范为标准的「5 级层级结构」：\n" +
        "country(国家/地区，必须用通用简称：美国、中国、德国、法国、香港、台湾、澳门；严禁官方全称如中华人民共和国、美利坚合众国、德意志联邦共和国，严禁「中国台湾」「中国香港」「中国澳门」) · province(一级行政区) · city(二级行政区) · district(三级行政区/街区) · landmark(地标/POI)。\n" +
        "核心规则：\n" +
        "1) 国内普通地级市：country 填「中国」，province 填省/自治区（四川省），city 填带「市」的地级市（成都市），district 填区/县/街道（锦江区），landmark 填地标/POI（太古里）。\n" +
        "2) landmark(地标) 必须是具体地标/POI（建筑物、公园、车站、景点、展馆、商圈等），禁止填街道/路名（XX路、XX大街、XX街道）；街道/路名可放 district（如 菩提树下大街），但信息过多时优先舍弃街道/路名，不要硬塞进五级字段。\n" +
        "3) 国内市级行政区和省级后缀强制规范：地级市/自治州/地区/盟必须带完整官方后缀（成都市、凉山彝族自治州、延边朝鲜族自治州、锡林郭勒盟、四川省），禁止省略（成都、凉山、锡林郭勒、四川）。\n" +
        "4) 直辖市与城市州下沉（北京/天津/上海/重庆及国外城市州如柏林州、华盛顿特区）：不在 province 与 city 重复同一城市名——province 填直辖市/州名（天津市、柏林州），city 填市辖区/郡（和平区、米特区），district 填街道/商圈/历史片区（五大道历史文化街区、菩提树下大街），landmark 填地标（民园广场、勃兰登堡门）。\n" +
        "5) 港澳台与微型国家（层级不足时向上合并、把层级空间下沉给更细粒度概念）：\n" +
        "   - 香港/澳门：country 香港/澳门 · province 大区（香港岛/氹仔）· city 分区（中西区/嘉模堂区）· district 商圈/街区（中环/金光大道）· landmark 地标（国际金融中心/威尼斯人）。\n" +
        "   - 台湾：country 台湾 · province 县/市（台北市/屏东县）· city 乡镇市区（信义区/恒春镇）· district 商圈/街区（101商圈）· landmark 地标（台北101）。\n" +
        "   - 城邦/微型国（梵蒂冈/新加坡/摩纳哥）：country 国家 · province 城市/大区 · city 功能区/园区/片区 · district 建筑/广场 · landmark 具体设施/展厅/房间/POI。\n" +
        "6) 国外地名简化与去后缀规则：\n" +
        "   - 国外二级行政区（city）提取纯地名，去除“县/郡/地区/Zone/Landkreis/Region/District”等行政后缀（如：把“Landkreis Goslar”或“戈斯拉尔县”简化为“戈斯拉尔”；把“Region Hannover”或“汉诺威地区”简化为“汉诺威”）。\n" +
        "   - 示例：country 美国 · province 加利福尼亚州 · city 洛杉矶（非洛杉矶县）· district 圣莫尼卡 · landmark 圣莫尼卡码头。\n" +
        "   - 示例：country 德国 · province 下萨克森州 · city 戈斯拉尔（非戈斯拉尔县）· district 皇帝行宫片区 · landmark 戈斯拉尔皇帝行宫。\n" +
        "7) 海外数据源（OSM等）脏数据清洗规则：\n" +
        "   - 驼峰或连写拆分：若遇到 “LandkreisGoslar”、“DepartementDeLaSarthe” 等行政前缀连写，自动拆分为 [前缀] + [地名]。\n" +
        "   - 英文/外文后缀规范：遇到 “County”（如 “Orange County”），统一翻译并映射为 “奥兰治县”，放入 city 字段；遇到 “District” / “Borough” / “Quarter”，统一映射为 district 字段。\n" +
        "   - 门牌号与邮编过滤：忽略数字门牌号、5位数邮编（如 38640）等无意义细节，不要将其填入 district 或 landmark。\n" +
        "   - 多语言优先转中文：如果 OSM 返回的是英文或当地语言（如 “Tokyo”、“Shinjuku”），统一翻译为标准中文（“日本”·“東京都”·“新宿区”）。\n" +
        "8) 缺失层级：优先依据地理逻辑合理补充；确实无法补充时该字段留空，保持总体五级逻辑平滑过渡。信息过多时优先舍弃低价值细粒度信息（街道/路名/门牌），保留到区县与地标即可，不要硬塞满五级。\n" +
        "9) 语言要求：所有字段内容统一使用中文，若无官方中文翻译则保留原文。" +
        "只返回一个 JSON 对象，结构固定为：{\"results\":[{\"input\":\"原始地点名\",\"country\":\"\",\"province\":\"\",\"city\":\"\",\"district\":\"\",\"landmark\":\"\"}]}，每个原始地点对应 results 数组中的一项，input 必须原样等于原始地点名。不要输出任何多余文字。";

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
        var content = await SendChatAsync(SystemPrompt, "请规范化这些地点：" + json, ct);
        var ordered = ParseAddresses(content);
        // 用「输入顺序」把模型结果映射回原始地点名，而不是依赖模型回显的 input 精确匹配
        // （模型常会清洗/微调 input 字符串，导致字典键对不上、规范结果整批丢失）。
        result.Clear();
        for (int i = 0; i < places.Count; i++)
        {
            if (i < ordered.Count && !ordered[i].IsEmpty)
            {
                result[places[i]] = ordered[i];
            }
        }
        return new LlmNormalizeOutcome(result, content);
    }

    /// <summary>One already-normalized address to be re-checked against the original reverse-geocoded place.</summary>
    public sealed record AddressToVerify(long Id, string GpsPlace, NormalizedAddress Current);

    public sealed record LlmVerifyOutcome(Dictionary<long, NormalizedAddress> Map, string? Raw);

    /// <summary>
    /// Second-pass verification: re-sends each photo's raw reverse-geocoded place (which is
    /// already correct) together with the (possibly wrong) first-pass address, asking the model
    /// to correct obvious errors (e.g. a district/street misclassified as a province). Returns a
    /// map keyed by photo id. Results still pass through <see cref="AddressCanonicalizer"/>.
    /// </summary>
    public async Task<LlmVerifyOutcome> VerifyAsync(IReadOnlyList<AddressToVerify> items, CancellationToken ct = default)
    {
        var result = new Dictionary<long, NormalizedAddress>(items.Count);
        if (items.Count == 0 || !LlmConfig.IsConfigured)
        {
            return new LlmVerifyOutcome(result, null);
        }

        var payload = items.Select(x => new
        {
            id = x.Id,
            gpsPlace = x.GpsPlace,
            current = new
            {
                country = x.Current.Country,
                province = x.Current.Province,
                city = x.Current.City,
                district = x.Current.District,
                landmark = x.Current.Landmark,
            },
        }).ToList();
        var json = JsonSerializer.Serialize(payload);
        var content = await SendChatAsync(VerifySystemPrompt, "请核实并纠正以下已规范化地址：" + json, ct);
        return new LlmVerifyOutcome(ParseVerified(content), content);
    }

    private const string VerifySystemPrompt =
        "你是地址纠错助手。给定一批「已规范化的五级地址」（country 国家/地区、province 一级行政区、city 二级行政区、" +
        "district 三级行政区/街区、landmark 地标）以及它们的原始地点名（gpsPlace，来自地图反解、本身是正确的），" +
        "请依据 gpsPlace 的真实层级核实并纠正明显错误。\n" +
        "典型错误：把区/县/街道误判为省（如西贡、福田应是深圳的区/街道，绝不能是省）；省/市/区张冠李戴；" +
        "国外地址层级错乱；把港澳台/微型国家的国家或大区层级写错；直辖市与城市州重复；省略市级官方后缀。\n" +
        "纠正规则：\n" +
        "0) country 必须用通用简称（美国、中国、德国、法国、香港、台湾、澳门）；严禁官方全称（中华人民共和国、美利坚合众国、德意志联邦共和国）与「中国台湾」「中国香港」「中国澳门」写法。\n" +
        "1) 国内市级行政区必须带完整官方后缀（成都市、凉山彝族自治州、延边朝鲜族自治州、锡林郭勒盟），禁止省略。\n" +
        "2) landmark 必须是具体地标/POI（建筑物、公园、车站、景点、展馆、商圈等），禁止填街道/路名；街道/路名（XX路、XX大街）可放 district，信息过多时优先舍弃，不硬塞五级。\n" +
        "3) 直辖市与城市州下沉（北京/天津/上海/重庆及国外城市州如柏林州、华盛顿特区）：不在 province 与 city 重复——province 填直辖市/州名（天津市、柏林州），city 填市辖区/郡（和平区、米特区），district 填街道/商圈/历史片区，landmark 填地标。\n" +
        "4) 港澳台与微型国家下沉：香港/澳门：country 香港/澳门 · province 大区 · city 分区 · district 商圈/街区 · landmark 地标；台湾：country 台湾 · province 县/市 · city 乡镇市区 · district 商圈/街区 · landmark 地标；城邦/微型国（梵蒂冈/新加坡/摩纳哥）：country 国家 · province 城市/大区 · city 功能区/园区 · district 建筑/广场 · landmark 具体设施/POI。\n" +
        "5) 国外地名简化与去后缀：city 提取纯地名，去除“县/郡/地区/Zone/Landkreis/Region/District”等行政后缀（Landkreis Goslar→戈斯拉尔、Region Hannover→汉诺威、洛杉矶县→洛杉矶）；Country/District/Borough/Quarter 等英文后缀按要求映射后放入对应字段。\n" +
        "6) 海外脏数据清洗：行政前缀连写（LandkreisGoslar、DepartementDeLaSarthe）应拆为前缀+地名；忽略门牌号、5位邮编等细节；外文（Tokyo、Shinjuku）统一翻译为中文（東京→东京、新宿）。\n" +
        "7) 缺失层级：优先依据地理逻辑合理补充；确实无法补充时该字段留空，保持五级整体逻辑平滑过渡。信息过多时优先舍弃低价值细粒度信息（街道/路名/门牌），保留到区县与地标即可，不硬塞满五级。\n" +
        "8) 语言要求：所有字段内容统一使用中文（若无中文翻译则保留原文）。" +
        "只返回一个 JSON 对象，结构固定为：{\"results\":[{\"id\":<原 id 整数>,\"country\":\"\",\"province\":\"\"," +
        "\"city\":\"\",\"district\":\"\",\"landmark\":\"\"}]}，每个输入对应一项，id 必须等于原始 id，未改动的项也原样返回。" +
        "不要输出任何多余文字。";

    /// <summary>
    /// Shared chat-completion call with retry/backoff. Throws an informative exception on
    /// permanent failure (4xx, exhausted 429/5xx, network) so callers can surface it.
    /// </summary>
    private async Task<string> SendChatAsync(string systemPrompt, string userContent, CancellationToken ct)
    {
        string url = BuildUrl();
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LlmConfig.ApiKey);
                var body = new
                {
                    model = LlmConfig.Model,
                    temperature = 0,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userContent },
                    },
                };
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var reply = await response.Content.ReadAsStringAsync(ct);
                    var content = ParseContent(reply);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content;
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户手动取消 → 透传，不重试、不重复计费。
                throw;
            }
            catch (HttpRequestException ex)
            {
                // Network error → transient, retry with backoff.
                if (attempt >= MaxRetries)
                {
                    throw new InvalidOperationException("LLM 请求网络失败（已重试 " + MaxRetries + " 次）：" + ex.Message, ex);
                }
                try { await Task.Delay(RetryDelayMs(attempt, null), ct); } catch (OperationCanceledException) { throw; }
            }
            catch (TaskCanceledException ex)
            {
                // 真正的客户端超时：服务器可能仍在生成并已计费，此时重试会重复扣费。
                // 因此不重试，直接抛出，提示用户调小批/并行数。
                throw new InvalidOperationException(
                    "LLM 请求超时（超过 " + HttpTimeoutSeconds + " 秒）：服务器可能仍在生成并已计费，未自动重试以免重复扣费。" +
                    "请在「设置 → 大语言模型」降低「每批地点数」或「规范化并行数」后重试。", ex);
            }
        }
    }

    private static Dictionary<long, NormalizedAddress> ParseVerified(string content)
    {
        var result = new Dictionary<long, NormalizedAddress>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }
                    long id = idEl.GetInt64();
                    result[id] = AddressCanonicalizer.Canonicalize(new NormalizedAddress(
                        Get(item, "country"), Get(item, "province"), Get(item, "city"),
                        Get(item, "district"), Get(item, "landmark")));
                }
            }
        }
        catch (JsonException)
        {
            // malformed model output — return what we have
        }
        return result;
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
    private static List<NormalizedAddress> ParseAddresses(string content)
    {
        var result = new List<NormalizedAddress>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Preferred shape: {"results":[{"input":"...","country":...}]}, returned in input order.
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    result.Add(AddressCanonicalizer.Canonicalize(new NormalizedAddress(
                        Get(item, "country"), Get(item, "province"), Get(item, "city"),
                        Get(item, "district"), Get(item, "landmark"))));
                }
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

    private const int HttpTimeoutSeconds = 300;

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            UseProxy = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds) };
        return client;
    }
}
