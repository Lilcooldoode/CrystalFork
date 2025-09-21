using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Shared;
using PlayerAgents.Map;

public sealed partial class GameClient
{
    private int GetNpcTravelDistance(NpcEntry entry, int maxDistance = int.MaxValue)
    {
        if (string.IsNullOrEmpty(_currentMapFile))
            return int.MaxValue;

        var destPath = Path.Combine(MapManager.MapDirectory, entry.MapFile + ".map");
        if (string.Equals(Path.GetFileNameWithoutExtension(_currentMapFile), entry.MapFile, StringComparison.OrdinalIgnoreCase))
            return Functions.MaxDistance(_currentLocation, new Point(entry.X, entry.Y));

        var travel = MovementHelper.FindTravelPath(this, destPath);
        if (travel == null)
            return int.MaxValue;

        var current = _currentLocation;
        int dist = 0;
        foreach (var step in travel)
        {
            dist += Functions.MaxDistance(current, new Point(step.SourceX, step.SourceY));
            if (dist > maxDistance)
                return dist;
            current = new Point(step.DestinationX, step.DestinationY);
        }
        dist += Functions.MaxDistance(current, new Point(entry.X, entry.Y));
        return dist;
    }

    private bool TryFindNearestNpc(
        Func<NpcEntry, bool> match,
        out uint id,
        out Point location,
        out NpcEntry? entry)
    {
        id = 0;
        location = default;
        entry = null;
        if (string.IsNullOrEmpty(_currentMapFile))
            return false;

        int bestDist = int.MaxValue;
        foreach (var e in _npcMemory.GetAll())
        {
            if (IsNpcIgnored(e)) continue;
            if (!match(e)) continue;

            int dist = GetNpcTravelDistance(e, bestDist);
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

    private bool TryFindNearestNpc(
        Func<NpcEntry, List<ItemType>> match,
        out uint id,
        out Point location,
        out NpcEntry? entry,
        out List<ItemType> matchedTypes)
    {
        id = 0;
        location = default;
        entry = null;
        matchedTypes = new List<ItemType>();
        if (string.IsNullOrEmpty(_currentMapFile))
            return false;

        int bestDist = int.MaxValue;
        foreach (var e in _npcMemory.GetAll())
        {
            if (IsNpcIgnored(e)) continue;
            var types = match(e);
            if (types.Count == 0) continue;

            int dist = GetNpcTravelDistance(e, bestDist);
            if (dist < bestDist)
            {
                bestDist = dist;
                entry = e;
                location = new Point(e.X, e.Y);
                matchedTypes = types;
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
    public bool TryFindNearestNpc(ItemType type, out uint id, out Point location, out NpcEntry? entry, bool includeUnknowns = true)
    {
        return TryFindNearestNpc(e =>
        {
            bool knows = e.SellItemTypes != null && e.SellItemTypes.Contains(type);
            bool unknown = e.CanSell &&
                (e.SellItemTypes == null || !e.SellItemTypes.Contains(type)) &&
                (e.CannotSellItemTypes == null || !e.CannotSellItemTypes.Contains(type));
            return knows || (includeUnknowns && unknown);
        }, out id, out location, out entry);
    }

    public bool TryFindNearestRepairNpc(ItemType type, out uint id, out Point location, out NpcEntry? entry, bool includeUnknowns = true, bool special = false)
    {
        return TryFindNearestNpc(e =>
        {
            if (!e.CheckedMerchantKeys) return false;
            bool knows = special ? (e.SpecialRepairItemTypes != null && e.SpecialRepairItemTypes.Contains(type))
                                 : (e.RepairItemTypes != null && e.RepairItemTypes.Contains(type));
            bool unknown = (special ? e.CanSpecialRepair : e.CanRepair) &&
                ((special ? e.SpecialRepairItemTypes : e.RepairItemTypes) == null || !(special ? e.SpecialRepairItemTypes : e.RepairItemTypes)!.Contains(type)) &&
                ((special ? e.CannotSpecialRepairItemTypes : e.CannotRepairItemTypes) == null || !(special ? e.CannotSpecialRepairItemTypes : e.CannotRepairItemTypes)!.Contains(type));
            return knows || (includeUnknowns && unknown);
        }, out id, out location, out entry);
    }

    public bool TryFindNearestBuyNpc(ItemType type, out uint id, out Point location, out NpcEntry? entry, bool includeUnknowns = true)
    {
        return TryFindNearestNpc(e =>
        {
            bool knows = e.BuyItems != null && e.BuyItems.Any(b => ItemInfoDict.TryGetValue(b.Index, out var info) && info.Type == type);
            bool unknown = e.CanBuy && (e.BuyItems == null || !e.BuyItems.Any(b => ItemInfoDict.TryGetValue(b.Index, out var info) && info.Type == type));
            return knows || (includeUnknowns && unknown);
        }, out id, out location, out entry);
    }

    public bool TryFindNearestBuyNpc(DesiredItem desired, out uint id, out Point location, out NpcEntry? entry, bool includeUnknowns = true)
    {
        return TryFindNearestNpc(e =>
        {
            bool knows = e.BuyItems != null && e.BuyItems.Any(b => ItemInfoDict.TryGetValue(b.Index, out var info) && MatchesDesiredItem(info, desired));
            bool unknown = e.CanBuy && (e.BuyItems == null || !e.BuyItems.Any(b => ItemInfoDict.TryGetValue(b.Index, out var info) && MatchesDesiredItem(info, desired)));
            return knows || (includeUnknowns && unknown);
        }, out id, out location, out entry);
    }

    public bool TryFindNearestBuyNpc(IEnumerable<ItemType> types, out uint id, out Point location, out NpcEntry? entry, out List<ItemType> matchedTypes, bool includeUnknowns = true)
    {
        return TryFindNearestNpc(e =>
        {
            var sells = new List<ItemType>();
            foreach (var t in types)
            {
                bool knows = e.BuyItems != null && e.BuyItems.Any(b => ItemInfoDict.TryGetValue(b.Index, out var info) && info.Type == t);
                bool unknown = e.CanBuy && (e.BuyItems == null || !e.BuyItems.Any(b => ItemInfoDict.TryGetValue(b.Index, out var info) && info.Type == t));
                if (knows || (includeUnknowns && unknown))
                    sells.Add(t);
            }
            return sells;
        }, out id, out location, out entry, out matchedTypes);
    }

    private static bool MatchesDesiredItem(ItemInfo info, DesiredItem desired)
    {
        if (info.Type != desired.Type) return false;
        if (desired.Shape.HasValue && info.Shape != desired.Shape.Value) return false;
        if (desired.HpPotion.HasValue)
        {
            bool healsHP = info.Stats[Stat.HP] > 0 || info.Stats[Stat.HPRatePercent] > 0;
            bool healsMP = info.Stats[Stat.MP] > 0 || info.Stats[Stat.MPRatePercent] > 0;
            if (desired.HpPotion.Value && !healsHP) return false;
            if (!desired.HpPotion.Value && !healsMP) return false;
        }
        return true;
    }

    public bool TryFindNearestStorageNpc(out uint id, out Point location, out NpcEntry? entry)
    {
        return TryFindNearestNpc(e => e.CanStore, out id, out location, out entry);
    }

    public bool TryFindNearestNpc(IEnumerable<ItemType> types, out uint id, out Point location, out NpcEntry? entry, out List<ItemType> matchedTypes, bool includeUnknowns = true)
    {
        return TryFindNearestNpc(e =>
        {
            var sells = new List<ItemType>();
            foreach (var t in types)
            {
                bool knows = e.SellItemTypes != null && e.SellItemTypes.Contains(t);
                bool unknown = e.CanSell &&
                    (e.SellItemTypes == null || !e.SellItemTypes.Contains(t)) &&
                    (e.CannotSellItemTypes == null || !e.CannotSellItemTypes.Contains(t));
                if (knows || (includeUnknowns && unknown))
                    sells.Add(t);
            }
            return sells;
        }, out id, out location, out entry, out matchedTypes);
    }

    public bool TryFindNearestRepairNpc(IEnumerable<ItemType> types, out uint id, out Point location, out NpcEntry? entry, out List<ItemType> matchedTypes, bool includeUnknowns = true, bool special = false)
    {
        return TryFindNearestNpc(e =>
        {
            if (!e.CheckedMerchantKeys) return new List<ItemType>();
            var repairs = new List<ItemType>();
            foreach (var t in types)
            {
                bool knows = special ? (e.SpecialRepairItemTypes != null && e.SpecialRepairItemTypes.Contains(t))
                                     : (e.RepairItemTypes != null && e.RepairItemTypes.Contains(t));
                bool unknown = (special ? e.CanSpecialRepair : e.CanRepair) &&
                    ((special ? e.SpecialRepairItemTypes : e.RepairItemTypes) == null || !(special ? e.SpecialRepairItemTypes : e.RepairItemTypes)!.Contains(t)) &&
                    ((special ? e.CannotSpecialRepairItemTypes : e.CannotRepairItemTypes) == null || !(special ? e.CannotSpecialRepairItemTypes : e.CannotRepairItemTypes)!.Contains(t));
                if (knows || (includeUnknowns && unknown))
                    repairs.Add(t);
            }
            return repairs;
        }, out id, out location, out entry, out matchedTypes);
    }

    public bool TryFindNearestUnresolvedGoodsNpc(int maxDistance, out uint id, out Point location, out NpcEntry? entry)
    {
        id = 0;
        location = default;
        entry = null;
        if (string.IsNullOrEmpty(_currentMapFile))
            return false;

        int bestDist = int.MaxValue;
        foreach (var e in _npcMemory.GetAll())
        {
            if (!e.CanBuy || e.BuyItems == null) continue;
            if (e.BuyItems.All(b => ItemInfoDict.ContainsKey(b.Index))) continue;
            if (IsNpcIgnored(e)) continue;
            if (ResolvedGoodsNpcs.ContainsKey((e.Name, e.MapFile, e.X, e.Y))) continue;

            int dist = GetNpcTravelDistance(e, Math.Min(maxDistance, bestDist));
            if (dist > maxDistance || dist >= bestDist) continue;

            bestDist = dist;
            entry = e;
            location = new Point(e.X, e.Y);
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

    public async Task SellItemsToNpcAsync(uint npcId, IReadOnlyList<(UserItem item, ushort count)> items)
    {
        var entry = await ResolveNpcEntryAsync(npcId);
        if (entry == null)
        {
            UpdateLastStorageAction($"Unknown NPC id {npcId}");
            return;
        }
        BeginTransaction(npcId, entry);
        UpdateLastStorageAction($"Opening sell page at {entry.Name}");
        var interaction = _npcInteraction!;
        var page = await WithNpcDialogTimeoutAsync(ct => interaction.BeginAsync(ct), "opening sell page");
        if (page == null)
        {
            EndTransaction();
            UpdateLastStorageAction($"Timeout opening sell page at {entry.Name}");
            return;
        }
        string[] sellKeys = { "@BUYSELLNEW", "@BUYSELL", "@SELL" };
        var sellKey = page.Buttons.Select(b => b.Key).FirstOrDefault(k => sellKeys.Contains(k.ToUpper()));
        if (sellKey == null || sellKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Ending selling transaction early");
            UpdateLastStorageAction($"No sell option on {entry.Name}");
            EndTransaction();
            return;
        }
        using (var cts = new System.Threading.CancellationTokenSource(2000))
        {
            try
            {
                Log($"Accessing sell page...");
                await interaction.SelectFromMainAsync(sellKey, cts.Token);
                UpdateLastStorageAction($"Accessing sell page at {entry.Name}");
            }
            catch (OperationCanceledException)
            {
                UpdateLastStorageAction($"Timeout accessing sell page at {entry.Name}");
            }
        }
        foreach (var (item, count) in items)
        {
            if (item.Info == null) continue;
            if (item.Info.Bind.HasFlag(BindMode.DontSell))
            {
                Log($"Skipping {item.Info.Name} due to binding restrictions");
                UpdateLastStorageAction($"Cannot sell {item.Info.Name}; binding restriction");
                continue;
            }
            Log($"Processing {item.Info.Name}...");
            _pendingSellChecks[item.UniqueID] = (entry, item.Info.Type);
            using var cts = new System.Threading.CancellationTokenSource(2000);
            var waitTask = WaitForSellItemAsync(item.UniqueID, cts.Token);
            Log($"Selling {item.Info.Name} (x{count})...");
            UpdateLastStorageAction($"Selling {item.Info.Name} (x{count})");
            await SellItemAsync(item.UniqueID, count);
            try
            {
                await waitTask;
                UpdateLastStorageAction($"Sold {item.Info.Name} (x{count})");
            }
            catch (OperationCanceledException)
            {
                _pendingSellChecks.Remove(item.UniqueID);
                UpdateLastStorageAction($"Timeout selling {item.Info.Name}");
            }
            await Task.Delay(200);
        }

        // clear out any leftover npc responses that may arrive after selling
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(200);
            await WaitForNpcResponseAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        EndTransaction();
        UpdateLastStorageAction($"Finished selling at {entry.Name}");
    }

    public async Task<bool> RepairItemsAtNpcAsync(uint npcId)
    {
        var entry = await ResolveNpcEntryAsync(npcId);
        if (entry == null)
        {
            LogError($"Unknown NPC id {npcId} while opening storage");
            return false;
        }
        BeginTransaction(npcId, entry);
        var interaction = _npcInteraction!;
        var page = await WithNpcDialogTimeoutAsync(ct => interaction.BeginAsync(ct), "opening repair page");
        if (page == null)
        {
            EndTransaction();
            return false;
        }
        string[] repairKeys = { "@SREPAIR", "@REPAIR" };
        var repairKey = page.Buttons.Select(b => b.Key).FirstOrDefault(k => repairKeys.Contains(k.ToUpper())) ?? "@REPAIR";
        bool special = repairKey.Equals("@SREPAIR", StringComparison.OrdinalIgnoreCase);
        if (repairKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Ending repair interaction early.");
            EndTransaction();
            return false;
        }
        using (var cts = new CancellationTokenSource(2000))
        {
            try
            {
                var waitTask = WaitForNpcResponseAsync(cts.Token);
                Log($"Accessing repair key...");
                await interaction.SelectFromMainAsync(repairKey, cts.Token);
                await waitTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        var cantAfford = await RepairNeededItemsAsync(entry, special);
        try
        {
            using var cts = new CancellationTokenSource(200);
            await WaitForNpcResponseAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        EndTransaction();
        return cantAfford;
    }

    public async Task<BuyItemsResult> BuyNeededItemsAtNpcAsync(uint npcId)
    {
        var entry = await ResolveNpcEntryAsync(npcId);
        if (entry == null)
        {
            LogError($"Unknown NPC id {npcId} while opening storage");
            UpdateLastStorageAction($"Unknown NPC id {npcId}");
            return BuyItemsResult.Success;
        }
        BeginTransaction(npcId, entry);
        UpdateLastStorageAction($"Opening buy page at {entry.Name}");

        var interaction = _npcInteraction!;
        var page = await WithNpcDialogTimeoutAsync(ct => interaction.BeginAsync(ct), "opening buy page");
        if (page == null)
        {
            EndTransaction();
            UpdateLastStorageAction($"Timeout opening buy page at {entry.Name}");
            return BuyItemsResult.Success;
        }
        string[] buyKeys = { "@BUYSELLNEW", "@BUYSELL", "@BUYNEW", "@PEARLBUY", "@BUY" };
        var buyKey = page.Buttons.Select(b => b.Key).FirstOrDefault(k => buyKeys.Contains(k.ToUpper())) ?? "@BUY";
        if (buyKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
        {
            EndTransaction();
            UpdateLastStorageAction($"No buy option on {entry.Name}");
            return BuyItemsResult.Success;
        }

        using (var cts = new CancellationTokenSource(NpcDialogTimeoutMs))
        {
            try
            {
                var waitTask = WaitForNpcGoodsAsync(cts.Token);
                await interaction.SelectFromMainAsync(buyKey, cts.Token);
                await waitTask;
                UpdateLastStorageAction($"Accessing buy page at {entry.Name}");
            }
            catch (OperationCanceledException)
            {
                UpdateLastStorageAction($"Timeout accessing buy page at {entry.Name}");
            }
        }

        var result = BuyItemsResult.Success;
        if (_lastNpcGoods != null)
            result = await BuyNeededItemsFromGoodsAsync(_lastNpcGoods, _lastNpcGoodsType);

        try
        {
            using var cts = new CancellationTokenSource(200);
            await WaitForNpcResponseAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        EndTransaction();
        UpdateLastStorageAction($"Finished buying at {entry.Name}");
        return result;
    }

    public async Task OpenBuyPageAsync(uint npcId)
    {
        var entry = await ResolveNpcEntryAsync(npcId);
        if (entry == null) return;

        BeginTransaction(npcId, entry);

        var interaction = _npcInteraction!;
        var page = await WithNpcDialogTimeoutAsync(ct => interaction.BeginAsync(ct), "opening buy page");
        if (page == null)
        {
            EndTransaction();
            return;
        }
        string[] buyKeys = { "@BUYSELLNEW", "@BUYSELL", "@BUYNEW", "@PEARLBUY", "@BUY" };
        var buyKey = page.Buttons.Select(b => b.Key).FirstOrDefault(k => buyKeys.Contains(k.ToUpper())) ?? "@BUY";
        if (buyKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
        {
            EndTransaction();
            return;
        }

        using (var cts = new CancellationTokenSource(NpcDialogTimeoutMs))
        {
            try
            {
                var waitTask = WaitForNpcGoodsAsync(cts.Token);
                await interaction.SelectFromMainAsync(buyKey, cts.Token);
                await waitTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(200);
            await WaitForNpcResponseAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        EndTransaction();
    }

    public async Task<bool> OpenStorageAsync(uint npcId)
    {
        var entry = await ResolveNpcEntryAsync(npcId);
        if (entry == null)
        {
            LogError($"Unknown NPC id {npcId} while opening storage");
            UpdateLastStorageAction($"Unknown NPC id {npcId}");
            return false;
        }

        Log($"I am opening storage at {entry.Name}");
        UpdateLastStorageAction($"Opening storage at {entry.Name}");

        BeginTransaction(npcId, entry);

        var interaction = _npcInteraction!;
        var page = await WithNpcDialogTimeoutAsync(ct => interaction.BeginAsync(ct), "opening storage");
        if (page == null)
        {
            EndTransaction();
            UpdateLastStorageAction($"Timeout starting storage at {entry.Name}");
            return false;
        }

        var storageButton = page.Buttons.FirstOrDefault(b => b.Key.StartsWith("@STORAGE", StringComparison.OrdinalIgnoreCase));
        string storageKey = storageButton?.Key ?? "@STORAGE";
        if (storageButton == null)
        {
            LogError($"No storage option found on {entry.Name} page");
            UpdateLastStorageAction($"No storage button on {entry.Name}");
        }

        using (var cts2 = new CancellationTokenSource(NpcDialogTimeoutMs))
        {
            var waitPageTask = WaitForUserStorageAsync(cts2.Token);
            var waitDataTask = WaitForStorageLoadedAsync(cts2.Token);
            try
            {
                await interaction.SelectFromMainAsync(storageKey, cts2.Token);

                var combinedTask = Task.WhenAll(waitPageTask, waitDataTask);
                var finished = await Task.WhenAny(combinedTask, Task.Delay(NpcDialogTimeoutMs, cts2.Token));
                if (finished != combinedTask)
                {
                    LogError($"Timed out loading storage data (npc {entry.Name} id {npcId})");
                    EndTransaction();
                    UpdateLastStorageAction($"Timeout loading storage at {entry.Name}");
                    return false;
                }

                await combinedTask;
                Log($"Storage page opened successfully at {entry.Name}");
                UpdateLastStorageAction($"Opened storage page at {entry.Name}");
            }
            catch (OperationCanceledException)
            {
                LogError($"Timed out waiting for storage page (npc {entry.Name} id {npcId}, key {storageKey})");
                EndTransaction();
                UpdateLastStorageAction($"Timeout waiting for storage page {entry.Name}");
                return false;
            }
        }

        try
        {
            using var cts3 = new CancellationTokenSource(200);
            await WaitForNpcResponseAsync(cts3.Token);
        }
        catch (OperationCanceledException)
        {
        }

        EndTransaction();
        UpdateLastStorageAction($"Closed storage at {entry.Name}");
        return true;
    }

    private UserItem? AddItem(UserItem item)
    {
        if (_inventory == null) return null;

        if (item.Info != null && item.Info.StackSize > 1)
        {
            for (int i = 0; i < _inventory.Length; i++)
            {
                var temp = _inventory[i];
                if (temp == null || temp.Info.Index != item.Info.Index || temp.Count >= temp.Info.StackSize) continue;

                if (item.Count + temp.Count <= temp.Info.StackSize)
                {
                    temp.Count += item.Count;
                    CheckAutoStore(temp);
                    return temp;
                }

                item.Count -= (ushort)(temp.Info.StackSize - temp.Count);
                temp.Count = temp.Info.StackSize;
            }
        }

        if (item.Info != null)
        {
            if (item.Info.Type == ItemType.Potion || item.Info.Type == ItemType.Scroll ||
                (item.Info.Type == ItemType.Script && item.Info.Effect == 1))
            {
                for (int i = 0; i < BeltIdx - 2 && i < _inventory.Length; i++)
                {
                    if (_inventory[i] != null) continue;
                    _inventory[i] = item;
                    CheckAutoStore(item);
                    return item;
                }
            }
            else if (item.Info.Type == ItemType.Amulet)
            {
                for (int i = 4; i < BeltIdx && i < _inventory.Length; i++)
                {
                    if (_inventory[i] != null) continue;
                    _inventory[i] = item;
                    CheckAutoStore(item);
                    return item;
                }
            }
            else
            {
                for (int i = BeltIdx; i < _inventory.Length; i++)
                {
                    if (_inventory[i] != null) continue;
                    _inventory[i] = item;
                    CheckAutoStore(item);
                    return item;
                }
            }
        }

        for (int i = 0; i < _inventory.Length; i++)
        {
            if (_inventory[i] != null) continue;
            _inventory[i] = item;
            CheckAutoStore(item);
            return item;
        }

        return null;
    }

    private async Task<NpcEntry?> ResolveNpcEntryAsync(uint npcId, int timeoutMs = 2000)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (_npcEntries.TryGetValue(npcId, out var e))
                return e;
            await Task.Delay(50);
            waited += 50;
        }
        return null;
    }

    public async Task<uint> ResolveNpcIdAsync(NpcEntry entry, int timeoutMs = 2000)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            foreach (var kv in _npcEntries)
            {
                var e = kv.Value;
                if (ReferenceEquals(e, entry) ||
                    (e.Name == entry.Name &&
                     e.MapFile == entry.MapFile &&
                     e.X == entry.X &&
                     e.Y == entry.Y))
                {
                    return kv.Key;
                }
            }

            await Task.Delay(50);
            waited += 50;
        }

        return 0u;
    }
}
