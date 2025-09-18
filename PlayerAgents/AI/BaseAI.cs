using ClientPackets;
using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayerAgents.Map;

public class BaseAI
{
    protected readonly GameClient Client;
    protected static readonly Random Random = new();
    private TrackedObject? _currentTarget;
    private Point? _searchDestination;
    private Point? _lostTargetLocation;
    private DateTime _nextTargetSwitchTime = DateTime.MinValue;
    private List<Point>? _currentRoamPath;
    private List<Point>? _lostTargetPath;
    private DateTime _nextPathFindTime = DateTime.MinValue;
    private MirDirection? _lastRoamDirection;
    private readonly Dictionary<string, DateTime> _groupRequestTimes = new();


    protected virtual TimeSpan TargetSwitchInterval => TimeSpan.FromSeconds(3);
    // Using HashSet for faster Contains checks
    protected static readonly HashSet<EquipmentSlot> OffensiveSlots = new()
    {
        EquipmentSlot.Weapon,
        EquipmentSlot.Necklace,
        EquipmentSlot.RingL,
        EquipmentSlot.RingR,
        EquipmentSlot.Stone
    };

    // Monsters with these AI values or an empty name are ignored when selecting a target
    internal static readonly HashSet<byte> IgnoredAIs = new() { 6, 58, 57, 56, 64, 80, 81, 82 };

    private const int NpcInteractionRange = Globals.DataRange;

    protected static bool IsOffensiveSlot(EquipmentSlot slot) => OffensiveSlots.Contains(slot);

    public BaseAI(GameClient client)
    {
        Client = client;
        Client.ItemScoreFunc = GetItemScore;
        Client.DesiredItemsProvider = () => DesiredItems;
        Client.MovementEntryRemoved += OnMovementEntryRemoved;
        Client.ExpRateSaved += OnExpRateSaved;
        Client.WhisperCommandReceived += OnWhisperCommand;
        Client.PickUpFailed += OnPickUpFailed;
        Client.MonsterHidden += OnMonsterHidden;
        Client.MonsterDied += OnMonsterDied;
        Client.PlayerDied += OnPlayerDied;
        Client.NpcTravelPaused += OnNpcTravelPaused;
        Client.WhisperReceived += OnWhisperReceived;

        Client.ScanInventoryForAutoStore();
    }

    private void OnMovementEntryRemoved()
    {
        _travelPath = null;
        _currentRoamPath = null;
        _searchDestination = null;
        _nextPathFindTime = DateTime.MinValue;
        _lastRoamDirection = null;
        _travelDestinationMap = null;
    }

    private void OnExpRateSaved(double rate)
    {
        if (rate <= 0)
        {
            _currentBestMap = null;
            _nextBestMapCheck = DateTime.UtcNow;
        }
    }

    private void OnWhisperCommand(string command)
    {
        if (command.Equals("sell", StringComparison.OrdinalIgnoreCase))
        {
            TriggerInventoryRefresh();
        }
        else if (command.Equals("tame", StringComparison.OrdinalIgnoreCase))
        {
            OnTameCommand();
        }
        else if (command.StartsWith("bestmap", StringComparison.OrdinalIgnoreCase))
        {
            var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                _currentBestMap = parts[1];
                _nextBestMapCheck = DateTime.UtcNow;
                Client.Log($"Best map set to {_currentBestMap}");
            }
        }
    }

    private void OnPickUpFailed()
    {
        var loc = Client.CurrentLocation;
        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type == ObjectType.Item && obj.Location == loc)
                IgnoreItem(obj.Location, obj.Name, ItemRetryDelay);
        }
        if (_currentTarget != null &&
            _currentTarget.Type == ObjectType.Item &&
            _currentTarget.Location == loc)
        {
            _currentTarget = null;
        }
    }

    private void OnMonsterHidden(uint id)
    {
        IgnoreMonster(id);
        if (_currentTarget != null && _currentTarget.Id == id)
        {
            _currentTarget = null;
            _lostTargetLocation = null;
            _lostTargetPath = null;
            _currentRoamPath = null;
            _nextTargetSwitchTime = DateTime.MinValue;
            _nextPathFindTime = DateTime.MinValue;
        }
    }

    private void OnMonsterDied(uint id)
    {
        if (_currentTarget != null && _currentTarget.Id == id)
        {
            _currentTarget = null;
            _lostTargetLocation = null;
            _lostTargetPath = null;
            _currentRoamPath = null;
            _nextTargetSwitchTime = DateTime.MinValue;
            _nextPathFindTime = DateTime.MinValue;
        }
    }

    private void OnPlayerDied()
    {
        _currentBestMap = null;
        _nextBestMapCheck = DateTime.UtcNow;
        Client.LeaveGroup();
    }

    private void OnNpcTravelPaused()
    {
        Client.Log("NPC destination is blocked, pausing travel");
        _travelPauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        Client.UpdateAction("roaming...");
    }

    private void OnWhisperReceived(string sender, string message)
    {
        if (Client.Travelling && (_travelDestinationMap != null || Client.CurrentNpcInteraction != NpcInteractionType.General))
            return;

        if (message.Equals("group?", StringComparison.OrdinalIgnoreCase))
        {
            if (!Client.IsGrouped && Client.AllowGroup && Client.GroupMembers.Count < Client.Personality.MaxGroupCount)
                _ = Client.SendWhisperAsync(sender, "yes");
        }
        else if (message.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            if (_groupRequestTimes.TryGetValue(sender, out var time) &&
                DateTime.UtcNow - time < TimeSpan.FromMinutes(10) &&
                Client.AllowGroup && Client.GroupMembers.Count < Client.Personality.MaxGroupCount &&
                (Client.GroupMembers.Count == 0 || Client.IsGroupLeader))
            {
                _ = Client.InviteToGroupAsync(sender);
            }
        }
        else if (message.Equals("groupleader", StringComparison.OrdinalIgnoreCase))
        {
            var leader = Client.GroupLeader ?? "no leader";
            _ = Client.SendWhisperAsync(sender, leader);
        }
    }

    private void TriggerInventoryRefresh()
    {
        if (_refreshInventory) return;
        _refreshInventory = true;
        _inventoryTeleportTask = UseInventoryTownTeleportAsync();
    }

    private async Task HandleGroupingAsync()
    {
        if (Client.Travelling && (_travelDestinationMap != null || Client.CurrentNpcInteraction != NpcInteractionType.General))
            return;

        foreach (var kv in _groupRequestTimes.ToList())
            if (DateTime.UtcNow - kv.Value >= TimeSpan.FromMinutes(10))
                _groupRequestTimes.Remove(kv.Key);

        if (!Client.AllowGroup) return;
        if (Client.GroupMembers.Count >= Client.Personality.MaxGroupCount) return;
        if (Client.IsGrouped && !Client.IsGroupLeader) return;

        foreach (var player in Client.TrackedObjects.Values.Where(o => o.Type == ObjectType.Player && o.Id != Client.ObjectId))
        {
            if (Client.GroupMembers.Contains(player.Name)) continue;
            if (_groupRequestTimes.TryGetValue(player.Name, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(10))
                continue;

            bool killing = Client.TrackedObjects.Values.Any(m => m.Type == ObjectType.Monster &&
                m.EngagedWith == player.Id && DateTime.UtcNow - m.LastEngagedTime < TimeSpan.FromSeconds(5));
            if (!killing) continue;

            await Client.SendWhisperAsync(player.Name, "group?");
            _groupRequestTimes[player.Name] = DateTime.UtcNow;
        }
    }

    private async Task UseInventoryTownTeleportAsync()
    {
        if (DateTime.UtcNow < _nextInventoryTeleportTime) return;
        Client.LeaveGroup();
        var teleport = Client.FindTownTeleport();
        if (teleport == null) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var mapChange = Client.WaitForMapChangeAsync(waitForNextMap: true, cts.Token);
        await Client.UseItemAsync(teleport);
        string name = teleport.Info?.FriendlyName ?? "town teleport";
        Client.Log($"Used {name} for inventory refresh");
        if (mapChange.IsCompleted)
            mapChange = Client.WaitForMapChangeAsync(waitForNextMap: true, cts.Token);
        try
        {
            await mapChange;
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Client.RecordSafezoneAsync();
            _nextInventoryTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            _nextTownTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        }
        catch (TaskCanceledException)
        {
            Client.Log($"Map did not change after using {name}");
            _nextInventoryTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            _nextTownTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        }
    }

    private async Task WaitForInventoryTeleportAsync()
    {
        if (_inventoryTeleportTask == null) return;
        try
        {
            await _inventoryTeleportTask;
        }
        finally
        {
            _inventoryTeleportTask = null;
        }
    }

    protected virtual int WalkDelay => 600;
    protected virtual int AttackDelay => Client.GetAttackDelay();
    protected virtual TimeSpan RoamPathFindInterval => TimeSpan.FromSeconds(2);
    protected virtual TimeSpan FailedPathFindDelay => TimeSpan.FromSeconds(5);
    protected virtual TimeSpan TravelPathFindInterval => TimeSpan.FromSeconds(1);
    protected virtual TimeSpan FailedTravelPathFindDelay => TimeSpan.FromSeconds(1);
    protected virtual TimeSpan EquipCheckInterval => TimeSpan.FromSeconds(5);
    protected virtual int GoodsResolveDistance => 100;
    protected virtual double HpPotionWeightFraction => 0;
    protected virtual double MpPotionWeightFraction => 0;
    protected virtual Stat[] OffensiveStats => Array.Empty<Stat>();
    protected virtual Stat[] DefensiveStats => Array.Empty<Stat>();

    private bool _needsMpPotions;
    private bool _needsMountFood;
    private IReadOnlyList<DesiredItem>? _baseDesiredItems;
    protected virtual IReadOnlyList<DesiredItem> DesiredItems
    {
        get
        {
            bool needsMp = Client.HasSpellsThatRequireMP();
            bool needsFood = Client.MountNeedsFood();
            if (_baseDesiredItems == null || needsMp != _needsMpPotions || needsFood != _needsMountFood)
            {
                _needsMpPotions = needsMp;
                _needsMountFood = needsFood;
                _baseDesiredItems = BuildDesiredItems(needsMp, needsFood);
            }
            return _baseDesiredItems;
        }
    }

    private IReadOnlyList<DesiredItem> BuildDesiredItems(bool needsMpPotions, bool needsMountFood)
    {
        double hpWeight = HpPotionWeightFraction;
        if (!needsMpPotions)
            hpWeight += MpPotionWeightFraction;

        var list = new List<DesiredItem>
        {
            new DesiredItem(ItemType.Potion, hpPotion: true, weightFraction: hpWeight)
        };

        if (needsMpPotions)
            list.Add(new DesiredItem(ItemType.Potion, hpPotion: false, weightFraction: MpPotionWeightFraction));

        list.Add(new DesiredItem(ItemType.Torch, count: 1));
        list.Add(new DesiredItem(ItemType.Scroll, shape: 1, count: 1));
        if (needsMountFood)
            list.Add(new DesiredItem(ItemType.Food, count: 1));

        return list.ToArray();
    }
    private DateTime _nextEquipCheck = DateTime.UtcNow;
    private DateTime _nextAttackTime = DateTime.UtcNow;
    private DateTime _nextPotionTime = DateTime.MinValue;
    private DateTime _nextTownTeleportTime = DateTime.MinValue;
    private DateTime _nextInventoryTeleportTime = DateTime.MinValue;
    private DateTime _nextBestMapCheck = DateTime.MinValue;
    private string? _currentBestMap;
    private DateTime _travelPauseUntil = DateTime.MinValue;
    private List<MapMovementEntry>? _travelPath;
    private int _travelIndex;
    private string? _travelDestinationMap;
    private DateTime _stationarySince = DateTime.MinValue;
    private Point _lastStationaryLocation = Point.Empty;
    private DateTime _travelStuckSince = DateTime.MinValue;
    private DateTime _lastMoveOrAttackTime = DateTime.MinValue;
    private DateTime _movementSaveSince = DateTime.MinValue;
    
    private readonly Dictionary<(Point Location, string Name), RetryInfo> _itemRetryTimes = new();
    private readonly Dictionary<uint, DateTime> _monsterIgnoreTimes = new();
    private static readonly TimeSpan ItemRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnreachableItemRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DroppedItemRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MonsterIgnoreDelay = TimeSpan.FromSeconds(10);
    private const int DangerousMonsterRange = 3;
    private bool _sentRevive;
    private bool _sellingItems;
    private bool _repairingItems;
    private bool _buyingItems;
    private bool _buyAttempted;
    private bool _refreshInventory;
    private Task? _inventoryTeleportTask;
    private HashSet<ItemType> _pendingBuyTypes = new();

    private class RetryInfo
    {
        public DateTime RetryAt;
        public TimeSpan Delay;

        public RetryInfo(TimeSpan delay)
        {
            Delay = delay;
            RetryAt = DateTime.UtcNow + delay;
        }
    }

    protected void IgnoreMonster(uint id, TimeSpan? duration = null)
    {
        _monsterIgnoreTimes[id] = DateTime.UtcNow + (duration ?? MonsterIgnoreDelay);
        if (_currentTarget?.Id == id)
        {
            _currentTarget = null;
            _nextTargetSwitchTime = DateTime.MinValue;
        }
    }

    private void IgnoreItem(Point location, string name, TimeSpan baseDelay)
    {
        var key = (location, name);
        if (_itemRetryTimes.TryGetValue(key, out var info))
        {
            var newDelayTicks = Math.Max(info.Delay.Ticks * 2, baseDelay.Ticks);
            info.Delay = TimeSpan.FromTicks(newDelayTicks);
            info.RetryAt = DateTime.UtcNow + info.Delay;
        }
        else
        {
            _itemRetryTimes[key] = new RetryInfo(baseDelay);
        }
    }

    protected virtual int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        if (item.Info == null) return 0;

        var stats = IsOffensiveSlot(slot) ? OffensiveStats : DefensiveStats;
        if (stats.Length == 0)
        {
            int score = item.Info.Stats.Count;
            if (item.AddedStats != null)
                score += item.AddedStats.Count;
            return score;
        }

        int total = 0;
        foreach (var s in stats)
            total += item.Info.Stats[s] + item.AddedStats[s];
        return total;
    }

    private Point GetRandomPoint(PlayerAgents.Map.MapData map, Random random, Point origin, int radius, MirDirection? preferred)
    {
        if (preferred == null)
            return MovementHelper.GetRandomPoint(Client, map, random, origin, radius);

        const int attempts = 20;
        var obstacles = MovementHelper.BuildObstacles(Client);

        for (int i = 0; i < attempts; i++)
        {
            var dir = Functions.ShiftDirection(preferred.Value, random.Next(-1, 2));
            int distance = random.Next(1, radius + 1);
            var candidate = Functions.PointMove(origin, dir, distance);

            if (map.IsWalkable(candidate.X, candidate.Y) && !obstacles.Contains(candidate))
                return candidate;
        }

        return MovementHelper.GetRandomPoint(Client, map, random, origin, radius);
    }

    private UserItem? GetBestItemForSlot(EquipmentSlot slot, IEnumerable<UserItem?> inventory, UserItem? current)
    {
        int bestScore = current != null ? GetItemScore(current, slot) : -1;
        UserItem? bestItem = current;
        foreach (var item in inventory)
        {
            if (item == null) continue;
            if (!Client.CanEquipItem(item, slot)) continue;
            int score = GetItemScore(item, slot);
            if (bestItem == null || score > bestScore)
            {
                bestItem = item;
                bestScore = score;
            }
        }
        return bestItem;
    }

    private UserItem? GetBestMountItemForSlot(MountSlot slot, IEnumerable<UserItem?> inventory, UserItem? current)
    {
        int bestScore = current != null ? GetItemScore(current, EquipmentSlot.Mount) : -1;
        UserItem? bestItem = current;
        foreach (var item in inventory)
        {
            if (item == null) continue;
            if (!Client.CanEquipMountItem(item, slot)) continue;
            int score = GetItemScore(item, EquipmentSlot.Mount);
            if (bestItem == null || score > bestScore)
            {
                bestItem = item;
                bestScore = score;
            }
        }
        return bestItem;
    }

    private async Task CheckEquipmentAsync()
    {
        var inventory = Client.Inventory;
        var equipment = Client.Equipment;
        if (inventory == null || equipment == null) return;

        // create a mutable copy so we can mark equipped items as used
        var available = inventory.ToList();

        for (int slot = 0; slot < equipment.Count; slot++)
        {
            var equipSlot = (EquipmentSlot)slot;
            if (equipSlot == EquipmentSlot.Torch) continue;
            UserItem? current = equipment[slot];
            UserItem? bestItem = GetBestItemForSlot(equipSlot, available, current);

            if (bestItem != null && bestItem != current)
            {
                await Client.EquipItemAsync(bestItem, equipSlot);
                int idx = available.IndexOf(bestItem);
                if (idx >= 0) available[idx] = null; // prevent using same item twice
                if (bestItem.Info != null)
                    Client.Log($"I have equipped {bestItem.Info.FriendlyName}");
            }
        }

        var mount = equipment.Count > (int)EquipmentSlot.Mount ? equipment[(int)EquipmentSlot.Mount] : null;
        if (mount != null)
        {
            for (int slot = 0; slot < mount.Slots.Length; slot++)
            {
                var mountSlot = (MountSlot)slot;
                UserItem? current = mount.Slots[slot];
                UserItem? bestItem = GetBestMountItemForSlot(mountSlot, available, current);
                if (bestItem != null && bestItem != current)
                {
                    await Client.EquipMountItemAsync(bestItem, mountSlot);
                    int idx = available.IndexOf(bestItem);
                    if (idx >= 0) available[idx] = null;
                    if (bestItem.Info != null)
                        Client.Log($"I have equipped {bestItem.Info.FriendlyName}");
                }
            }
        }

        // handle torch based on time of day
        var torchSlot = EquipmentSlot.Torch;
        UserItem? currentTorch = equipment.Count > (int)torchSlot ? equipment[(int)torchSlot] : null;
        bool dark = Client.TimeOfDay == LightSetting.Night ||
                    Client.MapLight == LightSetting.Night ||
                    Client.MapDarkLight > 0;

        if (dark)
        {
            UserItem? bestTorch = GetBestItemForSlot(torchSlot, available, currentTorch);
            if (bestTorch != null && bestTorch != currentTorch)
            {
                await Client.EquipItemAsync(bestTorch, torchSlot);
                int idx = available.IndexOf(bestTorch);
                if (idx >= 0) available[idx] = null;
                if (bestTorch.Info != null)
                    Client.Log($"I have equipped {bestTorch.Info.FriendlyName}");
            }
        }
        else if (!dark && currentTorch != null)
        {
            if (currentTorch.Info != null)
                Client.Log($"I have unequipped {currentTorch.Info.FriendlyName}");
            await Client.UnequipItemAsync(torchSlot);
        }

        await Client.UseLearnableBooksAsync();
    }

    private async Task TryUsePotionsAsync()
    {
        if (DateTime.UtcNow < _nextPotionTime) return;

        int maxHP = Client.GetMaxHP();
        int maxMP = Client.GetMaxMP();

        if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster)
        {
            int dmg = Client.MonsterMemory.GetDamage(_currentTarget.Name);
            if (dmg > 0 && Client.HP <= dmg)
            {
                var pot = Client.FindPotion(true);
                if (pot != null)
                {
                    await Client.UseItemAsync(pot);
                    string name = pot.Info?.FriendlyName ?? "HP potion";
                    Client.Log($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return;
                }
            }
        }

        if (Client.HP < maxHP)
        {
            var pot = Client.FindPotion(true);
            double hpPercent = (double)Client.HP / maxHP;
            if (pot != null)
            {
                int heal = Client.GetPotionRestoreAmount(pot, true);
                if (heal > 0 && (maxHP - Client.HP >= heal || hpPercent <= 0.15))
                {
                    await Client.UseItemAsync(pot);
                    string name = pot.Info?.FriendlyName ?? "HP potion";
                    Client.Log($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return;
                }
            }
            else if (DateTime.UtcNow >= _nextTownTeleportTime && hpPercent <= 0.15)
            {
                var teleport = Client.FindTownTeleport();
                if (teleport != null)
                {
                    var mapChange = Client.WaitForMapChangeAsync(waitForNextMap: true);
                    await Client.UseItemAsync(teleport);
                    string name = teleport.Info?.FriendlyName ?? "town teleport";
                    Client.Log($"Used {name}");
                    await mapChange;
                    await Client.RecordSafezoneAsync();
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    _nextTownTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                    TriggerInventoryRefresh();
                    return;
                }
                else
                {
                    TriggerInventoryRefresh();
                }
            }
        }

        if (Client.MP < maxMP)
        {
            var pot = Client.FindPotion(false);
            if (pot != null)
            {
                int heal = Client.GetPotionRestoreAmount(pot, false);
                double mpPercent = (double)Client.MP / maxMP;
                if (heal > 0 && (maxMP - Client.MP >= heal || mpPercent <= 0.15))
                {
                    await Client.UseItemAsync(pot);
                    string name = pot.Info?.FriendlyName ?? "MP potion";
                    Client.Log($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }
            else
            {
                int minCost = GetMinAttackSpellCost();
                if (minCost > 0 && Client.MP < minCost)
                    TriggerInventoryRefresh();
            }
        }
    }

    private async Task TryFeedMountAsync()
    {
        if (!Client.MountNeedsFood()) return;

        var food = Client.FindMountFood();
        if (food != null)
        {
            await Client.UseItemAsync(food);
            string name = food.Info?.FriendlyName ?? "mount food";
            Client.Log($"Fed mount with {name}");
            return;
        }

        UpdatePendingBuyTypes();
    }

    private TrackedObject? FindClosestTarget(Point current, out int bestDist)
    {
        // if we are standing on an item, try to pick it up before doing anything else
        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type == ObjectType.Item && obj.Location == current &&
                (!_itemRetryTimes.TryGetValue((obj.Location, obj.Name), out var retry) || DateTime.UtcNow >= retry.RetryAt) &&
                Client.HasFreeBagSpace() && Client.GetCurrentBagWeight() < Client.GetMaxBagWeight())
            {
                bestDist = 0;
                return obj;
            }
        }

        TrackedObject? closestMonster = null;
        int monsterDist = int.MaxValue;
        TrackedObject? groupMonster = null;
        int groupDist = int.MaxValue;
        TrackedObject? closestItem = null;
        int itemDist = int.MaxValue;

        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type == ObjectType.Monster)
            {
                if (_monsterIgnoreTimes.TryGetValue(obj.Id, out var ignore) && DateTime.UtcNow < ignore)
                    continue;
                if (obj.Dead || obj.Hidden) continue;
                if (obj.Tamed) continue;
                if (string.IsNullOrEmpty(obj.Name)) continue;
                if (IsDangerousMonster(obj)) continue;
                if (ShouldIgnoreMonster(obj)) continue;
                if (IgnoredAIs.Contains(obj.AI)) continue;
                int dist = Functions.MaxDistance(current, obj.Location);
                if (obj.EngagedWith.HasValue && obj.EngagedWith.Value != Client.ObjectId)
                {
                    if (Client.IsGrouped && Client.IsGroupMember(obj.EngagedWith.Value))
                    {
                        if (dist < groupDist)
                        {
                            groupDist = dist;
                            groupMonster = obj;
                        }
                    }
                    continue;
                }
                if (dist < monsterDist)
                {
                    monsterDist = dist;
                    closestMonster = obj;
                }
            }
            else if (obj.Type == ObjectType.Item)
            {
                if (_itemRetryTimes.TryGetValue((obj.Location, obj.Name), out var retry) && DateTime.UtcNow < retry.RetryAt)
                    continue;
                int dist = Functions.MaxDistance(current, obj.Location);
                if (dist < itemDist)
                {
                    itemDist = dist;
                    closestItem = obj;
                }
            }
        }

        if (groupMonster != null)
        {
            bestDist = groupDist;
            return groupMonster;
        }

        // Prioritize adjacent monsters
        if (closestMonster != null && monsterDist <= 1)
        {
            bestDist = monsterDist;
            return closestMonster;
        }

        // choose nearest between remaining options
        if (closestMonster != null && (closestItem == null || monsterDist <= itemDist))
        {
            bestDist = monsterDist;
            return closestMonster;
        }

        if (closestItem != null)
        {
            bool isGold = string.Equals(closestItem.Name, "Gold", StringComparison.OrdinalIgnoreCase);
            if (isGold || (Client.HasFreeBagSpace() && Client.GetCurrentBagWeight() < Client.GetMaxBagWeight()))
            {
                bestDist = itemDist;
                return closestItem;
            }
        }

        bestDist = int.MaxValue;
        return null;
    }

    private async Task<List<Point>> FindPathAsync(PlayerAgents.Map.MapData map, Point start, Point dest, uint ignoreId = 0, int radius = 1)
    {
        return await MovementHelper.FindPathAsync(Client, map, start, dest, ignoreId, radius);
    }

    private async Task<bool> MoveAlongPathAsync(List<Point> path, Point destination)
    {
        bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, destination);
        Client.CurrentPathPoints = path.ToList();
        if (moved)
            _lastMoveOrAttackTime = DateTime.UtcNow;
        return moved;
    }

    protected virtual async Task<bool> MoveToTargetAsync(PlayerAgents.Map.MapData map, Point current, TrackedObject target, int radius = 1)
    {
        int distance = Functions.MaxDistance(current, target.Location);

        if (target.Type == ObjectType.Item)
        {
            if (distance > 0)
            {
                var path = await FindPathAsync(map, current, target.Location, target.Id, 0);
                bool moved = path.Count > 0 && await MoveAlongPathAsync(path, target.Location);
                if (!moved)
                {
                    bool blocked = Client.TrackedObjects.Values.Any(o =>
                        !o.Dead &&
                        (o.Type == ObjectType.Player || o.Type == ObjectType.Monster) &&
                        o.Location == target.Location);
                    var delay = path.Count == 0 || blocked ? UnreachableItemRetryDelay : ItemRetryDelay;
                    IgnoreItem(target.Location, target.Name, delay);
                    _currentTarget = null;
                }
                return true;
            }

            return false;
        }

        if (ShouldIgnoreDistantTarget(target, distance))
        {
            IgnoreMonster(target.Id);
            return true;
        }

        if (distance > radius)
        {
            var path = await FindPathAsync(map, current, target.Location, target.Id, radius);
            bool moved = path.Count > 0 && await MoveAlongPathAsync(path, target.Location);
            if (!moved)
            {
                IgnoreMonster(target.Id);
            }
            return true;
        }

        return false;
    }

    protected virtual async Task AttackTargetAsync(TrackedObject target, Point current)
    {
        if (target.Type == ObjectType.Item)
        {
            if (Client.HasFreeBagSpace() && Client.GetCurrentBagWeight() < Client.GetMaxBagWeight())
            {
                await Client.PickUpAsync();
            }
            IgnoreItem(target.Location, target.Name, ItemRetryDelay);
            _currentTarget = null;
            return;
        }

        if (target.Dead || target.Hidden)
        {
            _currentTarget = null;
            return;
        }

        if (DateTime.UtcNow >= _nextAttackTime)
        {
            if (Client.RidingMount)
            {
                await Client.EnsureUnmountedAsync();
            }

            await AttackMonsterAsync(target, current);
        }
    }

    protected virtual bool ShouldIgnoreDistantTarget(TrackedObject target, int distance)
    {
        return distance > 20;
    }

    protected virtual bool ShouldIgnoreMonster(TrackedObject monster)
    {
        return false;
    }

    protected virtual Task<bool> OnBeginTravelToBestMapAsync()
    {
        return Task.FromResult(true);
    }

    protected virtual void OnTameCommand()
    {
    }

    protected virtual string GetMonsterTargetAction(TrackedObject monster)
    {
        return $"attacking {monster.Name}";
    }

    protected void SetBestMap(string? map)
    {
        _currentBestMap = map;
    }

    private bool IsDangerousMonster(TrackedObject monster)
    {
        if (monster.Tamed)
            return false;
        if (IgnoredAIs.Contains(monster.AI))
            return false;
        int dmg = Client.MonsterMemory.GetDamage(monster.Name);
        int maxHP = Client.GetMaxHP();
        return dmg > maxHP / 2;
    }

    private async Task<bool> RetreatFromMonsterAsync(MapData map, Point current, TrackedObject monster)
    {
        var dir = Functions.DirectionFromPoint(monster.Location, current);
        var obstacles = MovementHelper.BuildObstacles(Client);
        Point dest = current;
        for (int i = 1; i <= DangerousMonsterRange; i++)
        {
            var p = Functions.PointMove(current, dir, i);
            if (!map.IsWalkable(p.X, p.Y) || obstacles.Contains(p)) break;
            dest = p;
        }
        if (dest != current)
        {
            var path = await FindPathAsync(map, current, dest);
            if (path.Count > 0)
                return await MoveAlongPathAsync(path, dest);
        }
        return false;
    }

    private async Task<bool> AvoidDangerousMonstersAsync(MapData map, Point current)
    {
        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type != ObjectType.Monster || obj.Dead || obj.Hidden) continue;
            if (!IsDangerousMonster(obj)) continue;
            int dist = Functions.MaxDistance(current, obj.Location);
            if (dist <= DangerousMonsterRange)
            {
                IgnoreMonster(obj.Id);
                bool moved = await RetreatFromMonsterAsync(map, current, obj);
                return moved;
            }
        }
        return false;
    }

    protected async Task<bool> TravelToMapAsync(string destMapFile)
    {
        if (DateTime.UtcNow < _travelPauseUntil)
            return false;

        Client.Log($"Finding travel path to {Path.GetFileNameWithoutExtension(destMapFile)}");
        var path = MovementHelper.FindTravelPath(Client, destMapFile);
        Client.Log(path == null ? "Travel path not found" : $"Travel path has {path.Count} steps");
        if (path == null)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            _travelDestinationMap = null;
            _travelPauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            Client.Travelling = false;
            return false;
        }

        if (path.Count == 0)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            _travelDestinationMap = null;
            Client.Travelling = false;
            return true;
        }

        _travelPath = path;
        _travelIndex = 0;
        _travelDestinationMap = Path.GetFileNameWithoutExtension(destMapFile);
        Client.Travelling = true;
        await Client.EnsureMountedAsync();
        UpdateTravelDestination();
        return true;
    }

    private void UpdateTravelDestination()
    {
        if (_travelPath == null) return;
        if (_travelIndex >= _travelPath.Count)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            _travelDestinationMap = null;
            Client.Travelling = false;
            Client.UpdateAction("roaming...");
            return;
        }

        string current = Path.GetFileNameWithoutExtension(Client.CurrentMapFile);
        var step = _travelPath[_travelIndex];

        if (current == step.DestinationMap)
        {
            _travelIndex++;
            if (_travelIndex >= _travelPath.Count)
            {
                _travelPath = null;
                _searchDestination = null;
                _lastRoamDirection = null;
                _travelDestinationMap = null;
                Client.Travelling = false;
                Client.UpdateAction("roaming...");
                return;
            }
            step = _travelPath[_travelIndex];
        }
        else if (current != step.SourceMap)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            _travelDestinationMap = null;
            Client.Travelling = false;
            Client.UpdateAction("roaming...");
            return;
        }

        var dest = new Point(step.SourceX, step.SourceY);
        if (_searchDestination == null || _searchDestination.Value != dest)
        {
            _searchDestination = dest;
            _currentRoamPath = null;
            _nextPathFindTime = DateTime.MinValue;
            _lastRoamDirection = null;
            Client.Log($"Travel destination set to {dest.X},{dest.Y} on {step.SourceMap}");
        }
    }

    private async Task ProcessBestMapAsync()
    {
        if (Client.IgnoreNpcInteractions ||
            _buyingItems || _sellingItems || _repairingItems ||
            DateTime.UtcNow < _travelPauseUntil)
            return;

        if (DateTime.UtcNow >= _nextBestMapCheck)
        {
            _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromHours(1);
            string? selected = Client.GetBestMapForLevel();
            if (selected == null || Random.Next(20) == 0)
            {
                var explore = Client.GetRandomExplorationMap();
                selected = string.IsNullOrEmpty(explore) ? _currentBestMap : explore;
            }

            if (selected != _currentBestMap)
            {
                _currentBestMap = selected;
                var currentMap = string.IsNullOrEmpty(Client.CurrentMapFile) ? null :
                    Path.GetFileNameWithoutExtension(Client.CurrentMapFile);
                if (!string.Equals(selected, currentMap, StringComparison.OrdinalIgnoreCase))
                {
                    TriggerInventoryRefresh();
                    var cantAfford = await SellRepairAndBuyAsync();
                    if (!InventoryNeedsRefresh() || cantAfford)
                        _refreshInventory = false;
                }
                // force path recalculation if destination changes or interval lapses
                _travelPath = null;
                Client.Travelling = false;
                _travelDestinationMap = null;
            }
        }

        if (_currentBestMap == null)
            return;

        var target = Path.Combine(MapManager.MapDirectory, _currentBestMap + ".map");
        if (!string.Equals(Client.CurrentMapFile, target, StringComparison.OrdinalIgnoreCase))
        {
            if (_travelPath == null)
            {
                if (!await OnBeginTravelToBestMapAsync())
                    return;
                Client.LeaveGroup();
                Client.Log($"Travelling to best map {_currentBestMap}");
                await TryFeedMountAsync();
                if (!await TravelToMapAsync(target))
                {
                    _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                    return;
                }
                _currentRoamPath = null;
                _lostTargetLocation = null;
                _lostTargetPath = null;
                _currentTarget = null;
            }
        }
    }

    private static bool MatchesDesiredItem(UserItem item, DesiredItem desired)
    {
        if (item.Info == null) return false;
        if (item.Info.Type != desired.Type) return false;
        if (desired.Shape.HasValue && item.Info.Shape != desired.Shape.Value) return false;
        if (desired.HpPotion.HasValue)
        {
            bool healsHP = item.Info.Stats[Stat.HP] > 0 || item.Info.Stats[Stat.HPRatePercent] > 0;
            bool healsMP = item.Info.Stats[Stat.MP] > 0 || item.Info.Stats[Stat.MPRatePercent] > 0;
            if (desired.HpPotion.Value && !healsHP) return false;
            if (!desired.HpPotion.Value && !healsMP) return false;
        }

        return true;
    }

    private Dictionary<UserItem, ushort> GetItemKeepCounts(IEnumerable<UserItem> inventory)
    {
        var keep = new Dictionary<UserItem, ushort>();
        int maxWeight = Client.GetMaxBagWeight();

        // Keep any potential equipment upgrades
        var equipment = Client.Equipment;
        if (equipment != null)
        {
            // Create mutable list so each item can only fill a single slot
            var available = inventory.ToList();

            for (int slot = 0; slot < equipment.Count; slot++)
            {
                var equipSlot = (EquipmentSlot)slot;
                if (equipSlot == EquipmentSlot.Torch) continue;

                UserItem? current = equipment[slot];
                UserItem? bestItem = GetBestItemForSlot(equipSlot, available, current);

                if (bestItem != null && bestItem != current)
                {
                    keep[bestItem] = bestItem.Count;
                    int idx = available.IndexOf(bestItem);
                    if (idx >= 0) available[idx] = null; // don't reuse same item
                }
            }

            var mount = equipment.Count > (int)EquipmentSlot.Mount ? equipment[(int)EquipmentSlot.Mount] : null;
            if (mount != null)
            {
                for (int slot = 0; slot < mount.Slots.Length; slot++)
                {
                    var mountSlot = (MountSlot)slot;
                    UserItem? current = mount.Slots[slot];
                    UserItem? bestItem = GetBestMountItemForSlot(mountSlot, available, current);
                    if (bestItem != null && bestItem != current)
                    {
                        keep[bestItem] = bestItem.Count;
                        int idx = available.IndexOf(bestItem);
                        if (idx >= 0) available[idx] = null;
                    }
                }
            }
        }

        // Keep desired items up to the configured quota
        foreach (var desired in DesiredItems)
        {
            var matching = inventory.Where(i => MatchesDesiredItem(i, desired)).ToList();

            if (desired.Count.HasValue)
            {
                int remaining = desired.Count.Value;
                if (equipment != null)
                {
                    remaining -= equipment
                        .Where(i => i != null && MatchesDesiredItem(i!, desired))
                        .Sum(i => i!.Count);
                    var mountItem = equipment.Count > (int)EquipmentSlot.Mount ? equipment[(int)EquipmentSlot.Mount] : null;
                    if (mountItem != null)
                        remaining -= mountItem.Slots
                            .Where(i => i != null && MatchesDesiredItem(i!, desired))
                            .Sum(i => i!.Count);
                }

                if (remaining > 0)
                {
                    foreach (var item in matching.OrderByDescending(i => i.Weight))
                    {
                        if (remaining <= 0) break;
                        ushort already = keep.TryGetValue(item, out var val) ? val : (ushort)0;
                        int available = item.Count - already;
                        if (available <= 0) continue;
                        ushort amount = (ushort)Math.Min(available, remaining);
                        keep[item] = (ushort)(already + amount);
                        remaining -= amount;
                    }
                }
            }

            if (desired.WeightFraction > 0)
            {
                int requiredWeight = (int)Math.Ceiling(maxWeight * desired.WeightFraction);
                int current = matching.Sum(i =>
                {
                    ushort kept = keep.TryGetValue(i, out var val) ? val : (ushort)0;
                    return i.Info!.Weight * kept;
                });
                foreach (var item in matching.OrderByDescending(i => i.Weight))
                {
                    if (current >= requiredWeight) break;
                    ushort already = keep.TryGetValue(item, out var val) ? val : (ushort)0;
                    if (item.Info == null) continue;
                    int available = item.Count - already;
                    if (available <= 0) continue;
                    int weightPer = item.Info.Weight;
                    int needed = (requiredWeight - current + weightPer - 1) / weightPer;
                    int add = Math.Min(available, needed);
                    keep[item] = (ushort)(already + add);
                    current += add * weightPer;
                }
            }
        }

        return keep;
    }

    private enum NpcInteractionResult
    {
        Success,
        PathFailed,
        NpcNotFound,
        CantAfford
    }

    protected virtual Task BeforeNpcInteractionAsync(Point location, uint npcId, NpcEntry? entry, NpcInteractionType interactionType)
        => Task.CompletedTask;

    protected virtual Task AfterNpcInteractionAsync(Point location, uint npcId, NpcEntry? entry, NpcInteractionType interactionType)
        => Task.CompletedTask;

    private async Task<NpcInteractionResult> InteractWithNpcAsync(Point location, uint npcId, NpcEntry? entry,
        NpcInteractionType interactionType, IReadOnlyList<(UserItem item, ushort count)>? sellItems = null)
    {
        await BeforeNpcInteractionAsync(location, npcId, entry, interactionType);
        try
        {
            if (DateTime.UtcNow < _travelPauseUntil)
            {
                Client.Log("NPC pathing paused");
                Client.UpdateAction("roaming...");
                return NpcInteractionResult.PathFailed;
            }

            await TryFeedMountAsync();

            Client.Log($"Moving to NPC {entry?.Name ?? npcId.ToString()} at {location.X},{location.Y}");
            bool reached = await Client.MoveWithinRangeAsync(location, npcId, NpcInteractionRange, interactionType, WalkDelay, entry?.MapFile);
            if (!reached)
            {
                Client.Log($"Could not path to {entry?.Name ?? npcId.ToString()}");
                return NpcInteractionResult.PathFailed;
            }

            if (npcId == 0 && entry != null)
                npcId = await Client.ResolveNpcIdAsync(entry);

            if (npcId == 0)
            {
                Client.Log($"Could not find NPC to {interactionType.ToString().ToLower()}");
                if (entry != null)
                {
                    var near = Client.TrackedObjects.Values.FirstOrDefault(o => o.Type == ObjectType.Merchant &&
                        Functions.MaxDistance(o.Location, location) <= NpcInteractionRange);
                    if (near != null)
                        Client.IgnoreNpc(entry);
                    Client.RemoveNpc(entry);
                }
                return NpcInteractionResult.NpcNotFound;
            }

            switch (interactionType)
            {
                case NpcInteractionType.General:
                    if (entry != null)
                        Client.StartNpcInteraction(npcId, entry);
                    break;
                case NpcInteractionType.Buying:
                    if (await Client.BuyNeededItemsAtNpcAsync(npcId))
                        return NpcInteractionResult.CantAfford;
                    break;
                case NpcInteractionType.Selling:
                    if (sellItems != null)
                    {
                        await Client.SellItemsToNpcAsync(npcId, sellItems);
                        Client.Log($"Finished selling to {entry?.Name ?? npcId.ToString()}");
                    }
                    break;
                case NpcInteractionType.Repairing:
                    if (await Client.RepairItemsAtNpcAsync(npcId))
                        return NpcInteractionResult.CantAfford;
                    Client.Log($"Finished repairing at {entry?.Name ?? npcId.ToString()}");
                    break;
            }

            return NpcInteractionResult.Success;
        }
        finally
        {
            await AfterNpcInteractionAsync(location, npcId, entry, interactionType);
        }
    }

    private async Task HandleInventoryAsync(bool force = false, bool keepIgnore = false)
    {
        if (_sellingItems) return;
        var inventory = Client.Inventory;
        if (inventory == null) return;

        bool full = !Client.HasFreeBagSpace();
        bool heavy = Client.GetCurrentBagWeight() >= Client.GetMaxBagWeight() * 0.9;
        if (!force && !full && !heavy) return;

        var items = inventory
            .Where(i => i != null && i.Info != null &&
                        !i.Info.Bind.HasFlag(BindMode.DontSell))
            .ToList();
        var keepCounts = GetItemKeepCounts(items);
        var sellGroups = items
            .Select(i => {
                ushort keep = keepCounts.TryGetValue(i, out var k) ? k : (ushort)0;
                ushort sell = (ushort)Math.Max(i.Count - keep, 0);
                return (item: i, sell);
            })
            .Where(t => t.sell > 0)
            .GroupBy(t => t.item!.Info!.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

        _sellingItems = true;
        Client.UpdateAction("selling items");
        Client.IgnoreNpcInteractions = true;
        while (sellGroups.Count > 0)
        {
            var types = sellGroups.Keys.ToList();
            if (!Client.TryFindNearestNpc(types, out var npcId, out var loc, out var entry, out var matchedTypes))
                break;

            int count = matchedTypes.Sum(t => sellGroups[t].Sum(x => x.sell));
            Client.Log($"Heading to {entry?.Name ?? "unknown npc"} at {loc.X},{loc.Y} to sell {count} items");

            var sellItems = matchedTypes.SelectMany(t => sellGroups[t]).Where(x => x.item != null).ToList();
            var result = await InteractWithNpcAsync(loc, npcId, entry, NpcInteractionType.Selling, sellItems);

            if (result == NpcInteractionResult.Success)
            {
                foreach (var t in matchedTypes)
                    sellGroups.Remove(t);
            }
            else
            {
                if (result == NpcInteractionResult.NpcNotFound)
                    continue;
                break;
            }
        }
        if (!keepIgnore)
        {
            Client.IgnoreNpcInteractions = false;
            Client.ResumeNpcInteractions();
        }
        _sellingItems = false;
        Client.UpdateAction("roaming...");
        await ResolveNearbyGoodsAsync();
    }

    private async Task HandleStorageAsync()
    {
        if (DateTime.UtcNow < _travelPauseUntil) return;

        bool needToStore = Client.PendingStorageItems.Any();
        bool storageLoaded = Client.Storage != null;

        if (!needToStore && storageLoaded)
        {
            Client.Log("Storage already loaded, checking stored items for upgrades");
            await Client.CheckStorageForUpgradesAsync();
            return;
        }

        if (!Client.TryFindNearestStorageNpc(out var npcId, out var loc, out var entry))
            return;

        if (entry != null)
            Client.Log($"I am heading to {entry.Name} at {loc.X}, {loc.Y} to access storage");

        Client.UpdateAction("accessing storage...");
        bool reached = await Client.MoveWithinRangeAsync(loc, npcId, NpcInteractionRange, NpcInteractionType.Storing, WalkDelay, entry?.MapFile);
        if (!reached) return;

        if (npcId == 0 && entry != null)
            npcId = await Client.ResolveNpcIdAsync(entry);
        if (npcId == 0) return;

        bool opened = await Client.OpenStorageAsync(npcId);
        if (!opened)
        {
            Client.LogError($"Failed to open storage with NPC {entry?.Name ?? npcId.ToString()}");
            Client.UpdateAction("roaming...");
            return;
        }

        foreach (var item in Client.PendingStorageItems.ToList())
        {
            if (!Client.HasFreeStorageSpace()) break;
            await Client.StoreItemAsync(item);
        }

        await Client.CheckStorageForUpgradesAsync();
    }

    private async Task<bool> HandleEquipmentRepairsAsync(bool force = false, bool keepIgnore = false)
    {
        bool cantAfford = false;
        if (_repairingItems) return cantAfford;
        var equipment = Client.Equipment;
        if (equipment == null) return cantAfford;

        var toRepair = equipment
            .Where(i => i != null && i.Info != null &&
                        i.Info.Type != ItemType.Torch &&
                        i.Info.Type != ItemType.Mount &&
                        i.CurrentDura < i.MaxDura &&
                        !i.Info.Bind.HasFlag(BindMode.DontRepair))
            .ToList();
        if (toRepair.Count == 0) return cantAfford;

        bool urgent = toRepair.Any(i => i.MaxDura > 0 && i.CurrentDura <= i.MaxDura * 0.05);
        if (!force && !urgent) return cantAfford;

        _repairingItems = true;
        Client.UpdateAction("repairing items...");
        Client.IgnoreNpcInteractions = true;

        var types = toRepair.Select(i => i!.Info!.Type).Distinct().ToList();
        while (types.Count > 0)
        {
            if (!Client.TryFindNearestRepairNpc(types, out var npcId, out var loc, out var entry, out var matched))
                break;

            if (entry != null)
            {
                var itemNames = toRepair.Where(i => i.Info != null && matched.Contains(i.Info.Type))
                    .Select(i => i.Info!.FriendlyName)
                    .ToList();
                if (itemNames.Count > 0)
                    Client.Log($"I am heading to {entry.Name} at {loc.X}, {loc.Y} to repair {string.Join(", ", itemNames)}");
            }

            var result = await InteractWithNpcAsync(loc, npcId, entry, NpcInteractionType.Repairing);

            if (result == NpcInteractionResult.Success)
            {
                foreach (var t in matched)
                    types.Remove(t);
            }
            else
            {
                if (result == NpcInteractionResult.CantAfford)
                    cantAfford = true;
                break;
            }
        }

        if (!keepIgnore)
        {
            Client.IgnoreNpcInteractions = false;
            Client.ResumeNpcInteractions();
        }
        _repairingItems = false;
        Client.UpdateAction("roaming...");
        await ResolveNearbyGoodsAsync();
        return cantAfford;
    }

    private bool NeedMoreOfDesiredItem(DesiredItem desired)
    {
        var inventory = Client.Inventory;
        if (inventory == null) return false;
        var matching = inventory.Where(i => i != null && MatchesDesiredItem(i!, desired)).ToList();

        int count = Client.GetDesiredItemCount(desired);

        if (desired.Count.HasValue)
            return count < desired.Count.Value;

        if (desired.WeightFraction > 0)
        {
            int requiredWeight = (int)Math.Ceiling(Client.GetMaxBagWeight() * desired.WeightFraction);
            int currentWeight = matching.Sum(i => i!.Weight);
            return currentWeight < requiredWeight;
        }

        return false;
    }

    private HashSet<ItemType> GetNeededBuyTypes()
    {
        var needed = new HashSet<ItemType>();
        foreach (var d in DesiredItems)
            if (NeedMoreOfDesiredItem(d))
                needed.Add(d.Type);
        foreach (var info in Client.GetEquipmentUpgradeBuyTypes())
            needed.Add(info.Type);
        if (Client.AnyNpcHasLearnableBook())
            needed.Add(ItemType.Book);
        return needed;
    }

    private void UpdatePendingBuyTypes()
    {
        _pendingBuyTypes = GetNeededBuyTypes();
        _buyAttempted = false;
    }

    private void RefreshPendingBuyTypes(IEnumerable<ItemType>? types = null)
    {
        var toCheck = types != null ? types.ToList() : _pendingBuyTypes.ToList();
        foreach (var type in toCheck)
        {
            var desired = DesiredItems.Where(d => d.Type == type).ToList();
            if (desired.Count == 0 || desired.All(d => !NeedMoreOfDesiredItem(d)))
                _pendingBuyTypes.Remove(type);
        }
    }

    private async Task<bool> HandleBuyingItemsAsync(bool keepIgnore = false)
    {
        bool cantAfford = false;
        if (_buyingItems || _buyAttempted) return cantAfford;

        if (_pendingBuyTypes.Count == 0) return cantAfford;

        _buyAttempted = true;
        _buyingItems = true;
        Client.UpdateAction("buying items");
        Client.IgnoreNpcInteractions = true;

        try
        {
            while (_pendingBuyTypes.Count > 0)
            {
                uint npcId = 0;
                Point loc = default;
                NpcEntry? entry = null;
                var matched = new List<ItemType>();

                EquipmentUpgradeInfo? upgrade = null;
                foreach (var t in _pendingBuyTypes)
                {
                    if (Client.TryGetEquipmentUpgradeTarget(t, out var info))
                    {
                        upgrade = info;
                        break;
                    }
                }

                if (upgrade != null)
                {
                    entry = upgrade.Npc;
                    loc = new Point(entry.X, entry.Y);
                    matched.Add(upgrade.Type);
                }
                else if (_pendingBuyTypes.Contains(ItemType.Book) && Client.TryFindNearestLearnableBookNpc(out npcId, out loc, out entry))
                {
                    matched.Add(ItemType.Book);
                }
                else
                {
                    DesiredItem? target = null;
                    foreach (var desired in DesiredItems)
                    {
                        if (!_pendingBuyTypes.Contains(desired.Type) || !NeedMoreOfDesiredItem(desired)) continue;
                        if (Client.TryFindNearestBuyNpc(desired, out npcId, out loc, out entry, includeUnknowns: false))
                        {
                            target = desired;
                            matched.Add(desired.Type);
                            break;
                        }
                    }

                    if (target == null)
                        break;
                }

                if (entry != null)
                    Client.Log($"Heading to {entry.Name} at {loc.X}, {loc.Y} to buy {string.Join(", ", matched.Select(t => t.ToString()))}");

                var result = await InteractWithNpcAsync(loc, npcId, entry, NpcInteractionType.Buying);

                RefreshPendingBuyTypes();

                if (result != NpcInteractionResult.Success)
                {
                    if (result == NpcInteractionResult.CantAfford)
                        cantAfford = true;
                    break;
                }
            }
        }
        finally
        {
            if (!keepIgnore)
            {
                Client.IgnoreNpcInteractions = false;
                Client.ResumeNpcInteractions();
            }
            RefreshPendingBuyTypes();
            _buyingItems = false;
            Client.UpdateAction("roaming...");
            await ResolveNearbyGoodsAsync();
            UpdateTravelDestination();
        }
        return cantAfford;
    }

    private async Task<bool> SellRepairAndBuyAsync()
    {
        await WaitForInventoryTeleportAsync();
        await BeforeNpcInteractionAsync(Client.CurrentLocation, 0, null, NpcInteractionType.General);
        try
        {
            Client.IgnoreNpcInteractions = true;
            try
            {
                await HandleStorageAsync();
                await HandleInventoryAsync(true, true);
                bool cantAfford = await HandleEquipmentRepairsAsync(true, true);
                UpdatePendingBuyTypes();
                cantAfford |= await HandleBuyingItemsAsync(true);
                await ResolveNearbyGoodsAsync();
                return cantAfford;
            }
            finally
            {
                Client.IgnoreNpcInteractions = false;
                Client.ResumeNpcInteractions();
            }
        }
        finally
        {
            await AfterNpcInteractionAsync(Client.CurrentLocation, 0, null, NpcInteractionType.General);
        }
    }

    private async Task ResolveNearbyGoodsAsync()
    {
        if (DateTime.UtcNow < _travelPauseUntil) return;

        if (!Client.TryFindNearestUnresolvedGoodsNpc(GoodsResolveDistance, out var npcId, out var loc, out var entry))
            return;

        Client.UpdateAction(entry != null ? $"resolving npc {entry.Name}" : "resolving npc");

        Client.IgnoreNpcInteractions = true;
        try
        {
            if (entry != null)
                Client.Log($"Resolving goods at {entry.Name} at {loc.X}, {loc.Y}");

            bool reached = await Client.MoveWithinRangeAsync(loc, npcId, NpcInteractionRange, NpcInteractionType.Buying, WalkDelay, entry?.MapFile);
            if (!reached)
                return;

            if (npcId == 0 && entry != null)
                npcId = await Client.ResolveNpcIdAsync(entry);

            if (npcId != 0)
                await Client.OpenBuyPageAsync(npcId);
        }
        finally
        {
            Client.IgnoreNpcInteractions = false;
            Client.ResumeNpcInteractions();
            Client.UpdateAction("roaming...");
        }
    }

    private bool InventoryNeedsRefresh()
    {
        if (_pendingBuyTypes.Count > 0)
            return true;

        var equipment = Client.Equipment;
        if (equipment != null && equipment.Any(i => i != null && i.Info != null && i.Info.Type != ItemType.Torch && i.Info.Type != ItemType.Mount && i.CurrentDura < i.MaxDura))
            return true;

        var inventory = Client.Inventory;
        if (inventory != null)
        {
            var items = inventory.Where(i => i != null && i.Info != null).ToList();
            var keepCounts = GetItemKeepCounts(items);
            if (items.Any(i =>
            {
                ushort keep = keepCounts.TryGetValue(i, out var k) ? k : (ushort)0;
                return i.Count > keep;
            }))
                return true;
        }

        return false;
    }

    private bool NeedsImmediateSellOrRepair()
    {
        bool bagFull = !Client.HasFreeBagSpace() ||
            Client.GetCurrentBagWeight() >= Client.GetMaxBagWeight() * 0.9;

        bool needRepair = false;
        var equipment = Client.Equipment;
        if (equipment != null)
        {
            needRepair = equipment.Any(i => i != null && i.Info != null &&
                i.Info.Type != ItemType.Torch && i.MaxDura > 0 &&
                i.CurrentDura <= Math.Max(1, i.MaxDura * 0.02));
        }

        return bagFull || needRepair;
    }

    public virtual async Task RunAsync()
    {
        Point current;
        _nextBestMapCheck = DateTime.UtcNow;
        _lastStationaryLocation = Client.CurrentLocation;
        _stationarySince = DateTime.UtcNow;
        _lastMoveOrAttackTime = DateTime.UtcNow;
        _buyAttempted = false;
        await Client.SetAllowGroupAsync(true);
        while (!Client.Disconnected)
        {
            Client.StartCycle();
            if (await HandleReviveAsync())
                continue;

            if (await HandleHarvestingAsync())
                continue;

            if (Client.MovementSavePending)
            {
                if (_movementSaveSince == DateTime.MinValue)
                    _movementSaveSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - _movementSaveSince > TimeSpan.FromSeconds(5))
                {
                    Client.Log("Movement save stuck, clearing");
                    Client.ForceClearMovementSave();
                    _movementSaveSince = DateTime.MinValue;
                }

                await Task.Delay(WalkDelay);
                continue;
            }
            else
            {
                _movementSaveSince = DateTime.MinValue;
            }

            if (!_refreshInventory && NeedsImmediateSellOrRepair())
                TriggerInventoryRefresh();

            if (!Client.IsGrouped)
                Client.ProcessMapExpRateInterval();
            if (_refreshInventory)
            {
                var cantAfford = await SellRepairAndBuyAsync();
                if (!InventoryNeedsRefresh() || cantAfford)
                    _refreshInventory = false;
            }
            else
            {
                await HandleBuyingItemsAsync();
            }
            await HandleGroupingAsync();
            await ProcessBestMapAsync();
            UpdateTravelDestination();
            bool traveling = _travelPath != null && DateTime.UtcNow >= _travelPauseUntil;
            if (traveling)
            {
                _currentTarget = null;
                _lostTargetLocation = null;
                _lostTargetPath = null;
            }
            if (Client.IsProcessingNpc)
            {
                await Task.Delay(WalkDelay);
                continue;
            }

            if (DateTime.UtcNow >= _nextEquipCheck)
            {
                await CheckEquipmentAsync();
                _nextEquipCheck = DateTime.UtcNow + EquipCheckInterval;
            }

            await HandleEquipmentRepairsAsync();

            await TryUsePotionsAsync();

            await HandleInventoryAsync();

            await TryFeedMountAsync();

            if (Client.GetCurrentBagWeight() > Client.GetMaxBagWeight() && Client.LastPickedItem != null)
            {
                Client.Log("Overweight detected, dropping last picked item");
                var drop = Client.LastPickedItem;
                await Client.DropItemAsync(drop);
                if (drop?.Info != null)
                {
                    // item may spawn on any adjacent cell so ignore all nearby copies
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var loc = new Point(Client.CurrentLocation.X + dx, Client.CurrentLocation.Y + dy);
                            IgnoreItem(loc, drop.Info.FriendlyName, DroppedItemRetryDelay);
                        }
                    }
                }
            }

            foreach (var kv in _itemRetryTimes.ToList())
                if (DateTime.UtcNow >= kv.Value.RetryAt)
                    _itemRetryTimes.Remove(kv.Key);

            foreach (var kv in _monsterIgnoreTimes.ToList())
                if (DateTime.UtcNow >= kv.Value)
                    _monsterIgnoreTimes.Remove(kv.Key);

            if (!Client.IsProcessingNpc && DateTime.UtcNow >= _travelPauseUntil && Client.TryDequeueNpc(out var npcId, out var entry))
            {
                var npcLoc = new Point(entry.X, entry.Y);
                _ = await InteractWithNpcAsync(npcLoc, npcId, entry, NpcInteractionType.General);
                await Task.Delay(WalkDelay);
                continue;
            }

            var map = Client.CurrentMap;
            if (map == null || !Client.IsMapLoaded)
            {
                await Task.Delay(WalkDelay);
                continue;
            }

            current = Client.CurrentLocation;
            if (Client.GroupLeader != null && !Client.IsGroupLeader)
            {
                var leaderName = Client.GroupLeader;
                var leader = Client.TrackedObjects.Values
                    .Where(o => o.Type == ObjectType.Player && o.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (leader != null && !Functions.InRange(current, leader.Location, 7))
                {
                    await MoveToTargetAsync(map, current, leader, 7);
                    await Task.Delay(WalkDelay);
                    continue;
                }
            }
            else if (Client.IsGroupLeader)
            {
                TrackedObject? farMember = null;
                int maxDist = 0;
                foreach (var name in Client.GroupMembers)
                {
                    if (string.Equals(name, Client.PlayerName, StringComparison.OrdinalIgnoreCase)) continue;
                    var member = Client.TrackedObjects.Values
                        .Where(o => o.Type == ObjectType.Player && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                    if (member == null) continue;
                    int dist = Functions.MaxDistance(current, member.Location);
                    if (dist > 10 && dist > maxDist)
                    {
                        maxDist = dist;
                        farMember = member;
                    }
                }
                if (farMember != null)
                {
                    await MoveToTargetAsync(map, current, farMember, 10);
                    await Task.Delay(WalkDelay);
                    continue;
                }
            }
            if (await AvoidDangerousMonstersAsync(map, current))
            {
                await Task.Delay(WalkDelay);
                continue;
            }
            if (_currentTarget != null && !Client.TrackedObjects.ContainsKey(_currentTarget.Id))
            {
                _lostTargetLocation = _currentTarget.Location;
                _lostTargetPath = null;
                _currentRoamPath = null;
                _currentTarget = null;
                _nextPathFindTime = DateTime.MinValue;
            }
            if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster)
            {
                if (_currentTarget.Dead || _currentTarget.Hidden)
                {
                    _nextTargetSwitchTime = DateTime.MinValue;
                    if (_currentTarget.Hidden)
                    {
                        _lostTargetLocation = _currentTarget.Location;
                        _lostTargetPath = null;
                        _currentRoamPath = null;
                    }
                    _currentTarget = null;
                    _nextPathFindTime = DateTime.MinValue;
                }
            }
            int distance = 0;
            TrackedObject? closest = traveling ? null : FindClosestTarget(current, out distance);

            if (!traveling && _currentTarget != null && _currentTarget.Type == ObjectType.Monster &&
                !_currentTarget.Dead &&
                Client.TrackedObjects.ContainsKey(_currentTarget.Id) &&
                closest != null && closest.Type == ObjectType.Monster &&
                closest.Id != _currentTarget.Id &&
                DateTime.UtcNow < _nextTargetSwitchTime)
            {
                closest = _currentTarget;
                distance = Functions.MaxDistance(current, _currentTarget.Location);
            }

            if (closest != null)
            {
                _currentRoamPath = null;
                _lostTargetLocation = null;
                _lostTargetPath = null;
                _nextPathFindTime = DateTime.MinValue;
                if (_currentTarget?.Id != closest.Id)
                {
                    Client.Log($"I have targeted {closest.Name} at {closest.Location.X}, {closest.Location.Y}");
                    _currentTarget = closest;
                    if (closest.Type == ObjectType.Monster)
                        _nextTargetSwitchTime = DateTime.UtcNow + TargetSwitchInterval;
                }

                bool moved = await MoveToTargetAsync(map, current, closest, closest.Type == ObjectType.Item ? 0 : 1);
                if (!moved)
                {
                    await AttackTargetAsync(closest, current);
                }
            }
            else
            {
                _currentTarget = null;
                if (!traveling && _lostTargetLocation.HasValue)
                {
                    if (Functions.MaxDistance(current, _lostTargetLocation.Value) <= 0)
                    {
                        _lostTargetLocation = null;
                        _lostTargetPath = null;
                    }
                    else
                    {
                        if (_lostTargetPath == null || _lostTargetPath.Count <= 1)
                        {
                            if (DateTime.UtcNow >= _nextPathFindTime)
                            {
                                _lostTargetPath = await FindPathAsync(map, current, _lostTargetLocation.Value);
                                Client.Log($"Lost target path length {_lostTargetPath.Count}");
                                _nextPathFindTime = DateTime.UtcNow + RoamPathFindInterval;
                                if (_lostTargetPath.Count == 0)
                                {
                                    _lostTargetLocation = null;
                                    _lostTargetPath = null;
                                }
                            }
                        }

                        if (_lostTargetPath != null && _lostTargetPath.Count > 0)
                        {
                            var lostPath = _lostTargetPath;
                            bool moved = await MoveAlongPathAsync(lostPath, _lostTargetLocation.Value);

                            if (_lostTargetPath == lostPath)
                            {
                                if (!moved)
                                {
                                    _lostTargetPath = null;
                                    _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                                }
                                else if (lostPath.Count <= 1)
                                {
                                    _lostTargetPath = null;
                                    _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                                }
                            }
                        }
                    }
                }

                if (!traveling && !_lostTargetLocation.HasValue)
                {
                    if (Client.GroupLeader != null && !Client.IsGroupLeader)
                    {
                        var leaderName = Client.GroupLeader;
                        var leader = Client.TrackedObjects.Values
                            .Where(o => o.Type == ObjectType.Player && o.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase))
                            .FirstOrDefault();
                        if (leader != null)
                        {
                            _searchDestination = null;
                            _currentRoamPath = null;
                            _lastRoamDirection = null;
                            await MoveToTargetAsync(map, current, leader, 7);
                            await Task.Delay(WalkDelay);
                            continue;
                        }
                    }

                    if (_searchDestination == null ||
                        Functions.MaxDistance(current, _searchDestination.Value) <= 1 ||
                        !map.IsWalkable(_searchDestination.Value.X, _searchDestination.Value.Y) ||
                        Client.BlockingCells.Contains(_searchDestination.Value))
                    {
                        _searchDestination = GetRandomPoint(map, Random, current, 50, _lastRoamDirection);
                        _lastRoamDirection = Functions.DirectionFromPoint(current, _searchDestination.Value);
                        _currentRoamPath = null;
                        _nextPathFindTime = DateTime.MinValue;
                        Client.Log($"No targets nearby, searching at {_searchDestination.Value.X}, {_searchDestination.Value.Y}");
                    }

                    if (_currentRoamPath == null || _currentRoamPath.Count <= 1)
                    {
                        if (DateTime.UtcNow >= _nextPathFindTime)
                        {
                            _currentRoamPath = await FindPathAsync(map, current, _searchDestination.Value, 0, 0);
                            Client.Log($"Roam path length {_currentRoamPath.Count}");
                            _nextPathFindTime = DateTime.UtcNow + RoamPathFindInterval;
                            if (_currentRoamPath.Count == 0)
                            {
                                _currentRoamPath = null;
                                _searchDestination = null;
                                _lastRoamDirection = null;
                                _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                                await Task.Delay(WalkDelay);
                                continue;
                            }
                        }
                    }

                    if (_currentRoamPath != null && _currentRoamPath.Count > 0)
                    {
                        bool moved = await MoveAlongPathAsync(_currentRoamPath, _searchDestination.Value);
                        if (!moved)
                        {
                            _currentRoamPath = null;
                            _searchDestination = null;
                            _lastRoamDirection = null;
                            _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                        }
                        else if (_currentRoamPath?.Count <= 1)
                        {
                            _currentRoamPath = null;
                            _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                        }
                    }
                }
                else if (traveling && _searchDestination.HasValue)
                {
                    if (_currentRoamPath == null || _currentRoamPath.Count < 1)
                    {
                        if (DateTime.UtcNow >= _nextPathFindTime)
                        {
                            _currentRoamPath = await FindPathAsync(map, current, _searchDestination.Value, 0, 0);
                            Client.Log($"Travel roam path length {_currentRoamPath.Count}");
                            //_nextPathFindTime = DateTime.UtcNow + TravelPathFindInterval;
                            if (_currentRoamPath.Count == 0)
                            {
                                _travelPath = null;
                                _searchDestination = null;
                                _currentRoamPath = null;
                                _lastRoamDirection = null;
                                _travelDestinationMap = null;
                                _travelPauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                                _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                                Client.Travelling = false;
                                traveling = false;
                            }
                        }
                    }

                    if (traveling && _currentRoamPath != null && _currentRoamPath.Count >= 1)
                    {
                        var roamPath = _currentRoamPath;
                        bool moved = await MoveAlongPathAsync(roamPath, _searchDestination.Value);

                        if (_currentRoamPath == roamPath)
                        {
                            if (!moved)
                            {
                                _currentRoamPath = null;
                                //_nextPathFindTime = DateTime.UtcNow + FailedTravelPathFindDelay;
                            }
                            else if (roamPath.Count <= 1)
                            {
                                _currentRoamPath = null;
                                _nextPathFindTime = DateTime.UtcNow + FailedTravelPathFindDelay;
                            }
                        }
                    }
                }
                if (traveling && _searchDestination.HasValue)
                {
                    if (Client.CurrentLocation == _searchDestination.Value)
                    {
                        if (_travelStuckSince == DateTime.MinValue)
                            _travelStuckSince = DateTime.UtcNow;
                        else if (DateTime.UtcNow - _travelStuckSince > TimeSpan.FromSeconds(5))
                        {
                            var dir = (MirDirection)Random.Next(8);
                            await Client.TurnAsync(dir);
                            _travelStuckSince = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        _travelStuckSince = DateTime.MinValue;
                    }
                }
            }

            if (_sellingItems)
            {
                Client.UpdateAction("selling items");
            }
            else if (_repairingItems)
            {
                Client.UpdateAction("repairing items...");
            }
            else if (_buyingItems)
            {
                Client.UpdateAction("buying items");
            }
            else if (traveling)
            {
                if (!string.IsNullOrEmpty(_travelDestinationMap))
                    Client.UpdateAction($"travelling to {_travelDestinationMap}...");
                else
                    Client.UpdateAction("travelling...");
            }
            else if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster)
            {
                Client.UpdateAction(GetMonsterTargetAction(_currentTarget));
            }
            else
            {
                Client.UpdateAction("roaming...");
            }

            if (Client.CurrentLocation != _lastStationaryLocation)
            {
                _lastStationaryLocation = Client.CurrentLocation;
                _stationarySince = DateTime.UtcNow;
            }
            else if (_stationarySince != DateTime.MinValue &&
                     DateTime.UtcNow - _stationarySince > TimeSpan.FromSeconds(5) &&
                     !traveling &&
                     !_sellingItems && !_repairingItems && !_buyingItems &&
                     !Client.IsProcessingNpc)
            {
                var dir = (MirDirection)Random.Next(8);
                await Client.TurnAsync(dir);
                _stationarySince = DateTime.UtcNow;

                // Reset roaming state if we've been stuck for a while
                _searchDestination = null;
                _currentRoamPath = null;
                _lostTargetLocation = null;
                _lostTargetPath = null;
                _travelPath = null;
                Client.Travelling = false;
                _travelDestinationMap = null;
                _nextPathFindTime = DateTime.MinValue;
                Client.Log("Roaming reset due to inactivity");
            }

            if (DateTime.UtcNow - _lastMoveOrAttackTime > TimeSpan.FromSeconds(60) &&
                DateTime.UtcNow >= _nextTownTeleportTime)
            {
                var teleport = Client.FindTownTeleport();
                if (teleport != null)
                {
                    var mapChange = Client.WaitForMapChangeAsync(waitForNextMap: true);
                    await Client.UseItemAsync(teleport);
                    string name = teleport.Info?.FriendlyName ?? "town teleport";
                    Client.Log($"Used {name} due to inactivity");
                    await mapChange;
                    await Client.RecordSafezoneAsync();
                    _nextTownTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                    _lastMoveOrAttackTime = DateTime.UtcNow;
                }
            }

            await Task.Delay(WalkDelay);
        }
    }

    private async Task<bool> HandleReviveAsync()
    {
        if (!Client.Dead) return false;
        _currentTarget = null;
        Client.UpdateAction("reviving");
        if (!_sentRevive)
        {
            await Client.TownReviveAsync();
            _sentRevive = true;
            TriggerInventoryRefresh();
        }
        await Task.Delay(WalkDelay);
        if (!Client.Dead) _sentRevive = false;
        return true;
    }

    protected void RecordAttackTime()
    {
        _lastMoveOrAttackTime = DateTime.UtcNow;
        _stationarySince = _lastMoveOrAttackTime;
        _nextAttackTime = _lastMoveOrAttackTime + TimeSpan.FromMilliseconds(AttackDelay);
    }

    protected virtual IEnumerable<Spell> GetAttackSpells()
    {
        yield break;
    }

    private int GetMinAttackSpellCost()
    {
        int minCost = int.MaxValue;
        int level = Client.Level;
        foreach (var spell in GetAttackSpells())
        {
            var magic = Client.Magics.FirstOrDefault(m => m.Spell == spell);
            if (magic == null) continue;

            int availableLevel = 0;
            if (level >= magic.Level3) availableLevel = 3;
            else if (level >= magic.Level2) availableLevel = 2;
            else if (level >= magic.Level1) availableLevel = 1;

            int castLevel = Math.Min(magic.Level + 1, availableLevel);
            if (castLevel == 0) continue;

            int cost = magic.BaseCost + magic.LevelCost * (castLevel - 1);
            if (cost < minCost)
                minCost = cost;
        }

        return minCost == int.MaxValue ? 0 : minCost;
    }

    protected ClientMagic? GetMagic(Spell spell)
        => Client.Magics.FirstOrDefault(m => m.Spell == spell);

    protected ClientMagic? GetBestMagic(params Spell[] spells)
    {
        ClientMagic? best = null;
        int bestReq = -1;
        int bestCastLevel = 0;
        int mp = Client.MP;
        int playerLevel = Client.Level;
        foreach (var magic in Client.Magics)
        {
            if (Array.IndexOf(spells, magic.Spell) < 0) continue;

            int availableLevel = 0;
            if (playerLevel >= magic.Level3) availableLevel = 3;
            else if (playerLevel >= magic.Level2) availableLevel = 2;
            else if (playerLevel >= magic.Level1) availableLevel = 1;

            int castLevel = Math.Min(magic.Level + 1, availableLevel);
            if (castLevel == 0) continue;

            int cost = magic.BaseCost + magic.LevelCost * (castLevel - 1);
            if (cost > mp) continue;

            int req = availableLevel switch
            {
                3 => magic.Level3,
                2 => magic.Level2,
                _ => magic.Level1
            };

            if (best == null)
            {
                best = magic;
                bestReq = req;
                bestCastLevel = castLevel;
                continue;
            }

            int bestCost = best.BaseCost + best.LevelCost * (bestCastLevel - 1);
            if (req > bestReq || (req == bestReq && cost > bestCost))
            {
                best = magic;
                bestReq = req;
                bestCastLevel = castLevel;
            }
        }
        return best;
    }

    protected async Task AttackWithSpellAsync(Point current, TrackedObject monster, Spell spell)
    {
        var dir = Functions.DirectionFromPoint(current, monster.Location);
        await Client.AttackAsync(dir, spell);
        RecordAttackTime();
    }

    protected static bool CanCast(MapData map, Point from, Point to)
    {
        Point location = from;
        while (location != to)
        {
            MirDirection dir = Functions.DirectionFromPoint(location, to);
            location = Functions.PointMove(location, dir, 1);
            if (location.X < 0 || location.Y < 0 ||
                location.X >= map.Width || location.Y >= map.Height)
                return false;
            if (!map.IsWalkable(location.X, location.Y))
                return false;
        }
        return true;
    }

    protected HashSet<Point> BuildObstacles(MapData map)
    {
        var obstacles = MovementHelper.BuildObstacles(Client);
        var dirs = new[]
        {
            new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0),
            new Point(1, -1), new Point(1, 1), new Point(-1, 1), new Point(-1, -1)
        };
        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type != ObjectType.Monster || obj.Dead) continue;
            obstacles.Add(obj.Location);
            foreach (var d in dirs)
            {
                var p = new Point(obj.Location.X + d.X, obj.Location.Y + d.Y);
                if (map.IsWalkable(p.X, p.Y))
                    obstacles.Add(p);
            }
        }
        return obstacles;
    }

    protected async Task<List<Point>> FindBufferedPathAsync(MapData map, Point start, Point dest, int radius)
    {
        var obstacles = BuildObstacles(map);
        var path = await PathFinder.FindPathAsync(map, start, dest, obstacles, radius);
        if (path.Count == 0)
            path = await MovementHelper.FindPathAsync(Client, map, start, dest, 0, radius);
        return path;
    }

    protected virtual async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        var dir = Functions.DirectionFromPoint(current, monster.Location);
        await Client.AttackAsync(dir, Spell.None);
        RecordAttackTime();
    }

    private async Task<bool> HandleHarvestingAsync()
    {
        if (!Client.IsHarvesting) return false;

        var current = Client.CurrentLocation;
        if (Client.TryGetNearbyHarvestInterruptingMonster(out var monster, out int dist))
        {
            Client.CancelHarvesting();
            if (monster != null && dist <= 1)
                await AttackTargetAsync(monster, current);
            return false;
        }

        Client.UpdateAction("harvesting");
        await Task.Delay(WalkDelay);
        return true;
    }
}
