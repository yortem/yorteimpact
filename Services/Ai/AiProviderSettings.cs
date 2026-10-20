namespace YorteImpact.Services.Ai;

/// <summary>
/// Connection settings for the AI canvas assistant. Persisted in
/// <c>settings.json</c>; the API key is encrypted on save (see
/// <see cref="AiKeyProtector"/>).
/// </summary>
public sealed class AiProviderSettings
{
    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com";
    public const string GeminiModel = "gemini-2.5-flash";
    public const string OpenAiBaseUrl = "https://api.deepseek.com";
    public const string OpenAiModel = "deepseek-chat";

    public AiProviderKind Provider { get; set; } = AiProviderKind.Gemini;
    public string BaseUrl { get; set; } = GeminiBaseUrl;
    public string Model { get; set; } = GeminiModel;
    public string ApiKey { get; set; } = "";
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Run a second review pass after a successful first pass.</summary>
    public bool TwoPass { get; set; } = true;

    public string EffectiveBaseUrl => (BaseUrl ?? "").Trim().TrimEnd('/');

    public static AiProviderSettings CreateDefault(AiProviderKind provider) => provider == AiProviderKind.Gemini
        ? new AiProviderSettings()
        : new AiProviderSettings
        {
            Provider = AiProviderKind.OpenAiCompatible,
            BaseUrl = OpenAiBaseUrl,
            Model = OpenAiModel,
        };

    /// <summary>Fill empty values and keep the numeric settings in sane ranges.</summary>
    public void Normalize()
    {
        var defaults = CreateDefault(Provider);
        if (string.IsNullOrWhiteSpace(BaseUrl)) BaseUrl = defaults.BaseUrl;
        if (string.IsNullOrWhiteSpace(Model)) Model = defaults.Model;
        if (!double.IsFinite(Temperature)) Temperature = 0.2;
        Temperature = Math.Clamp(Temperature, 0, 2);
        MaxTokens = Math.Clamp(MaxTokens, 256, 32768);
    }

    public AiProviderSettings Clone() => new()
    {
        Provider = Provider,
        BaseUrl = BaseUrl,
        Model = Model,
        ApiKey = ApiKey,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        TwoPass = TwoPass,
    };
}
