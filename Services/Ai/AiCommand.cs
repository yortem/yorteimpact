using System.Text.Json;

namespace YorteImpact.Services.Ai;

/// <summary>One validated JSON command returned by the model.</summary>
public sealed class AiCommand
{
    public string Op { get; init; } = "";
    public JsonElement Target { get; init; }
    public Dictionary<string, JsonElement> Props { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool Confirm { get; init; }
}

/// <summary>
/// The model's answer: a short Hebrew summary plus the command batch. Parsing
/// is tolerant of markdown fences and surrounding prose, but the JSON itself
/// must be well formed; a malformed batch never touches the document.
/// </summary>
public sealed class AiResponse
{
    public string Summary { get; init; } = "";
    public List<AiCommand> Ops { get; } = new();
    public List<string> Errors { get; } = new();

    public static bool TryParse(string? raw, out AiResponse response, out string error)
    {
        response = new AiResponse();
        string json = ExtractFirstJsonObject(raw ?? "");
        if (json.Length == 0)
        {
            error = "המודל לא החזיר JSON.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "תשובת המודל אינה אובייקט JSON.";
                return false;
            }

            string summary = ReadString(root, "summary") ?? "";
            response = new AiResponse { Summary = summary };

            if (!root.TryGetProperty("ops", out JsonElement ops) || ops.ValueKind != JsonValueKind.Array)
            {
                error = "לתשובה אין מערך ops.";
                return false;
            }

            int index = 0;
            foreach (JsonElement item in ops.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    response.Errors.Add($"פקודה {index}: אינה אובייקט.");
                    continue;
                }

                string op = ReadString(item, "op")?.Trim() ?? "";
                if (op.Length == 0)
                {
                    response.Errors.Add($"פקודה {index}: חסר שדה op.");
                    continue;
                }

                var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("props", out JsonElement propsNode))
                {
                    if (propsNode.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in propsNode.EnumerateObject())
                            props[property.Name] = property.Value.Clone();
                    }
                    else if (propsNode.ValueKind != JsonValueKind.Null)
                    {
                        response.Errors.Add($"פקודה {index} ({op}): props חייב להיות אובייקט.");
                        continue;
                    }
                }

                JsonElement target = item.TryGetProperty("target", out JsonElement targetNode)
                    ? targetNode.Clone()
                    : default;

                bool confirm = item.TryGetProperty("confirm", out JsonElement confirmNode)
                               && confirmNode.ValueKind == JsonValueKind.True;

                response.Ops.Add(new AiCommand
                {
                    Op = op,
                    Target = target,
                    Props = props,
                    Confirm = confirm,
                });
            }

            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            error = "ה-JSON שחזר אינו תקין: " + ex.Message;
            return false;
        }
    }

    private static string? ReadString(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Extract the first balanced top-level JSON object, ignoring code fences and prose.</summary>
    public static string ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return "";

        bool inString = false;
        bool escaped = false;
        int depth = 0;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }

        return "";
    }
}
