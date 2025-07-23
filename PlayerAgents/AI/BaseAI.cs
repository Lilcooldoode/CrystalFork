using ClientPackets;
using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

public class BaseAI
{
    protected readonly GameClient Client;
    protected static readonly Random Random = new();
    private TrackedObject? _currentTarget;
    private Point? _searchDestination;
    private Point? _lostTargetLocation;
    private DateTime _nextTargetSwitchTime = DateTime.MinValue;

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

    // Monsters with these AI values are ignored when selecting a target
    protected static readonly HashSet<byte> IgnoredAIs = new() { 6, 58, 57, 56, 64 };

    protected static bool IsOffensiveSlot(EquipmentSlot slot) => OffensiveSlots.Contains(slot);

    public BaseAI(GameClient client)
    {
        Client = client;
    }

    protected virtual int WalkDelay => 600;
    protected virtual int AttackDelay => 1400;
    protected virtual TimeSpan EquipCheckInterval => TimeSpan.FromSeconds(5);
    protected virtual IReadOnlyList<DesiredItem> DesiredItems => Array.Empty<DesiredItem>();
    private DateTime _nextEquipCheck = DateTime.UtcNow;
    private DateTime _nextAttackTime = DateTime.UtcNow;
    private DateTime _nextPotionTime = DateTime.MinValue;
    
    private readonly Dictionary<(Point Location, string Name), DateTime> _itemRetryTimes = new();
    private static readonly TimeSpan ItemRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DroppedItemRetryDelay = TimeSpan.FromMinutes(5);
    private bool _sentRevive;
    private bool _sellingItems;

    protected virtual int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        int score = 0;
        if (item.Info != null)
            score += item.Info.Stats.Count;
        if (item.AddedStats != null)
            score += item.AddedStats.Count;
        return score;
    }

    private static Point GetRandomPoint(PlayerAgents.Map.MapData map, Random random)
    {
        var cells = map.WalkableCells;
        if (cells.Count == 0)
            return new Point(0, 0);
        return cells[random.Next(cells.Count)];
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
                    Console.WriteLine($"I have equipped {bestItem.Info.FriendlyName}");
            }
        }

        // handle torch based on time of day
        var torchSlot = EquipmentSlot.Torch;
        UserItem? currentTorch = equipment.Count > (int)torchSlot ? equipment[(int)torchSlot] : null;
        if (Client.TimeOfDay == LightSetting.Night)
        {
            UserItem? bestTorch = GetBestItemForSlot(torchSlot, available, currentTorch);
            if (bestTorch != null && bestTorch != currentTorch)
            {
                await Client.EquipItemAsync(bestTorch, torchSlot);
                int idx = available.IndexOf(bestTorch);
                if (idx >= 0) available[idx] = null;
                if (bestTorch.Info != null)
                    Console.WriteLine($"I have equipped {bestTorch.Info.FriendlyName}");
            }
        }
        else if (currentTorch != null)
        {
            if (currentTorch.Info != null)
                Console.WriteLine($"I have unequipped {currentTorch.Info.FriendlyName}");
            await Client.UnequipItemAsync(torchSlot);
        }
    }

    private async Task TryUsePotionsAsync()
    {
        if (DateTime.UtcNow < _nextPotionTime) return;

        int maxHP = Client.GetMaxHP();
        int maxMP = Client.GetMaxMP();

        if (Client.HP < maxHP)
        {
            var pot = Client.FindPotion(true);
            if (pot != null)
            {
                int heal = Client.GetPotionRestoreAmount(pot, true);
                if (heal > 0 && maxHP - Client.HP >= heal)
                {
                    await Client.UseItemAsync(pot);
                    string name = pot.Info?.FriendlyName ?? "HP potion";
                    Console.WriteLine($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return;
                }
            }
        }

        if (Client.MP < maxMP)
        {
            var pot = Client.FindPotion(false);
            if (pot != null)
            {
                int heal = Client.GetPotionRestoreAmount(pot, false);
                if (heal > 0 && maxMP - Client.MP >= heal)
                {
                    await Client.UseItemAsync(pot);
                    string name = pot.Info?.FriendlyName ?? "MP potion";
                    Console.WriteLine($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }
        }
    }

    private TrackedObject? FindClosestTarget(Point current, out int bestDist)
    {
        TrackedObject? closestMonster = null;
        int monsterDist = int.MaxValue;
        TrackedObject? closestItem = null;
        int itemDist = int.MaxValue;

        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type == ObjectType.Monster)
            {
                if (obj.Dead) continue;
                if (IgnoredAIs.Contains(obj.AI)) continue;
                if (obj.EngagedWith.HasValue && obj.EngagedWith.Value != Client.ObjectId &&
                    DateTime.UtcNow - obj.LastEngagedTime < TimeSpan.FromSeconds(5))
                    continue;
                int dist = Functions.MaxDistance(current, obj.Location);
                if (dist < monsterDist)
                {
                    monsterDist = dist;
                    closestMonster = obj;
                }
            }
            else if (obj.Type == ObjectType.Item)
            {
                if (_itemRetryTimes.TryGetValue((obj.Location, obj.Name), out var retry) && DateTime.UtcNow < retry)
                    continue;
                int dist = Functions.MaxDistance(current, obj.Location);
                if (dist < itemDist)
                {
                    itemDist = dist;
                    closestItem = obj;
                }
            }
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

    private HashSet<Point> BuildObstacles(uint ignoreId = 0)
    {
        var obstacles = new HashSet<Point>(Client.BlockingCells);
        if (ignoreId != 0 && Client.TrackedObjects.TryGetValue(ignoreId, out var obj))
            obstacles.Remove(obj.Location);
        return obstacles;
    }

    private async Task<List<Point>> FindPathAsync(PlayerAgents.Map.MapData map, Point start, Point dest, uint ignoreId = 0, int radius = 1)
    {
        try
        {
            var obstacles = BuildObstacles(ignoreId);
            return await PlayerAgents.Map.PathFinder.FindPathAsync(map, start, dest, obstacles, radius);
        }
        catch
        {
            return new List<Point>();
        }
    }

    private async Task MoveAlongPathAsync(List<Point> path, Point destination, Point current)
    {
        if (path.Count > 2)
        {
            var first = path[1];
            var dir = Functions.DirectionFromPoint(current, first);
            if (Functions.PointMove(current, dir, 2) == path[2] && Client.CanRun(dir))
                await Client.RunAsync(dir);
            else if (Client.CanWalk(dir))
                await Client.WalkAsync(dir);
        }
        else if (path.Count > 1)
        {
            var dir = Functions.DirectionFromPoint(current, path[1]);
            if (Client.CanWalk(dir))
                await Client.WalkAsync(dir);
        }
        else if (path.Count == 1)
        {
            var dir = Functions.DirectionFromPoint(current, destination);
            if (Client.CanWalk(dir))
                await Client.WalkAsync(dir);
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

    private HashSet<UserItem> GetItemsToKeep(IEnumerable<UserItem> inventory)
    {
        var keep = new HashSet<UserItem>();
        int maxWeight = Client.GetMaxBagWeight();

        foreach (var desired in DesiredItems)
        {
            var matching = inventory.Where(i => MatchesDesiredItem(i, desired)).ToList();

            if (desired.Count.HasValue)
            {
                foreach (var item in matching.OrderByDescending(i => i.Weight).Take(desired.Count.Value))
                    keep.Add(item);
            }

            if (desired.WeightFraction > 0)
            {
                int requiredWeight = (int)Math.Ceiling(maxWeight * desired.WeightFraction);
                int current = matching.Where(i => keep.Contains(i)).Sum(i => i.Weight);
                foreach (var item in matching.Where(i => !keep.Contains(i)).OrderByDescending(i => i.Weight))
                {
                    if (current >= requiredWeight) break;
                    keep.Add(item);
                    current += item.Weight;
                }
            }
        }

        return keep;
    }

    private async Task HandleInventoryAsync()
    {
        if (_sellingItems) return;
        var inventory = Client.Inventory;
        if (inventory == null) return;

        bool full = !Client.HasFreeBagSpace();
        bool heavy = Client.GetCurrentBagWeight() >= Client.GetMaxBagWeight() * 0.9;
        if (!full && !heavy) return;

        var items = inventory.Where(i => i != null && i.Info != null).ToList();
        var keepSet = GetItemsToKeep(items);
        var groups = items.Where(i => !keepSet.Contains(i))
            .GroupBy(i => i!.Info!.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

        _sellingItems = true;
        Client.UpdateAction("selling items");
        Client.IgnoreNpcInteractions = true;
        while (groups.Count > 0)
        {
            var types = groups.Keys.ToList();
            if (!Client.TryFindNearestNpc(types, out var npcId, out var loc, out var entry, out var matchedTypes))
                break;

            int count = matchedTypes.Sum(t => groups[t].Count);
            Console.WriteLine($"Heading to {entry?.Name ?? "unknown npc"} at {loc.X},{loc.Y} to sell {count} items");

            var map = Client.CurrentMap;
            if (map == null) break;
            bool foundPath = true;
            while (Functions.MaxDistance(Client.CurrentLocation, loc) > 6)
            {
                var path = await FindPathAsync(map, Client.CurrentLocation, loc, npcId, 6);
                if (path.Count == 0)
                {
                    Console.WriteLine($"Could not path to {entry?.Name ?? npcId.ToString()}");
                    foundPath = false;
                    break;
                }
                await MoveAlongPathAsync(path, loc, Client.CurrentLocation);
                await Task.Delay(WalkDelay);
                map = Client.CurrentMap;
                if (map == null) break;
            }

            if (Functions.MaxDistance(Client.CurrentLocation, loc) <= 6)
            {
                if (npcId == 0)
                    Client.TryFindNearestNpc(types, out npcId, out _, out entry, out matchedTypes);

                if (npcId == 0 || entry != null)
                    npcId = await Client.ResolveNpcIdAsync(entry);

                if (npcId != 0)
                {
                    var sellItems = matchedTypes.SelectMany(t => groups[t]).Where(i => i != null).ToList();
                    await Client.SellItemsToNpcAsync(npcId, sellItems);
                    Console.WriteLine($"Finished selling to {entry?.Name ?? npcId.ToString()}");
                    foreach (var t in matchedTypes)
                        groups.Remove(t);
                }
                else
                {
                    Console.WriteLine($"Could not find NPC to sell items");
                    break;
                }
            }

            if (!foundPath) break; // resume normal behaviour if we cannot reach npc
        }
        Client.IgnoreNpcInteractions = false;
        Client.ResumeNpcInteractions();
        _sellingItems = false;
        Client.UpdateAction("roaming...");
    }

    public virtual async Task RunAsync()
    {
        Point current;
        while (true)
        {
            if (await HandleReviveAsync())
                continue;

            if (await HandleHarvestingAsync())
                continue;

            Client.ProcessMapExpRateInterval();
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

            await TryUsePotionsAsync();

            await HandleInventoryAsync();

            if (Client.GetCurrentBagWeight() > Client.GetMaxBagWeight() && Client.LastPickedItem != null)
            {
                Console.WriteLine("Overweight detected, dropping last picked item");
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
                            _itemRetryTimes[(loc, drop.Info.FriendlyName)] = DateTime.UtcNow + DroppedItemRetryDelay;
                        }
                    }
                }
            }

            foreach (var kv in _itemRetryTimes.ToList())
                if (DateTime.UtcNow >= kv.Value)
                    _itemRetryTimes.Remove(kv.Key);

            var map = Client.CurrentMap;
            if (map == null || !Client.IsMapLoaded)
            {
                await Task.Delay(WalkDelay);
                continue;
            }

            current = Client.CurrentLocation;
            if (_currentTarget != null && !Client.TrackedObjects.ContainsKey(_currentTarget.Id))
            {
                _lostTargetLocation = _currentTarget.Location;
                _currentTarget = null;
            }
            if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster)
            {
                if (_currentTarget.Dead ||
                    (_currentTarget.EngagedWith.HasValue && _currentTarget.EngagedWith.Value != Client.ObjectId))
                    _nextTargetSwitchTime = DateTime.MinValue;
            }
            int distance;
            var closest = FindClosestTarget(current, out distance);

            if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster &&
                !_currentTarget.Dead &&
                (_currentTarget.EngagedWith == null || _currentTarget.EngagedWith.Value == Client.ObjectId) &&
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
                _lostTargetLocation = null;
                if (_currentTarget?.Id != closest.Id)
                {
                    Console.WriteLine($"I have targeted {closest.Name} at {closest.Location.X}, {closest.Location.Y}");
                    _currentTarget = closest;
                    if (closest.Type == ObjectType.Monster)
                        _nextTargetSwitchTime = DateTime.UtcNow + TargetSwitchInterval;
                }

                if (closest.Type == ObjectType.Item)
                {
                    if (distance > 0)
                    {
                        var path = await FindPathAsync(map, current, closest.Location, closest.Id, 0);
                        await MoveAlongPathAsync(path, closest.Location, current);
                    }
                    else
                    {
                        if (Client.HasFreeBagSpace() && Client.GetCurrentBagWeight() < Client.GetMaxBagWeight())
                        {
                            await Client.PickUpAsync();
                        }
                        _itemRetryTimes[(closest.Location, closest.Name)] = DateTime.UtcNow + ItemRetryDelay;
                        _currentTarget = null;
                    }
                }
                else
                {
                    if (distance > 1)
                    {
                        var path = await FindPathAsync(map, current, closest.Location, closest.Id);
                        await MoveAlongPathAsync(path, closest.Location, current);
                    }
                    else if (DateTime.UtcNow >= _nextAttackTime)
                    {
                        var dir = Functions.DirectionFromPoint(current, closest.Location);
                        await Client.AttackAsync(dir);
                        _nextAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
                    }
                }
            }
            else
            {
                _currentTarget = null;
                if (_lostTargetLocation.HasValue)
                {
                    if (Functions.MaxDistance(current, _lostTargetLocation.Value) <= 0)
                    {
                        _lostTargetLocation = null;
                    }
                    else
                    {
                        var path = await FindPathAsync(map, current, _lostTargetLocation.Value);
                        if (path.Count > 0)
                        {
                            await MoveAlongPathAsync(path, _lostTargetLocation.Value, current);
                        }
                        else
                        {
                            _lostTargetLocation = null;
                        }
                    }
                }

                if (!_lostTargetLocation.HasValue)
                {
                    if (_searchDestination == null ||
                        Functions.MaxDistance(current, _searchDestination.Value) <= 1 ||
                        !map.IsWalkable(_searchDestination.Value.X, _searchDestination.Value.Y))
                    {
                        _searchDestination = GetRandomPoint(map, Random);
                        Console.WriteLine($"No targets nearby, searching at {_searchDestination.Value.X}, {_searchDestination.Value.Y}");
                    }

                    var path = await FindPathAsync(map, current, _searchDestination.Value);
                    if (path.Count == 0)
                    {
                        _searchDestination = GetRandomPoint(map, Random);
                        await Task.Delay(WalkDelay);
                        continue;
                    }
                    await MoveAlongPathAsync(path, _searchDestination.Value, current);
                }
            }

            if (_sellingItems)
            {
                Client.UpdateAction("selling items");
            }
            else if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster)
            {
                Client.UpdateAction($"attacking {_currentTarget.Name}");
            }
            else
            {
                Client.UpdateAction("roaming...");
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
        }
        await Task.Delay(WalkDelay);
        if (!Client.Dead) _sentRevive = false;
        return true;
    }

    private async Task<bool> HandleHarvestingAsync()
    {
        if (!Client.IsHarvesting) return false;
        Client.UpdateAction("harvesting");
        var current = Client.CurrentLocation;
        var target = FindClosestTarget(current, out int dist);
        if (target != null && target.Type == ObjectType.Monster && dist <= 1)
        {
            if (DateTime.UtcNow >= _nextAttackTime)
            {
                var dir = Functions.DirectionFromPoint(current, target.Location);
                await Client.AttackAsync(dir);
                _nextAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
            }
        }
        await Task.Delay(WalkDelay);
        return true;
    }
}
