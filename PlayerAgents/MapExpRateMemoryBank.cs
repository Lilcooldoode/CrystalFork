using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

public class MapExpRateEntry
{
    public string MapFile { get; set; } = string.Empty;
    public MirClass Class { get; set; }
    public ushort Level { get; set; }
    public double ExpPerHour { get; set; }
}

public class MapExpRateMemoryBank
{
    private readonly string _path;
    private DateTime _lastWriteTime;
    private readonly List<MapExpRateEntry> _entries = new();
    private readonly object _lock = new();
    private static readonly Mutex _fileMutex = new(false, "Global\\MapExpRateMemoryBankMutex");

    public MapExpRateMemoryBank(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        List<MapExpRateEntry>? items = null;
        if (File.Exists(_path))
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            items = JsonSerializer.Deserialize<List<MapExpRateEntry>>(fs);
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

    public IReadOnlyList<MapExpRateEntry> GetAll()
    {
        lock (_lock)
        {
            ReloadIfUpdated();
            return _entries.ToList();
        }
    }
}
