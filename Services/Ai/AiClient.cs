using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace YorteImpact.Services.Ai;

/// <summary>One earlier chat turn sent to the model (assistant or user).</summary>
public sealed class AiConversationTurn
{
    public string Role { get; init; } = "user";
    public string Text { get; init; } = "";
}

public sealed class AiClientException : Exception
{
    public AiClientException(string message) : base(message) { }
    public AiClientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// HTTP transport for the canvas assistant. Gemini and OpenAI-compatible
/// endpoints share the same turn structure; only the envelope and the auth
/// header differ. Keys are never written to logs or error messages.
/// </summary>
public sealed class AiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public async Task<string> CompleteAsync(
        AiProviderSettings settings,
        IReadOnlyList<AiConversationTurn> history,
        string prompt,
        string canvasJson,
        string? pngBase64,
        CancellationToken cancellationToken,
        bool fastProbe = false)
    {
        settings.Normalize();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new AiClientException("לא הוגדר מפתח API. הוסף מפתח בהגדרות → AI.");
        if (!Uri.TryCreate(settings.EffectiveBaseUrl, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            throw new AiClientException("כתובת ה-Base URL אינה תקינה. השתמש בכתובת http/https מלאה.");

        return settings.Provider == AiProviderKind.Gemini
            ? await CompleteGeminiAsync(settings, history, prompt, canvasJson, pngBase64,
                cancellationToken, fastProbe)
            : await CompleteOpenAiAsync(settings, history, prompt, canvasJson, pngBase64, cancellationToken);
    }

    public async Task<(bool Ok, string Message)> TestAsync(
        AiProviderSettings settings, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var probe = settings.Clone();
            probe.MaxTokens = Math.Min(probe.MaxTokens, 64);
            probe.Temperature = 0;
            await CompleteAsync(probe, Array.Empty<AiConversationTurn>(),
                "Reply with the JSON {\"ok\":true} and nothing else.",
                "{}", null, timeout.Token, fastProbe: true);
            return (true, "Connection OK — the model answered.");
        }
        catch (AiClientException ex)
        {
            return (false, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "The connection test timed out after 30 seconds. Check the base URL, model and network.");
        }
        catch (OperationCanceledException)
        {
            return (false, "The test was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, "Connection failed: " + ex.Message);
        }
    }

    private static async Task<string> CompleteGeminiAsync(
        AiProviderSettings settings, IReadOnlyList<AiConversationTurn> history,
        string prompt, string canvasJson, string? pngBase64, CancellationToken cancellationToken,
        bool fastProbe)
    {
        string url = BuildGeminiUrl(settings);
        string response = await SendWithRetryAsync(
            url,
            jsonMode => BuildGeminiBody(settings, history, prompt, canvasJson, pngBase64, jsonMode, fastProbe),
            request => request.Headers.TryAddWithoutValidation("x-goog-api-key", settings.ApiKey),
            cancellationToken);

        return ExtractGeminiText(response);
    }

    private static async Task<string> CompleteOpenAiAsync(
        AiProviderSettings settings, IReadOnlyList<AiConversationTurn> history,
        string prompt, string canvasJson, string? pngBase64, CancellationToken cancellationToken)
    {
        string url = BuildOpenAiUrl(settings);
        string response = await SendWithRetryAsync(
            url,
            jsonMode => BuildOpenAiBody(settings, history, prompt, canvasJson, pngBase64, jsonMode),
            request => request.Headers.TryAddWithoutValidation(
                "Authorization", "Bearer " + settings.ApiKey),
            cancellationToken);

        return ExtractOpenAiText(response);
    }

    private static async Task<string> SendWithRetryAsync(
        string url,
        Func<bool, string> buildBody,
        Action<HttpRequestMessage> configureRequest,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(buildBody(attempt == 0), Encoding.UTF8, "application/json"),
            };
            configureRequest(request);

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiClientException("הבקשה לספק ארכה יותר מדי (timeout). נסה שוב.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new AiClientException(
                    "לא ניתן להתחבר לספק. בדוק את החיבור לאינטרנט ואת כתובת ה-Base URL.", ex);
            }

            using (response)
            {
                string text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return text;

                // Some models/models versions reject the strict JSON flags.
                // Retry once without them and let the parser handle the text.
                if (attempt == 0 && response.StatusCode == HttpStatusCode.BadRequest)
                    continue;

                throw CreateHttpError(response.StatusCode, text);
            }
        }

        throw new AiClientException("הבקשה לספק נכשלה.");
    }

    private static string BuildGeminiBody(
        AiProviderSettings settings, IReadOnlyList<AiConversationTurn> history,
        string prompt, string canvasJson, string? pngBase64, bool jsonMode, bool fastProbe)
    {
        var contents = new JsonArray();
        foreach (var turn in history)
        {
            contents.Add(new JsonObject
            {
                ["role"] = turn.Role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = turn.Text }),
            });
        }

        var parts = new JsonArray
        {
            new JsonObject { ["text"] = AiPromptBuilder.BuildUserTurn(prompt, canvasJson, pngBase64 != null) },
        };
        if (!string.IsNullOrEmpty(pngBase64))
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = "image/png",
                    ["data"] = pngBase64,
                },
            });
        }

        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });

        var generationConfig = new JsonObject
        {
            ["temperature"] = settings.Temperature,
            ["maxOutputTokens"] = settings.MaxTokens,
        };
        if (jsonMode) generationConfig["response_mime_type"] = "application/json";
        // Gemini 2.5 Flash spends output tokens on thinking. For the short
        // connection probe that can consume the whole 64-token budget.
        if (fastProbe
            && settings.Model.Contains("2.5", StringComparison.OrdinalIgnoreCase)
            && settings.Model.Contains("flash", StringComparison.OrdinalIgnoreCase))
        {
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = AiPromptBuilder.BuildSystemPrompt() }),
            },
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };
        return body.ToJsonString(JsonOptions);
    }

    private static string BuildOpenAiBody(
        AiProviderSettings settings, IReadOnlyList<AiConversationTurn> history,
        string prompt, string canvasJson, string? pngBase64, bool jsonMode)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = AiPromptBuilder.BuildSystemPrompt() },
        };

        foreach (var turn in history)
        {
            messages.Add(new JsonObject
            {
                ["role"] = turn.Role == "assistant" ? "assistant" : "user",
                ["content"] = turn.Text,
            });
        }

        var text = AiPromptBuilder.BuildUserTurn(prompt, canvasJson, pngBase64 != null);
        JsonNode content;
        if (string.IsNullOrEmpty(pngBase64))
        {
            content = JsonValue.Create(text);
        }
        else
        {
            var parts = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = "data:image/png;base64," + pngBase64,
                    },
                },
            };
            content = parts;
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });

        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["temperature"] = settings.Temperature,
            ["max_tokens"] = settings.MaxTokens,
        };
        if (jsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body.ToJsonString(JsonOptions);
    }

    private static string ExtractGeminiText(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("promptFeedback", out JsonElement feedback)
            && feedback.TryGetProperty("blockReason", out JsonElement blocked)
            && blocked.ValueKind == JsonValueKind.String)
            throw new AiClientException("הבקשה נחסמה על ידי הספק: " + blocked.GetString());

        if (!root.TryGetProperty("candidates", out JsonElement candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
            throw new AiClientException("הספק לא החזיר תשובה.");

        var text = new StringBuilder();
        foreach (JsonElement candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out JsonElement content)
                || !content.TryGetProperty("parts", out JsonElement parts)
                || parts.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                    text.Append(value.GetString());
            }
        }

        if (text.Length == 0)
            throw new AiClientException("הספק החזיר תשובה ריקה.");
        return text.ToString();
    }

    private static string ExtractOpenAiText(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new AiClientException("הספק לא החזיר תשובה.");

        JsonElement message = choices[0];
        if (message.TryGetProperty("message", out JsonElement messageNode)
            && messageNode.TryGetProperty("content", out JsonElement content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                string value = content.GetString() ?? "";
                if (value.Length > 0) return value;
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var text = new StringBuilder();
                foreach (JsonElement part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out JsonElement value)
                        && value.ValueKind == JsonValueKind.String)
                        text.Append(value.GetString());
                }
                if (text.Length > 0) return text.ToString();
            }
        }

        throw new AiClientException("הספק החזיר תשובה ריקה.");
    }

    private static AiClientException CreateHttpError(HttpStatusCode status, string body)
    {
        string detail = ExtractErrorDetail(body);
        string message = status switch
        {
            HttpStatusCode.Unauthorized =>
                "מפתח ה-API שגוי או חסר הרשאה (401). בדוק את המפתח בהגדרות → AI.",
            HttpStatusCode.Forbidden =>
                "הספק דחה את הבקשה (403). בדוק את המפתח והמודל.",
            HttpStatusCode.NotFound =>
                "המודל או הכתובת לא נמצאו (404). בדוק את Base URL ואת Model.",
            HttpStatusCode.TooManyRequests =>
                "הגעת למגבלת הקצב של הספק (429). נסה שוב בעוד רגע.",
            HttpStatusCode.BadRequest =>
                "הבקשה נדחתה על ידי הספק (400). בדוק את שם המודל והגדרות החשבון.",
            _ => (int)status >= 500
                ? "שגיאה זמנית אצל הספק (" + (int)status + "). נסה שוב מאוחר יותר."
                : "הספק החזיר שגיאה (" + (int)status + ").",
        };

        if (detail.Length > 0) message += " " + detail;
        return new AiClientException(message);
    }

    private static string ExtractErrorDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("error", out JsonElement error)) return "";

            string detail = error.ValueKind switch
            {
                JsonValueKind.String => error.GetString() ?? "",
                JsonValueKind.Object when error.TryGetProperty("message", out JsonElement message)
                                          && message.ValueKind == JsonValueKind.String
                    => message.GetString() ?? "",
                _ => "",
            };

            detail = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return detail.Length <= 240 ? detail : detail[..240] + "…";
        }
        catch
        {
            return "";
        }
    }

    private static string BuildGeminiUrl(AiProviderSettings settings)
    {
        string baseUrl = settings.EffectiveBaseUrl;
        string prefix = baseUrl.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
            ? ""
            : "/v1beta";
        return $"{baseUrl}{prefix}/models/{Uri.EscapeDataString(settings.Model)}:generateContent";
    }

    private static string BuildOpenAiUrl(AiProviderSettings settings)
    {
        string baseUrl = settings.EffectiveBaseUrl;
        string path = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "/chat/completions"
            : "/v1/chat/completions";
        return baseUrl + path;
    }
}
