using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Shared;

public class NpcEntry
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

public class NpcMemoryBank
{
    private readonly string _path;
    private DateTime _lastWriteTime;
    private readonly List<NpcEntry> _entries = new();
    private readonly object _lock = new();
    private static readonly Mutex _fileMutex = new(false, "Global\\NpcMemoryBankMutex");

    public NpcMemoryBank(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        List<NpcEntry>? items = null;
        if (File.Exists(_path))
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            items = JsonSerializer.Deserialize<List<NpcEntry>>(fs);
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

    public void SaveChanges()
    {
        lock (_lock)
        {
            Save();
        }
    }

    public IReadOnlyList<NpcEntry> GetAll()
    {
        lock (_lock)
        {
            ReloadIfUpdated();
            return _entries.ToList();
        }
    }
}
