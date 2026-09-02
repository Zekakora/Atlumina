namespace MyAlbum.Core.Services;

/// <summary>
/// App-wide LLM configuration (model / API key / base URL). The App mirrors the persisted
/// settings into this static so Core services (e.g. address normalization) can use it
/// without referencing the UI layer. The API key is stored in the user's settings.json,
/// never in source.
/// </summary>
public static class LlmConfig
{
    public static string Model { get; private set; } = "deepseek-chat";
    public static string ApiKey { get; private set; } = "";
    public static string BaseUrl { get; private set; } = "https://api.deepseek.com";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);

    public static void Set(string model, string apiKey, string baseUrl)
    {
        Model = string.IsNullOrWhiteSpace(model) ? "deepseek-v4-flash" : model.Trim();
        ApiKey = (apiKey ?? "").Trim();
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.deepseek.com" : baseUrl.Trim().TrimEnd('/');
    }
}
