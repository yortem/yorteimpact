using System.Collections.ObjectModel;
using System.Windows;
using YorteImpact.Models;
using YorteImpact.ViewModels;

namespace YorteImpact.Services.Ai;

public enum AiMessageRole
{
    User,
    Assistant,
    Error,
    Info,
    Thinking,
}

/// <summary>One bubble in the per-tab chat.</summary>
public sealed class AiChatMessage
{
    public AiMessageRole Role { get; init; }
    public string Text { get; init; } = "";

    public bool IsUser => Role == AiMessageRole.User;
    public bool IsError => Role == AiMessageRole.Error;
    public bool IsInfo => Role == AiMessageRole.Info;

    public HorizontalAlignment Alignment =>
        IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
}

/// <summary>
/// Chat state for one editor tab (memory only). Sends the canvas brief and a
/// PNG to the configured provider, validates the returned command batch and
/// applies it as one undo step. With two-pass refinement enabled, the first
/// pass creates or changes the layout and a second pass reviews the rendered
/// result and fixes what is still wrong.
/// </summary>
public sealed class AiChatService
{
    private const int HistoryTurnLimit = 20;
    private const int HistoryPreviewLength = 120;

    private readonly AiClient _client = new();
    private CancellationTokenSource? _cancellation;
    private AiChatMessage? _thinking;

    public ObservableCollection<AiChatMessage> Messages { get; } = new();
    public bool IsBusy { get; private set; }

    public event Action? Changed;

    public void AddInfo(string text) =>
        Add(new AiChatMessage { Role = AiMessageRole.Info, Text = text });

    public void AddError(string text) =>
        Add(new AiChatMessage { Role = AiMessageRole.Error, Text = text });

    public void Clear()
    {
        _thinking = null;
        Messages.Clear();
        Notify();
    }

    /// <summary>Cancel an in-flight request (used when the tab loses focus).</summary>
    public void Cancel() => _cancellation?.Cancel();

    public async Task SendAsync(
        DesignDocument document,
        string prompt,
        AiProviderSettings settings,
        string? initialPngBase64,
        Func<string?> captureCanvasPng,
        Func<AiDeleteRequest, Task<bool>> confirmDelete,
        Action pushUndo,
        Func<string, bool> isDocumentCurrent,
        Action<AiApplyReport> afterApply)
    {
        if (IsBusy) return;
        prompt = (prompt ?? "").Trim();
        if (prompt.Length == 0) return;

        var userMessage = new AiChatMessage { Role = AiMessageRole.User, Text = prompt };
        Add(userMessage);

        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        SetThinking("המודל חושב… נא להמתין.");

        bool undoPushed = false;
        void PushOnce()
        {
            if (undoPushed) return;
            pushUndo();
            undoPushed = true;
        }

        try
        {
            var (canvasJson, map) = AiCanvasBrief.Build(document);
            string signature = canvasJson;
            var history = BuildHistory(userMessage);

            AiRoundResult first = await RunRoundAsync(
                document, map, canvasJson, prompt, initialPngBase64,
                settings, history, signature, confirmDelete, PushOnce,
                isDocumentCurrent, afterApply, token);

            if (settings.TwoPass && first.Applied > 0)
            {
                SetThinking("משפר את העיצוב…");
                var (nextJson, nextMap) = AiCanvasBrief.Build(document);
                string nextSignature = nextJson;
                string? nextPng = captureCanvasPng();
                string reviewPrompt = AiPromptBuilder.BuildReviewTurn(prompt, first.Summary);
                var reviewHistory = BuildHistory(null);

                await RunRoundAsync(
                    document, nextMap, nextJson, reviewPrompt, nextPng,
                    settings, reviewHistory, nextSignature, confirmDelete, PushOnce,
                    isDocumentCurrent, afterApply, token);
            }
        }
        catch (OperationCanceledException)
        {
            AddInfo(undoPushed
                ? "סבב השיפור בוטל; השינויים מהשלב הראשון נשמרו."
                : "הבקשה בוטלה.");
        }
        catch (AiClientException ex)
        {
            AddError(ex.Message);
        }
        catch (Exception ex)
        {
            AddError("שגיאה לא צפויה: " + ex.Message);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            if (_thinking != null)
            {
                Messages.Remove(_thinking);
                _thinking = null;
            }
            IsBusy = false;
            Notify();
        }
    }

    private async Task<AiRoundResult> RunRoundAsync(
        DesignDocument document,
        IReadOnlyDictionary<string, DesignElement> map,
        string canvasJson,
        string prompt,
        string? pngBase64,
        AiProviderSettings settings,
        IReadOnlyList<AiConversationTurn> history,
        string signature,
        Func<AiDeleteRequest, Task<bool>> confirmDelete,
        Action pushUndo,
        Func<string, bool> isDocumentCurrent,
        Action<AiApplyReport> afterApply,
        CancellationToken token)
    {
        string raw = await _client.CompleteAsync(
            settings, history, prompt, canvasJson, pngBase64, token);
        token.ThrowIfCancellationRequested();

        if (!isDocumentCurrent(signature))
        {
            AddError("המסמך השתנה בזמן שהמתנתי לתשובה, ולכן השינויים מהתשובה הזו לא הוחלו.");
            return new AiRoundResult();
        }

        if (!AiResponse.TryParse(raw, out AiResponse response, out string parseError))
        {
            AddError(parseError);
            return new AiRoundResult();
        }

        AiValidationResult validation = AiCommandApplier.Validate(response, document, map);

        var problems = new List<string>();
        problems.AddRange(response.Errors);
        problems.AddRange(validation.Errors);
        if (problems.Count > 0)
        {
            Add(new AiChatMessage
            {
                Role = AiMessageRole.Error,
                Text = "פקודות שלא בוצעו:\n" + string.Join("\n", problems),
            });
        }

        var commands = validation.Commands.ToList();
        bool cancelledDeletes = false;

        if (commands.Any(c => c.IsDestructive))
        {
            var destructive = commands.Where(c => c.IsDestructive).ToList();
            var destructiveTargets = destructive.SelectMany(c => c.Targets).ToList();
            if (destructive.Any(c => c.Command.Op == "clear"))
            {
                destructiveTargets = document.Elements
                    .Where(e => !e.IsBackgroundLayer)
                    .ToList();
            }

            var deleteRequest = new AiDeleteRequest
            {
                Ids = destructiveTargets
                    .Select(t => map.FirstOrDefault(pair => ReferenceEquals(pair.Value, t)).Key ?? "?")
                    .Distinct()
                    .ToList(),
                Labels = destructiveTargets
                    .Select(t => AiCanvasBrief.LabelOf(document, t))
                    .Distinct()
                    .ToList(),
            };

            bool approved = await confirmDelete(deleteRequest);
            token.ThrowIfCancellationRequested();

            if (!approved)
            {
                commands.RemoveAll(c => c.IsDestructive);
                cancelledDeletes = true;
            }

            if (!isDocumentCurrent(signature))
            {
                AddError("המסמך השתנה בזמן אישור המחיקה, ולכן השינויים מהתשובה הזו לא הוחלו.");
                return new AiRoundResult();
            }
        }

        AiApplyReport report = commands.Count > 0
            ? AiCommandApplier.Apply(document, commands, map, pushUndo)
            : new AiApplyReport();

        afterApply(report);

        var summary = new List<string>();
        if (!string.IsNullOrWhiteSpace(response.Summary))
            summary.Add(response.Summary.Trim());
        else if (report.Applied.Count > 0)
            summary.Add("העיצוב עודכן.");
        else
            summary.Add("לא בוצעו שינויים.");

        if (cancelledDeletes)
            summary.Add(commands.Count > 0
                ? "המחיקה בוטלה; שאר השינויים הוחלו."
                : "המחיקה בוטלה.");

        if (report.Errors.Count > 0)
            summary.Add("חלק מהפקודות לא בוצעו.");

        string text = string.Join(" ", summary).Trim();
        Add(new AiChatMessage
        {
            Role = AiMessageRole.Assistant,
            Text = text,
        });

        return new AiRoundResult { Applied = report.Applied.Count, Summary = text };
    }

    private void SetThinking(string text)
    {
        if (_thinking != null)
            Messages.Remove(_thinking);

        _thinking = new AiChatMessage { Role = AiMessageRole.Thinking, Text = text };
        Messages.Add(_thinking);
        Notify();
    }

    private List<AiConversationTurn> BuildHistory(AiChatMessage? current)
    {
        var turns = new List<AiConversationTurn>();
        foreach (var message in Messages)
        {
            if (current != null && ReferenceEquals(message, current)) break;
            if (message.Role is not (AiMessageRole.User or AiMessageRole.Assistant)) continue;
            turns.Add(new AiConversationTurn
            {
                Role = message.IsUser ? "user" : "assistant",
                Text = Truncate(message.Text, HistoryPreviewLength),
            });
        }

        if (turns.Count > HistoryTurnLimit)
            turns = turns.TakeLast(HistoryTurnLimit).ToList();
        return turns;
    }

    private void Add(AiChatMessage message)
    {
        Messages.Add(message);
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    private static string Truncate(string text, int max)
    {
        string single = (text ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    private sealed class AiRoundResult
    {
        public int Applied { get; init; }
        public string Summary { get; init; } = "";
    }
}
