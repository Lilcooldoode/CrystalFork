using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using C = ClientPackets;

public sealed partial class GameClient
{
    private string? _debugRecipient;
    private bool _debugActive;

    internal void Log(string message)
    {
        if (_logger is SummaryAgentLogger summary && !summary.ShouldLog(PlayerName))
            return;
        Console.WriteLine(message);
        if (_debugActive && !string.IsNullOrEmpty(_debugRecipient))
            FireAndForget(SendWhisperAsync(_debugRecipient, message));
    }

    internal void LogError(string message)
    {
        string location = $"{CurrentMapName} ({CurrentLocation.X}, {CurrentLocation.Y})";
        string fullMessage = $"{PlayerName} at {location}: {message}";
        try
        {
            File.AppendAllText("Error.txt", fullMessage + Environment.NewLine);
        }
        catch
        {
        }
        Log(fullMessage);
    }

    internal Task SendWhisperAsync(string target, string message)
    {
        if (_stream == null) return Task.CompletedTask;
        return SendChatAsync($"/{target} {message}");
    }

    private void HandleDebugCommand(string text)
    {
        var parts = text.Split(new[] {"=>"}, 2, StringSplitOptions.None);
        if (parts.Length != 2) return;
        string sender = parts[0];
        string msg = parts[1].Trim();

        if (msg.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            StartDebug(sender);
        }
        else if (msg.Equals("enddebug", StringComparison.OrdinalIgnoreCase))
        {
            StopDebug(sender);
        }
        else if (msg.Equals("focus", StringComparison.OrdinalIgnoreCase))
        {
            if (_logger is SummaryAgentLogger summary)
                summary.FocusAgent(PlayerName);
        }
        else if (msg.Equals("endfocus", StringComparison.OrdinalIgnoreCase))
        {
            if (_logger is SummaryAgentLogger summary)
                summary.EndFocus();
        }
        else if (msg.Equals("sort", StringComparison.OrdinalIgnoreCase))
        {
            if (_logger is SummaryAgentLogger summary)
                summary.SortByCycleTime();
        }
        else if (msg.Equals("inventory", StringComparison.OrdinalIgnoreCase))
        {
            FireAndForget(SendInventoryAsync(sender));
        }
        else if (msg.Equals("lastaction", StringComparison.OrdinalIgnoreCase))
        {
            FireAndForget(SendWhisperAsync(sender, LastStorageAction));
        }
        else if (msg.Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            var points = CurrentPathPoints;
            string response = (points == null || points.Count == 0)
                ? "No path"
                : string.Join(" -> ", points.Select(p => $"{p.X},{p.Y}"));
            FireAndForget(SendWhisperAsync(sender, response));
        }
        else if (msg.Equals("tame", StringComparison.OrdinalIgnoreCase))
        {
            WhisperCommandReceived?.Invoke("tame");
        }
        else if (msg.Equals("sell", StringComparison.OrdinalIgnoreCase))
        {
            WhisperCommandReceived?.Invoke("sell");
        }
        else if (msg.Equals("isolate", StringComparison.OrdinalIgnoreCase))
        {
            IsolateCommandReceived?.Invoke();
        }
        else if (msg.StartsWith("bestmap", StringComparison.OrdinalIgnoreCase))
        {
            WhisperCommandReceived?.Invoke(msg);
        }
        else if (msg.StartsWith("item ", StringComparison.OrdinalIgnoreCase))
        {
            var parts2 = msg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts2.Length == 2 && int.TryParse(parts2[1], out var index))
            {
                if (ItemInfoDict.TryGetValue(index, out var info))
                {
                    FireAndForget(SendWhisperAsync(sender,
                        $"Item {index} known: {info.FriendlyName}"));
                }
                else
                {
                    FireAndForget(SendWhisperAsync(sender,
                        $"Item {index} unknown"));
                }
            }
        }
    }

    private void StartDebug(string sender)
    {
        if (_debugActive) return;
        _debugRecipient = sender;
        _debugActive = true;
    }

    private void StopDebug(string sender)
    {
        if (!_debugActive || _debugRecipient != sender) return;
        _debugActive = false;
        _debugRecipient = null;
    }

    private async Task SendInventoryAsync(string target)
    {
        if (_inventory == null) return;

        var groups = _inventory
            .Where(i => i != null && i.Info != null)
            .GroupBy(i => i!.Info!.FriendlyName)
            .Select(g => new { Name = g.Key, Count = g.Sum(i => (int)i!.Count) })
            .OrderBy(g => g.Name);

        foreach (var g in groups)
        {
            await SendWhisperAsync(target, $"{g.Name} x{g.Count}");
            await Task.Delay(500);
        }
    }
}
