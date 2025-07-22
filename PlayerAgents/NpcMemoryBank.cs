using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Shared;

public sealed class NpcEntry
{
    public string Name { get; set; } = string.Empty;
    public string MapFile { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public bool CanBuy { get; set; }
    public bool CanSell { get; set; }
    public bool CanRepair { get; set; }
    public List<int>? BuyItemIndexes { get; set; }
    public List<ItemType>? SellItemTypes { get; set; }
    public List<ItemType>? CannotSellItemTypes { get; set; }
    public List<ItemType>? RepairItemTypes { get; set; }
    public List<ItemType>? CannotRepairItemTypes { get; set; }
}

public sealed class NpcMemoryBank : MemoryBankBase<NpcEntry>
{
    public NpcMemoryBank(string path) : base(path, "Global\\NpcMemoryBankMutex")
    {
    }

    public NpcEntry AddNpc(string name, string mapFile, Point location)
    {
        bool added = false;
        NpcEntry? entry = null;
        lock (_lock)
        {
            ReloadIfUpdated();
            var normalized = Path.GetFileNameWithoutExtension(mapFile);
            entry = _entries.FirstOrDefault(e => e.Name == name && e.MapFile == normalized && e.X == location.X && e.Y == location.Y);
            if (entry == null)
            {
                entry = new NpcEntry { Name = name, MapFile = normalized, X = location.X, Y = location.Y };
                _entries.Add(entry);
                added = true;
            }
        }

        if (added)
            Save();

        return entry!;
    }
}
