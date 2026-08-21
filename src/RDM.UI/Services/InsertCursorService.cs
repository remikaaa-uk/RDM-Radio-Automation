namespace RDM.UI.Services;

public interface IInsertCursorService
{
    int?  VisibleIndex { get; }
    bool  IsActive     { get; }
    void  SetIndex(int visibleIndex);
    void  Advance();
    void  Clear();
}

public sealed class InsertCursorService : IInsertCursorService
{
    public int?  VisibleIndex { get; private set; }
    public bool  IsActive     => VisibleIndex.HasValue;

    public void SetIndex(int visibleIndex) => VisibleIndex = visibleIndex;
    public void Advance()                  => VisibleIndex = VisibleIndex.HasValue ? VisibleIndex.Value + 1 : null;
    public void Clear()                    => VisibleIndex = null;
}
