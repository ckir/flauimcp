namespace FlaUI.Mcp.Core.Perception;

/// <summary>Bounded store of recent snapshot models keyed by snapshotId. Cached SnapshotModels are
/// immutable records; eviction is pure LRU (a diff/stats requesting an evicted baseline gets a clean
/// SnapshotNotFound). Separate from RefRegistry — BeginSnapshot never touches this cache.</summary>
public sealed class SnapshotCache
{
    private const int Capacity = 32;
    private readonly object _gate = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, SnapshotModel> _byId = new();
    public void Put(string snapshotId, SnapshotModel model)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(snapshotId)) _order.Remove(snapshotId);
            _byId[snapshotId] = model; _order.AddFirst(snapshotId);
            while (_order.Count > Capacity) { var e = _order.Last!.Value; _order.RemoveLast(); _byId.Remove(e); }
        }
    }
    public bool TryGet(string snapshotId, out SnapshotModel? model)
    { lock (_gate) { return _byId.TryGetValue(snapshotId, out model); } }
}
