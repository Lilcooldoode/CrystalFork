using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using C = ClientPackets;
using Shared;

public sealed partial class GameClient
{
    private readonly List<string> _groupMembers = new();
    private bool _allowGroup;
    private string? _groupLeader;

    public IReadOnlyList<string> GroupMembers => _groupMembers;
    public bool IsGrouped => _groupMembers.Count > 0;
    public string? GroupLeader => _groupLeader;
    public bool IsGroupLeader => _groupLeader != null && string.Equals(_groupLeader, PlayerName, StringComparison.OrdinalIgnoreCase);
    public bool AllowGroup => _allowGroup;

    public event Action<string, string>? WhisperReceived;
    public event Action<string?>? GroupLeaderChanged;

    private void UpdateGroupLeader()
    {
        var leader = _groupMembers.Count > 0 ? _groupMembers[0] : null;
        if (_groupLeader != leader)
        {
            _groupLeader = leader;
            GroupLeaderChanged?.Invoke(_groupLeader);
        }
    }

    public async Task SetAllowGroupAsync(bool allow)
    {
        if (_stream == null) return;
        await SendAsync(new C.SwitchGroup { AllowGroup = allow });
        _allowGroup = allow;
    }

    public async Task InviteToGroupAsync(string name)
    {
        if (_stream == null) return;
        await SendAsync(new C.AddMember { Name = name });
    }

    public async Task LeaveGroupAsync()
    {
        if (_stream != null && IsGrouped)
            await SetAllowGroupAsync(false);

        if (_groupMembers.Count > 0)
        {
            _groupMembers.Clear();
            UpdateGroupLeader();

            if (!string.IsNullOrEmpty(_currentMapFile))
                StartMapExpTracking(_currentMapFile);
        }
    }

    public void LeaveGroup()
    {
        FireAndForget(LeaveGroupAsync());
    }

    internal bool IsGroupMember(uint id)
    {
        if (!_trackedObjects.TryGetValue(id, out var obj)) return false;
        return obj.Type == ObjectType.Player &&
               _groupMembers.Exists(n => n.Equals(obj.Name, StringComparison.OrdinalIgnoreCase));
    }
}
