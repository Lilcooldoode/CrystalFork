using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

public class MapMovementEntry
{
    public string SourceMap { get; set; } = string.Empty;
    public int SourceX { get; set; }
    public int SourceY { get; set; }
    public string DestinationMap { get; set; } = string.Empty;
    public int DestinationX { get; set; }
    public int DestinationY { get; set; }
}

public class MapMovementMemoryBank : MemoryBankBase<MapMovementEntry>
{
    public MapMovementMemoryBank(string path) : base(path, "Global\\MapMovementMemoryBankMutex")
    {
    }

    public void AddMovement(string sourceMapFile, Point sourceLocation, string destinationMapFile, Point destinationLocation)
    {
        bool added = false;
        lock (_lock)
        {
            ReloadIfUpdated();
            var src = Path.GetFileNameWithoutExtension(sourceMapFile);
            var dest = Path.GetFileNameWithoutExtension(destinationMapFile);
            bool exists = _entries.Any(e => e.SourceMap == src && e.SourceX == sourceLocation.X && e.SourceY == sourceLocation.Y &&
                                           e.DestinationMap == dest && e.DestinationX == destinationLocation.X && e.DestinationY == destinationLocation.Y);
            if (!exists)
            {
                _entries.Add(new MapMovementEntry
                {
                    SourceMap = src,
                    SourceX = sourceLocation.X,
                    SourceY = sourceLocation.Y,
                    DestinationMap = dest,
                    DestinationX = destinationLocation.X,
                    DestinationY = destinationLocation.Y
                });
                added = true;
            }
        }

        if (added)
            Save();
    }
}
