using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

public class MapMovementEntry
{
    public string SourceMap { get; set; } = string.Empty;
    public int SourceX { get; set; }
    public int SourceY { get; set; }
    public string DestinationMap { get; set; } = string.Empty;
    public int DestinationX { get; set; }
    public int DestinationY { get; set; }
}

public class MapMovementMemoryBank
{
    private readonly string _path;
    private DateTime _lastWriteTime;
    private readonly List<MapMovementEntry> _entries = new();
    private readonly object _lock = new();
    private static readonly Mutex _fileMutex = new(false, "Global\\MapMovementMemoryBankMutex");

    public MapMovementMemoryBank(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        List<MapMovementEntry>? items = null;
        if (File.Exists(_path))
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            items = JsonSerializer.Deserialize<List<MapMovementEntry>>(fs);
            _lastWriteTime = File.GetLastWriteTimeUtc(_path);
        }

        lock (_lock)
        {
            _entries.Clear();
            if (items != null)
                _entries.AddRange(items);
        }
    }

    private void Save()
    {
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
        }

        _fileMutex.WaitOne();
        try
        {
            string tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(json);
            }
            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);
            _lastWriteTime = File.GetLastWriteTimeUtc(_path);
        }
        finally
        {
            _fileMutex.ReleaseMutex();
        }
    }

    private void ReloadIfUpdated()
    {
        if (!File.Exists(_path)) return;
        var time = File.GetLastWriteTimeUtc(_path);
        if (time > _lastWriteTime)
        {
            Load();
        }
    }

    public void CheckForUpdates() => ReloadIfUpdated();

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

    public IReadOnlyList<MapMovementEntry> GetAll()
    {
        lock (_lock)
        {
            ReloadIfUpdated();
            return _entries.ToList();
        }
    }
}
