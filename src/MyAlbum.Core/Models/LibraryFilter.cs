using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAlbum.Core.Models;

/// <summary>
/// The active filter criteria for the photo view, also serialized as the
/// definition of a smart album. Null fields mean "no constraint".
/// </summary>
public sealed class LibraryFilter
{
    public string? FolderPath { get; set; }
    public string? CameraModel { get; set; }
    public int? RatingMin { get; set; }
    public string? TagName { get; set; }
    public string? SearchText { get; set; }
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }

    /// <summary>LLM-normalized location tree filter: country → province → city (null = no constraint).</summary>
    public string? PlaceCountry { get; set; }
    public string? PlaceProvince { get; set; }
    public string? PlaceCity { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static LibraryFilter FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryFilter>(json, Options) ?? new LibraryFilter();
        }
        catch
        {
            return new LibraryFilter();
        }
    }

    /// <summary>True when no constraints are set.</summary>
    [JsonIgnore]
    public bool IsEmpty => FolderPath is null && CameraModel is null && RatingMin is null
        && TagName is null && SearchText is null && DateFrom is null && DateTo is null
        && PlaceCountry is null && PlaceProvince is null && PlaceCity is null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (FolderPath is not null)
        {
            parts.Add(Path.GetFileName(FolderPath.TrimEnd('\\', '/')));
        }
        if (CameraModel is not null)
        {
            parts.Add(CameraModel);
        }
        if (RatingMin is { } r && r > 0)
        {
            parts.Add($"{r}★+");
        }
        if (TagName is not null)
        {
            parts.Add($"#{TagName}");
        }
        if (SearchText is not null)
        {
            parts.Add($"“{SearchText}”");
        }
        var placeParts = new[] { PlaceCountry, PlaceProvince, PlaceCity }.Where(s => !string.IsNullOrWhiteSpace(s));
        if (placeParts.Any())
        {
            parts.Add(string.Join(" · ", placeParts));
        }
        if (DateFrom is not null && DateTo is not null && DateFrom == DateTo)
        {
            parts.Add(DateFrom);
        }
        else if (DateFrom is not null || DateTo is not null)
        {
            parts.Add($"{DateFrom ?? "…"} ~ {DateTo ?? "…"}");
        }
        return parts.Count == 0 ? "全部照片" : string.Join(" · ", parts);
    }
}
