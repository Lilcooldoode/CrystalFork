using ClientPackets;
using Shared;

public class BaseAI
{
    protected readonly GameClient Client;
    protected readonly Random Random = new();

    public BaseAI(GameClient client)
    {
        Client = client;
    }

    protected virtual int WalkDelay => 600;
    protected virtual TimeSpan EquipCheckInterval => TimeSpan.FromSeconds(5);
    private DateTime _nextEquipCheck = DateTime.UtcNow;

    private async Task CheckEquipmentAsync()
    {
        var inventory = Client.Inventory;
        var equipment = Client.Equipment;
        if (inventory == null || equipment == null) return;

        for (int slot = 0; slot < equipment.Count; slot++)
        {
            var equipSlot = (EquipmentSlot)slot;
            UserItem? current = equipment[slot];
            int bestScore = current != null ? Client.GetItemScore(current) : -1;
            UserItem? bestItem = current;

            foreach (var item in inventory)
            {
                if (item == null) continue;
                if (!Client.CanEquipItem(item, equipSlot)) continue;
                int score = Client.GetItemScore(item);
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

            var dir = (MirDirection)Random.Next(0, 8);
            await Client.WalkAsync(dir);
            await Task.Delay(WalkDelay);
        }
    }
}
