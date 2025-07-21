using ClientPackets;
using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

public class BaseAI
{
    protected readonly GameClient Client;
    protected readonly Random Random = new();
    private TrackedObject? _currentTarget;
    private Point? _searchDestination;

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
    private DateTime _nextEquipCheck = DateTime.UtcNow;
    private DateTime _nextAttackTime = DateTime.UtcNow;

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

        for (int slot = 0; slot < equipment.Count; slot++)
        {
            var equipSlot = (EquipmentSlot)slot;
            if (equipSlot == EquipmentSlot.Torch) continue;
            UserItem? current = equipment[slot];
            UserItem? bestItem = GetBestItemForSlot(equipSlot, inventory, current);

            if (bestItem != null && bestItem != current)
            {
                await Client.EquipItemAsync(bestItem, equipSlot);
                if (bestItem.Info != null)
                    Console.WriteLine($"I have equipped {bestItem.Info.FriendlyName}");
            }
        }

        // handle torch based on time of day
        var torchSlot = EquipmentSlot.Torch;
        UserItem? currentTorch = equipment.Count > (int)torchSlot ? equipment[(int)torchSlot] : null;
        if (Client.TimeOfDay == LightSetting.Night)
        {
            UserItem? bestTorch = GetBestItemForSlot(torchSlot, inventory, currentTorch);
            if (bestTorch != null && bestTorch != currentTorch)
            {
                await Client.EquipItemAsync(bestTorch, torchSlot);
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

    private TrackedObject? FindClosestTarget(Point current, out int bestDist)
    {
        TrackedObject? closest = null;
        bestDist = int.MaxValue;
        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type != ObjectType.Monster) continue;
            if (obj.Dead) continue;
            if (IgnoredAIs.Contains(obj.AI)) continue;
            if (obj.EngagedWith.HasValue && obj.EngagedWith.Value != Client.ObjectId &&
                DateTime.UtcNow - obj.LastEngagedTime < TimeSpan.FromSeconds(5))
                continue;
            int dist = Functions.MaxDistance(current, obj.Location);
            if (dist < bestDist)
            {
                bestDist = dist;
                closest = obj;
            }
        }
        return closest;
    }

    private HashSet<Point> BuildObstacles(uint ignoreId = 0)
    {
        var obstacles = new HashSet<Point>();
        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Id == ignoreId || obj.Id == Client.ObjectId) continue;
            if (obj.Dead) continue;
            if (obj.Type == ObjectType.Player || obj.Type == ObjectType.Monster || obj.Type == ObjectType.Merchant)
                obstacles.Add(obj.Location);
        }
        return obstacles;
    }

    private async Task<List<Point>> FindPathAsync(PlayerAgents.Map.MapData map, Point start, Point dest, uint ignoreId = 0)
    {
        try
        {
            var obstacles = BuildObstacles(ignoreId);
            return await PlayerAgents.Map.PathFinder.FindPathAsync(map, start, dest, obstacles);
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

    public virtual async Task RunAsync()
    {
        bool sentRevive = false;
        while (true)
        {
            if (Client.Dead)
            {
                _currentTarget = null;
                if (!sentRevive)
                {
                    await Client.TownReviveAsync();
                    sentRevive = true;
                }
                await Task.Delay(WalkDelay);
                if (!Client.Dead) sentRevive = false;
                continue;
            }

            if (DateTime.UtcNow >= _nextEquipCheck)
            {
                await CheckEquipmentAsync();
                _nextEquipCheck = DateTime.UtcNow + EquipCheckInterval;
            }

            var map = Client.CurrentMap;
            if (map == null || !Client.IsMapLoaded)
            {
                await Task.Delay(WalkDelay);
                continue;
            }

            var current = Client.CurrentLocation;
            int distance;
            var closest = FindClosestTarget(current, out distance);

            if (closest != null)
            {
                if (_currentTarget?.Id != closest.Id)
                {
                    Console.WriteLine($"I have targeted {closest.Name} at {closest.Location.X}, {closest.Location.Y}");
                    _currentTarget = closest;
                }

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
            else
            {
                _currentTarget = null;
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

            await Task.Delay(WalkDelay);
        }
    }
}
