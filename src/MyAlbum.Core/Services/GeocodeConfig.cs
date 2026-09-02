namespace MyAlbum.Core.Services;

/// <summary>
/// Reverse-geocoding source configuration: OSM Nominatim (international, rate-limited ~1 req/s)
/// or 高德 AMap regeo (domestic, requires a free API key, supports concurrent requests).
/// The App mirrors the persisted settings into this static.
/// </summary>
public static class GeocodeConfig
{
    /// <summary>"osm" (Nominatim) or "amap" (高德).</summary>
    public static string Source { get; private set; } = "osm";

    public static string AmapKey { get; private set; } = "";

    /// <summary>高德安全密钥（用于 sig 数字签名；键未开启该特性时可为空）。</summary>
    public static string AmapSecret { get; private set; } = "";

    public static void Set(string source, string amapKey, string amapSecret)
    {
        Source = string.Equals(source, "amap", StringComparison.OrdinalIgnoreCase) ? "amap" : "osm";
        AmapKey = (amapKey ?? "").Trim();
        AmapSecret = (amapSecret ?? "").Trim();
    }
}

/// <summary>
/// Global processing tuning: reverse-geocoding parallelism (经纬度解析) and LLM address
/// normalization parallelism + batch size are kept separate because they have very different
/// concurrency profiles — Nominatim is rate-limited to 1 req/s while Amap supports concurrency,
/// and LLM calls are HTTP-bound but subject to provider rate limits.
/// </summary>
public static class ProcessingConfig
{
    public const int MaxParallelism = 500;
    public const int MinLlmBatch = 20;
    public const int MaxLlmBatch = 100;

    /// <summary>并行度：经纬度反地理编码（1..500）。</summary>
    public static int GeocodeParallelism { get; private set; } = 4;

    /// <summary>并行度：LLM 地址规范化（1..500）。</summary>
    public static int LlmParallelism { get; private set; } = 16;

    /// <summary>每次 LLM 请求处理的地点名数量（20..100）。</summary>
    public static int LlmBatchSize { get; private set; } = 40;

    public static void SetGeocodeParallelism(int value) => GeocodeParallelism = Math.Clamp(value, 1, MaxParallelism);

    public static void SetLlmParallelism(int value) => LlmParallelism = Math.Clamp(value, 1, MaxParallelism);

    public static void SetLlmBatchSize(int value) => LlmBatchSize = Math.Clamp(value, MinLlmBatch, MaxLlmBatch);
}
