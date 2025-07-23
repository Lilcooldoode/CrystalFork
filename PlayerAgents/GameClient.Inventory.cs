using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shared;

public sealed partial class GameClient
{
    public bool TryFindNearestNpc(ItemType type, out uint id, out Point location, out NpcEntry? entry)
    {
        id = 0;
        location = default;
        entry = null;
        if (string.IsNullOrEmpty(_currentMapFile))
            return false;

        int bestDist = int.MaxValue;
        string map = Path.GetFileNameWithoutExtension(_currentMapFile);

        foreach (var e in _npcMemory.GetAll())
        {
            if (e.MapFile != map) continue;
            bool knows = e.SellItemTypes != null && e.SellItemTypes.Contains(type);
            bool unknown = e.SellItemTypes == null && e.CannotSellItemTypes == null && e.CanSell;
            if (!knows && !unknown) continue;

            int dist = Functions.MaxDistance(_currentLocation, new Point(e.X, e.Y));
            if (dist < bestDist)
            {
                bestDist = dist;
                entry = e;
                location = new Point(e.X, e.Y);
            }
        }

        if (entry != null)
        {
            foreach (var kv in _npcEntries)
            {
                if (kv.Value == entry)
                {
                    id = kv.Key;
                    break;
                }
            }
        }

        return entry != null;
    }

    public async Task SellItemsToNpcAsync(uint npcId, IReadOnlyList<UserItem> items)
    {
        if (!_npcEntries.TryGetValue(npcId, out var entry)) return;
        var interaction = new NPCInteraction(this, npcId);
        var page = await interaction.BeginAsync();
        string[] sellKeys = { "@BUYSELLNEW", "@BUYSELL", "@SELL" };
        var sellKey = page.Buttons.Select(b => b.Key).FirstOrDefault(k => sellKeys.Contains(k.ToUpper())) ?? "@SELL";
        if (sellKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
            return;
        using (var cts = new System.Threading.CancellationTokenSource(2000))
        {
            var waitTask = WaitForLatestNpcResponseAsync(cts.Token);
            await interaction.SelectFromMainAsync(sellKey);
            try
            {
                await waitTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        foreach (var item in items)
        {
            if (item.Info == null) continue;
            _pendingSellChecks[item.UniqueID] = (entry, item.Info.Type);
            var count = item.Count;
            await SellItemAsync(item.UniqueID, count);
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(2000);
                await WaitForLatestNpcResponseAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            await Task.Delay(100);
        }
    }
}
