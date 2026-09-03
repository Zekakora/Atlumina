using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyAlbum.Core.Services;

/// <summary>
/// Reverse geocoding for photos: resolves a GPS coordinate to a human-readable place name
/// chain (省 → 市 → 区/县 → 街道/地标) that the LLM normalization turns into the five-level
/// address. Source is configurable via <see cref="GeocodeConfig"/>:
/// - "osm": OSM Nominatim (international; rate-limited ~1 req/s so a shared delay is enforced)
/// - "amap": 高德 AMap regeo (domestic; needs a free API key, supports concurrent requests)
/// Results are stored in Photos.GpsPlace by the <see cref="GpsPlaceService"/> background pass.
/// </summary>
public sealed class ReverseGeocodeService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private const string NominatimUrl =
        "https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={0}&lon={1}&accept-language=zh-CN&zoom=18&addressdetails=1";

    /// <summary>Nominatim asks for at most 1 request/second; the AMap source skips this gate.</summary>
    private readonly SemaphoreSlim _rateGate = new(1, 1);

    /// <summary>Result of one reverse-geocode attempt: the place (if any) and which source produced it.</summary>
    public sealed record GeocodeResult(string? Place, string? Source);

    /// <summary>
    /// Resolves <paramref name="lat"/>/<paramref name="lon"/> to a place-name chain. Picks the
    /// primary source by location — 高德 (Amap) for mainland-China coordinates, OSM Nominatim
    /// for overseas — then automatically falls back to the other source if the first fails
    /// (e.g. 高德 fails for overseas addresses → OSM; OSM rate-limited/offline → 高德). The
    /// returned <see cref="GeocodeResult.Source"/> records which source succeeded so the caller
    /// can mark the photo accordingly.
    /// </summary>
    public async Task<GeocodeResult> ResolveAsync(double lat, double lon, CancellationToken ct = default)
    {
        // 智能选择：中国大陆优先高德（覆盖好、支持并发），境外优先 OSM（国际覆盖好）；
        // 任一源失败都自动回退到另一源，保证尽量反解成功。
        var (primary, fallback) = PickSources(lat, lon);

        if (primary is not null)
        {
            var r = await TrySourceAsync(primary, lat, lon, ct);
            if (r.Place is not null)
            {
                return r;
            }
        }
        if (fallback is not null)
        {
            var r = await TrySourceAsync(fallback, lat, lon, ct);
            if (r.Place is not null)
            {
                return r;
            }
        }
        return new GeocodeResult(null, null);
    }

    /// <summary>
    /// 依据坐标位置挑选反解主源与兜底源：中国大陆（含港澳台）优先高德，境外优先 OSM。
    /// 高德仅在配置了 API Key 时可用；否则两地都只用 OSM。
    /// </summary>
    private static (string? Primary, string? Fallback) PickSources(double lat, double lon)
    {
        bool inChina = lat is >= 18.0 and <= 53.6 && lon is >= 73.5 and <= 134.8;
        bool amapUsable = !string.IsNullOrWhiteSpace(GeocodeConfig.AmapKey);
        if (inChina)
        {
            // 中国大陆：优先高德（可用时），OSM 兜底。
            return (amapUsable ? "amap" : "osm", amapUsable ? "osm" : null);
        }
        // 境外：优先 OSM，高德仅作兜底（境外覆盖差）。
        return ("osm", amapUsable ? "amap" : null);
    }

    private async Task<GeocodeResult> TrySourceAsync(string source, double lat, double lon, CancellationToken ct)
    {
        try
        {
            string? place = source == "amap"
                ? await ResolveAmapAsync(lat, lon, ct)
                : await ResolveNominatimAsync(lat, lon, ct);
            return new GeocodeResult(place, place is null ? null : source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HTTP timeout / other non-user cancellation → per-photo failure, never abort the batch.
            return new GeocodeResult(null, null);
        }
        catch
        {
            return new GeocodeResult(null, null);
        }
    }

    private async Task<string?> ResolveNominatimAsync(double lat, double lon, CancellationToken ct)
    {
        await _rateGate.WaitAsync(ct);
        try
        {
            var url = string.Format(NominatimUrl, lat.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture),
                lon.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture));
            using var response = await Http.GetAsync(url, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            return ParseNominatim(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null; // HTTP timeout / unknown cancel → this photo fails, batch continues
        }
        catch
        {
            return null;
        }
        finally
        {
            try { await Task.Delay(1100, CancellationToken.None); } catch { }
            _rateGate.Release();
        }
    }

    private async Task<string?> ResolveAmapAsync(double lat, double lon, CancellationToken ct)
    {
        var prms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = GeocodeConfig.AmapKey,
            ["location"] = $"{lon.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}," +
                           $"{lat.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}",
            ["extensions"] = "all",
            ["radius"] = "1000",
        };
        if (!string.IsNullOrWhiteSpace(GeocodeConfig.AmapSecret))
        {
            prms["sig"] = AmapSignature(prms, GeocodeConfig.AmapSecret);
        }
        var qs = string.Join("&", prms.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        try
        {
            using var response = await Http.GetAsync("https://restapi.amap.com/v3/geocode/regeo?" + qs, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            return ParseAmap(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null; // HTTP timeout / unknown cancel → this photo fails, batch continues
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 高德 Web 服务数字签名：除 sig 外的全部参数按参数名升序拼接为 "k=v&k=v"，末尾拼上
    /// 安全密钥，整体做 MD5（十六进制小写）。
    /// </summary>
    private static string AmapSignature(IDictionary<string, string> prms, string secret)
    {
        var raw = string.Join("&", prms
            .Where(kv => kv.Key != "sig")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}")) + secret;
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>Builds a compact place chain from Nominatim's address object (省→市→区县→街道/地标).</summary>
    private static string? ParseNominatim(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("address", out var addr))
            {
                return null;
            }
            string? name = Get(addr, "name");
            string? road = Get(addr, "road") ?? Get(addr, "pedestrian") ?? Get(addr, "footway") ?? Get(addr, "residential");
            string? district = Get(addr, "county") ?? Get(addr, "suburb") ?? Get(addr, "city_district");
            string? city = Get(addr, "city") ?? Get(addr, "town") ?? Get(addr, "village") ?? Get(addr, "municipality");
            string? state = Get(addr, "state") ?? Get(addr, "province") ?? Get(addr, "region");
            string? country = Get(addr, "country");

            var parts = new List<string>(4);
            if (country is not null && country != "中华人民共和国" && country != "中国")
            {
                parts.Add(country);
            }
            if (state is not null)
            {
                parts.Add(state);
            }
            if (city is not null && !string.Equals(city, state, StringComparison.Ordinal))
            {
                parts.Add(city);
            }
            if (district is not null && !string.Equals(district, city, StringComparison.Ordinal))
            {
                parts.Add(district);
            }
            if (name is { Length: > 0 and <= 24 } && name != city && name != district)
            {
                parts.Add(name);
            }
            else if (road is not null && road != district && road != city)
            {
                parts.Add(road);
            }
            return parts.Count == 0 ? null : SplitJoinedAdminWords(string.Join("", parts));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds a place chain from 高德 regeo (省 → 市 → 区 → 街道 → POI/地标).</summary>
    private static string? ParseAmap(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "1"
                || !root.TryGetProperty("regeocode", out var regeo))
            {
                return null;
            }
            var comp = regeo.TryGetProperty("addressComponent", out var c) ? c : default;
            string? province = Get(comp, "province");
            string? city = Get(comp, "city");
            string? district = Get(comp, "district");
            string? township = Get(comp, "township");
            string? street = null;
            if (comp.ValueKind == JsonValueKind.Object && comp.TryGetProperty("streetNumber", out var sn)
                && sn.TryGetProperty("street", out var st) && st.ValueKind == JsonValueKind.String)
            {
                street = st.GetString();
            }
            string? poi = null;
            if (regeo.TryGetProperty("pois", out var pois) && pois.ValueKind == JsonValueKind.Array
                && pois.GetArrayLength() > 0 && pois[0].TryGetProperty("name", out var pn))
            {
                poi = pn.GetString();
            }

            var parts = new List<string>(5);
            if (!string.IsNullOrWhiteSpace(province)) parts.Add(province!);
            if (!string.IsNullOrWhiteSpace(city) && city != province) parts.Add(city!);
            if (!string.IsNullOrWhiteSpace(district) && district != city) parts.Add(district!);
            if (!string.IsNullOrWhiteSpace(township) && township != district) parts.Add(township!);
            if (!string.IsNullOrWhiteSpace(street) && street != township) parts.Add(street!);
            if (poi is { Length: > 0 and <= 24 } && poi != street) parts.Add(poi);
            return parts.Count == 0 ? null : SplitJoinedAdminWords(string.Join("", parts));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Inserts a space between a common administrative word and the proper name glued to it by
    /// <see cref="string.Join(string, IEnumerable{string})"/> with an empty separator — e.g.
    /// "LandkreisGoslar" → "Landkreis Goslar", "DepartementDeLaSarthe" → "Departement De La Sarthe",
    /// "RegionHannover" → "Region Hannover", "ProvinciaGranada" → "Provincia Granada". Covers the
    /// frequent 德/法/西/英 行政前缀 so the concatenated reverse-geocode chain stays readable and
    /// the LLM can split it into the five levels instead of swallowing the admin word into one token.
    /// </summary>
    private static readonly Regex AdminWordJoin = new(
        @"(?<=(?:Landkreis|Regierungsbezirk|Kreisfreie|Stadtbezirk|Bezirk|Kreis|Region|Gemeinde|Amt|Freistaat|Stadt|Sankt|Departement|Arrondissement|Commune|Canton|Préfecture|Prefeitura|Provincia|Province|Municipio|Comarca|Distrito|Barrio|Partido|Comunidad|County|Borough|Quarter|District|Parish|Township|Municipality|Consell|Ville|Vila|Pueblo|San|Santa|Santo|São|Porto|Fuerte|Villa|Mount|Lake|Fort|Cape|North|South|East|West|Upper|Lower|New|Great|Little))(?!\s)(?=[A-ZÀÂÄÉÈÊËÎÏÔÖÙÛÜÇÑÁÍÓÚ])",
        RegexOptions.Compiled);

    private static string SplitJoinedAdminWords(string chain) =>
        AdminWordJoin.Replace(chain, " ");

    private static string? Get(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            UseProxy = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyAlbum/1.0 (photo organizer; contact: local)");
        return client;
    }
}
