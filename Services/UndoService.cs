using YorteImpact.Models;
using YorteImpact.ViewModels;

namespace YorteImpact.Services;

/// <summary>
/// Snapshot undo/redo: captures the whole document (via the project DTO, so
/// images round-trip losslessly) before each mutation. Drag gestures push one
/// snapshot at gesture start; typing pushes once per focus session.
/// Bounded (default 40 steps) to cap memory with big images.
/// </summary>
public sealed class UndoService
{
    private sealed record Entry(YorteProjectService.ProjectFile State, List<Guid> SelectedIds);

    private readonly LinkedList<Entry> _undo = new();
    private readonly LinkedList<Entry> _redo = new();

    public int Limit { get; set; } = 40;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Record pre-mutation state. Clears the redo stack.</summary>
    public void Capture(DesignDocument doc)
    {
        _undo.AddFirst(new Entry(YorteProjectService.Capture(doc),
            doc.SelectedElements.Select(e => e.Id).ToList()));
        while (_undo.Count > Limit) _undo.RemoveLast();
        _redo.Clear();
    }

    public bool Undo(DesignDocument doc)
    {
        if (_undo.First == null) return false;
        _redo.AddFirst(Current(doc));
        var e = _undo.First.Value;
        _undo.RemoveFirst();
        Apply(doc, e);
        return true;
    }

    public bool Redo(DesignDocument doc)
    {
        if (_redo.First == null) return false;
        _undo.AddFirst(Current(doc));
        var e = _redo.First.Value;
        _redo.RemoveFirst();
        Apply(doc, e);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static Entry Current(DesignDocument doc) =>
        new(YorteProjectService.Capture(doc),
            doc.SelectedElements.Select(e => e.Id).ToList());

    private static void Apply(DesignDocument doc, Entry e)
    {
        YorteProjectService.ApplyTo(e.State, doc);
        var matches = e.SelectedIds
            .Select(id => doc.Elements.FirstOrDefault(x => x.Id == id))
            .Where(x => x != null)
            .Cast<DesignElement>()
            .ToList();
        if (matches.Count > 0) doc.SelectRange(matches);
        else doc.Select(null);
    }
}
