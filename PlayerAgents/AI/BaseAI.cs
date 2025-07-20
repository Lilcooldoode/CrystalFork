using ClientPackets;
using Shared;
using System.Collections.Generic;
using System.Drawing;

public class BaseAI
{
    protected readonly GameClient Client;
    protected readonly Random Random = new();
    private TrackedObject? _currentTarget;

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
    protected virtual TimeSpan EquipCheckInterval => TimeSpan.FromSeconds(5);
    private DateTime _nextEquipCheck = DateTime.UtcNow;

    protected virtual int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        int score = 0;
        if (item.Info != null)
            score += item.Info.Stats.Count;
        if (item.AddedStats != null)
            score += item.AddedStats.Count;
        return score;
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
            int bestScore = current != null ? GetItemScore(current, equipSlot) : -1;
            UserItem? bestItem = current;

            foreach (var item in inventory)
            {
                if (item == null) continue;
                if (!Client.CanEquipItem(item, equipSlot)) continue;
                int score = GetItemScore(item, equipSlot);
                if (bestItem == null || score > bestScore)
                {
                    bestItem = item;
                    bestScore = score;
                }
            }

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
            int bestScore = currentTorch != null ? GetItemScore(currentTorch, torchSlot) : -1;
            UserItem? bestTorch = currentTorch;

            foreach (var item in inventory)
            {
                if (item == null) continue;
                if (!Client.CanEquipItem(item, torchSlot)) continue;
                int score = GetItemScore(item, torchSlot);
                if (bestTorch == null || score > bestScore)
                {
                    bestTorch = item;
                    bestScore = score;
                }
            }

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

    public virtual async Task RunAsync()
    {
        while (true)
        {
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

            TrackedObject? closest = null;
            int bestDist = int.MaxValue;

            foreach (var obj in Client.TrackedObjects.Values)
            {
                if (obj.Type != ObjectType.Monster) continue;
                if (obj.Dead) continue;
                if (IgnoredAIs.Contains(obj.AI)) continue;
                int dist = Functions.MaxDistance(current, obj.Location);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    closest = obj;
                }
            }

            if (closest != null)
            {
                if (_currentTarget?.Id != closest.Id)
                {
                    Console.WriteLine($"I have targeted {closest.Name} at {closest.Location.X}, {closest.Location.Y}");
                    _currentTarget = closest;
                }

                if (bestDist > 1)
                {
                    List<Point> path = new();
                    try
                    {
                        path = await PlayerAgents.Map.PathFinder.FindPathAsync(map, current, closest.Location);
                    }
                    catch
                    {
                        // ignore pathing errors
                    }

                    if (path.Count > 1)
                    {
                        var next = path[1];
                        var dir = Functions.DirectionFromPoint(current, next);
                        await Client.WalkAsync(dir);
                    }
                    else if (path.Count == 1)
                    {
                        var dir = Functions.DirectionFromPoint(current, closest.Location);
                        await Client.WalkAsync(dir);
                    }

                }
            }

            await Task.Delay(WalkDelay);
        }
    }
}
