using System.Collections.Concurrent;

namespace FlaUI.Mcp.Core.Session;

/// <summary>Per-connection lifecycle. Frees handles on termination always; kills
/// server-owned spawned processes only when policy allows (default on for stdio,
/// off for shared HTTP — auto-killing a user's app under a shared server is destructive).</summary>
public sealed class SessionManager
{
    private readonly bool _killSpawnedOnDisconnect;
    private readonly Action<int> _killProcess;
    private readonly Action<string> _freeHandle;
    private readonly ConcurrentDictionary<string, byte> _handles = new();
    private readonly ConcurrentDictionary<string, int> _ownedProcesses = new();

    public SessionManager(bool killSpawnedOnDisconnect, Action<int> killProcess, Action<string> freeHandle)
    {
        _killSpawnedOnDisconnect = killSpawnedOnDisconnect;
        _killProcess = killProcess;
        _freeHandle = freeHandle;
    }

    public void TrackHandle(string handleId) => _handles[handleId] = 0;
    public void TrackOwnedProcess(string handleId, int pid) => _ownedProcesses[handleId] = pid;

    public void Terminate()
    {
        foreach (var id in _handles.Keys) _freeHandle(id);
        if (_killSpawnedOnDisconnect)
            foreach (var pid in _ownedProcesses.Values)
                try { _killProcess(pid); } catch { }
        _handles.Clear();
        _ownedProcesses.Clear();
    }
}
