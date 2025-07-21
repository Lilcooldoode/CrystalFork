using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;

public class NpcEntry
{
    public string Name { get; set; } = string.Empty;
    public string MapFile { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
}

public class NpcMemoryBank
{
    private readonly string _path;
    private DateTime _lastWriteTime;
    private readonly List<NpcEntry> _entries = new();
    private readonly object _lock = new();

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
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
        string tmp = _path + ".tmp";
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
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

    public void AddNpc(string name, string mapFile, Point location)
    {
        lock (_lock)
        {
            ReloadIfUpdated();
            var normalized = Path.GetFileNameWithoutExtension(mapFile);
            bool exists = _entries.Any(e => e.Name == name && e.MapFile == normalized && e.X == location.X && e.Y == location.Y);
            if (!exists)
            {
                _entries.Add(new NpcEntry { Name = name, MapFile = normalized, X = location.X, Y = location.Y });
                Save();
            }
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
