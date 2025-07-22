using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public sealed class MapExpRateEntry
{
    public string MapFile { get; set; } = string.Empty;
    public MirClass Class { get; set; }
    public ushort Level { get; set; }
    public double ExpPerHour { get; set; }
}

public sealed class MapExpRateMemoryBank : MemoryBankBase<MapExpRateEntry>
{
    public MapExpRateMemoryBank(string path) : base(path, "Global\\MapExpRateMemoryBankMutex")
    {
    }

    public void AddRate(string mapFile, MirClass playerClass, ushort level, double expPerHour)
    {
        bool added = false;
        lock (_lock)
        {
            ReloadIfUpdated();
            var normalized = Path.GetFileNameWithoutExtension(mapFile);
            var existing = _entries.FirstOrDefault(e => e.MapFile == normalized && e.Class == playerClass && e.Level == level);
            if (existing != null)
            {
                if (expPerHour > existing.ExpPerHour)
                {
                    existing.ExpPerHour = expPerHour;
                    added = true;
                }
            }
            else
            {
                _entries.Add(new MapExpRateEntry { MapFile = normalized, Class = playerClass, Level = level, ExpPerHour = expPerHour });
                added = true;
            }
        }

        if (added)
            Save();
    }
}
