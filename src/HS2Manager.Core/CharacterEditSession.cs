namespace HS2Manager.Core;

public sealed record CharacterEditSnapshot(CharacterDocument Document, long Revision);

/// <summary>One draft, with structural sharing and gesture-sized undo transactions.</summary>
public sealed class CharacterEditSession
{
    private readonly Stack<CharacterDocument> undo = new(), redo = new();
    private CharacterDocument? gestureStart;
    public CharacterDocument Document { get; private set; }
    public CharacterDocument Baseline { get; private set; }
    public CardRecord Card { get; private set; }
    public long Revision { get; private set; }
    public bool IsDirty => !Document.ContentEquals(Baseline);
    public bool CanUndo => undo.Count > 0 || gestureStart != null && !Document.ContentEquals(gestureStart);
    public bool CanRedo => redo.Count > 0;
    public event Action? Changed;

    public CharacterEditSession(CardRecord card, CharacterDocument document)
    { Card = card; Document = Baseline = document; }
    public CharacterEditSnapshot Snapshot() => new(Document, Revision);
    public void BeginGesture() { EndGesture(); gestureStart = Document; }
    public void EndGesture()
    {
        if (gestureStart == null) return;
        if (!Document.ContentEquals(gestureStart)) { undo.Push(gestureStart); redo.Clear(); }
        gestureStart = null;
    }
    public void SetValue(string path, object? value) => Apply(CharacterEditRules.SetValue(Document, path, value));
    public void SetColor(string path, IReadOnlyList<float> value) => Apply(CharacterEditRules.Color(Document, path, value));
    public void Apply(CharacterDocument next)
    {
        if (next.SourceHash != Document.SourceHash) throw new InvalidOperationException("不能将其他人物卡的草稿应用到当前编辑会话。");
        if (Document.ContentEquals(next)) return;
        if (gestureStart == null) undo.Push(Document);
        redo.Clear(); Document = next; Revision++; Changed?.Invoke();
    }
    public void Restore(string path)
    {
        EndGesture();
        var next = Document.WithNode(path, Baseline.Node(path));
        if(System.Text.RegularExpressions.Regex.IsMatch(path,@"^hair\.parts\[\d+\]\.id$"))
        {
            string root=path[..^3];
            foreach(string field in new[]{"bundleId","dictBundle","acsColorInfo"})
                if(Baseline.Contains(root+"."+field))next=next.WithNode(root+"."+field,Baseline.Node(root+"."+field));
        }
        if(System.Text.RegularExpressions.Regex.IsMatch(path,@"^accessory\.parts\[\d+\]\.(id|type)$"))
        {
            string root=path[..path.LastIndexOf('.')];
            next=next.WithNode(root,Baseline.Node(root));path=root+".id";
        }
        // Resource selectors restore both the value and its resolver entry as one operation.
        next = CharacterAssetReferences.Restore(next, Baseline, path);
        Apply(next);
    }
    public void RestoreCategory(string category)
    {
        EndGesture();
        var roots = category == "makeup" ? new[] { "face.makeup" } : category == "face" ? new[] { "face" } : new[] { category };
        var next = Document;
        foreach (string root in roots) next = CharacterAssetReferences.Restore(next.WithNode(root, Baseline.Node(root)), Baseline, root);
        if(category=="face"&&Document.Contains("face.makeup"))next=next.WithNode("face.makeup",Document.Node("face.makeup"));
        Apply(next);
    }
    public void RestoreAll() {EndGesture();Apply(Baseline);}
    public void Undo()
    { EndGesture(); if (undo.TryPop(out var previous)) { redo.Push(Document); Document = previous; Revision++; Changed?.Invoke(); } }
    public void Redo()
    { EndGesture(); if (redo.TryPop(out var next)) { undo.Push(Document); Document = next; Revision++; Changed?.Invoke(); } }
    public void AcceptSaved(CardRecord saved, CharacterDocument document, long savedRevision)
    {
        if (Revision != savedRevision) throw new InvalidOperationException("保存时仍有新的编辑，请等待编辑完成。");
        gestureStart = null; undo.Clear(); redo.Clear(); Card = saved; Document = Baseline = document; Revision++; Changed?.Invoke();
    }
}
