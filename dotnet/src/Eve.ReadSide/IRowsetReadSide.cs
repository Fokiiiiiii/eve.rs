namespace Eve.ReadSide;

/// <summary>Registration and execution surface used by services that expose Rowset Read Side results.</summary>
public interface IRowsetReadSide
{
    void Register(RowsetQuery query);
    Task<RowSet> RowsAsync(string name, object?[]? args = null, CancellationToken cancellationToken = default);
}
