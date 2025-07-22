using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using C = ClientPackets;
using S = ServerPackets;
using Shared;
using PlayerAgents.Map;

public partial class GameClient
{
    private readonly Config _config;
    private readonly NpcMemoryBank _npcMemory;
    private readonly MapMovementMemoryBank _movementMemory;
    private bool _suppressNextMovement;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _pingTime;
    private readonly byte[] _buffer = new byte[1024 * 8];
    private byte[] _rawData = Array.Empty<byte>();
    private readonly Random _random = new();
    private MirClass? _playerClass;
    private readonly TaskCompletionSource<MirClass> _classTcs = new();
    private Point _currentLocation = Point.Empty;
    private string _playerName = string.Empty;
    private uint _objectId;
    private string _currentMapFile = string.Empty;
    private PlayerAgents.Map.MapData? _mapData;

    private LightSetting _timeOfDay = LightSetting.Normal;

    private MirGender _gender;
    private ushort _level;
    private int _hp;
    private int _mp;
    private UserItem[]? _inventory;
    private UserItem[]? _equipment;
    private UserItem? _lastPickedItem;
    private uint _gold;

    private uint? _lastAttackTarget;
    private uint? _lastStruckAttacker;

    private bool _dead;

    private DateTime _lastMoveTime = DateTime.MinValue;
    private bool _canRun;

    // store information on nearby objects
    private readonly ConcurrentDictionary<uint, TrackedObject> _trackedObjects = new();

    // Use a dictionary for faster lookups by item index
    public static readonly Dictionary<int, ItemInfo> ItemInfoDict = new();

    private static readonly HashSet<byte> AutoHarvestAIs = new() { 1, 2, 4, 5, 7, 9 };

    private bool _awaitingHarvest;
    private uint? _harvestTargetId;
    private bool _harvestComplete;
    private static readonly TimeSpan HarvestDelay = TimeSpan.FromMilliseconds(600);
    private DateTime _nextHarvestTime = DateTime.MinValue;

    public bool IsHarvesting => _awaitingHarvest;

    private static void Bind(UserItem item)
    {
        if (ItemInfoDict.TryGetValue(item.ItemIndex, out var info))
        {
            item.Info = info;
            for (int i = 0; i < item.Slots.Length; i++)
            {
                if (item.Slots[i] != null)
                    Bind(item.Slots[i]!);
            }
        }
    }

    private static void BindAll(UserItem[]? items)
    {
        if (items == null) return;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] != null)
                Bind(items[i]!);
        }
    }

    public IReadOnlyList<UserItem>? Inventory => _inventory;
    public IReadOnlyList<UserItem>? Equipment => _equipment;

    public bool Dead => _dead;

    public MirClass? PlayerClass => _playerClass;
    public Task<MirClass> WaitForClassAsync() => _classTcs.Task;
    public LightSetting TimeOfDay => _timeOfDay;
    public MapData? CurrentMap => _mapData;
    public IReadOnlyDictionary<uint, TrackedObject> TrackedObjects => _trackedObjects;
    public bool IsMapLoaded => _mapData != null && _mapData.Width > 0 && _mapData.Height > 0;
    public Point CurrentLocation => _currentLocation;
    public long PingTime => _pingTime;
    public uint ObjectId => _objectId;
    public uint Gold => _gold;
    public UserItem? LastPickedItem => _lastPickedItem;
    public int HP => _hp;
    public int MP => _mp;

    public GameClient(Config config, NpcMemoryBank npcMemory, MapMovementMemoryBank movementMemory)
    {
        _config = config;
        _npcMemory = npcMemory;
        _movementMemory = movementMemory;
    }

    private Task RandomStartupDelayAsync() => Task.Delay(_random.Next(1000, 3000));

    public int GetCurrentBagWeight()
    {
        int weight = 0;
        if (_inventory != null)
        {
            foreach (var item in _inventory)
                if (item != null)
                    weight += item.Weight;
        }
        return weight;
    }

    public int GetMaxBagWeight()
    {
        if (_playerClass == null) return int.MaxValue;
        var stats = new BaseStats(_playerClass.Value);
        int baseWeight = stats.Stats.First(s => s.Type == Stat.BagWeight).Calculate(_playerClass.Value, _level);
        int extra = 0;
        if (_equipment != null)
        {
            foreach (var item in _equipment)
            {
                if (item == null || item.Info == null) continue;
                extra += item.Info.Stats[Stat.BagWeight];
                extra += item.AddedStats[Stat.BagWeight];
            }
        }
        return baseWeight + extra;
    }

    public int GetMaxHP()
    {
        if (_playerClass == null) return int.MaxValue;
        var stats = new BaseStats(_playerClass.Value);
        int baseHP = stats.Stats.First(s => s.Type == Stat.HP).Calculate(_playerClass.Value, _level);
        int extra = 0;
        if (_equipment != null)
        {
            foreach (var item in _equipment)
            {
                if (item == null || item.Info == null) continue;
                extra += item.Info.Stats[Stat.HP];
                extra += item.AddedStats[Stat.HP];
            }
        }
        return baseHP + extra;
    }

    public int GetMaxMP()
    {
        if (_playerClass == null) return int.MaxValue;
        var stats = new BaseStats(_playerClass.Value);
        int baseMP = stats.Stats.First(s => s.Type == Stat.MP).Calculate(_playerClass.Value, _level);
        int extra = 0;
        if (_equipment != null)
        {
            foreach (var item in _equipment)
            {
                if (item == null || item.Info == null) continue;
                extra += item.Info.Stats[Stat.MP];
                extra += item.AddedStats[Stat.MP];
            }
        }
        return baseMP + extra;
    }

    public bool HasFreeBagSpace()
    {
        if (_inventory == null) return true;
        for (int i = 0; i < _inventory.Length; i++)
            if (_inventory[i] == null) return true;
        return false;
    }

    public UserItem? FindPotion(bool hpPotion)
    {
        if (_inventory == null) return null;
        foreach (var item in _inventory)
        {
            if (item?.Info == null) continue;
            if (item.Info.Type != ItemType.Potion) continue;

            bool healsHP = item.Info.Stats[Stat.HP] > 0 || item.Info.Stats[Stat.HPRatePercent] > 0;
            bool healsMP = item.Info.Stats[Stat.MP] > 0 || item.Info.Stats[Stat.MPRatePercent] > 0;

            if (hpPotion && healsHP) return item;
            if (!hpPotion && healsMP) return item;
        }
        return null;
    }

    public int GetPotionRestoreAmount(UserItem item, bool hpPotion)
    {
        int max = hpPotion ? GetMaxHP() : GetMaxMP();
        int flat = item.GetTotal(hpPotion ? Stat.HP : Stat.MP);
        int percent = item.GetTotal(hpPotion ? Stat.HPRatePercent : Stat.MPRatePercent);
        return flat + (max * percent) / 100;
    }

    private async Task HarvestLoopAsync(TrackedObject monster)
    {
        _awaitingHarvest = true;
        _harvestTargetId = monster.Id;
        _harvestComplete = false;

        while (!_harvestComplete)
        {
            if (!HasFreeBagSpace() || GetCurrentBagWeight() >= GetMaxBagWeight())
                break;

            if (_trackedObjects.Values.Any(o => o.Type == ObjectType.Monster && !o.Dead &&
                Functions.MaxDistance(_currentLocation, o.Location) <= 1))
            {
                await Task.Delay(HarvestDelay);
                continue;
            }

            if (DateTime.UtcNow < _nextHarvestTime)
                await Task.Delay(_nextHarvestTime - DateTime.UtcNow);

            var dir = Functions.DirectionFromPoint(_currentLocation, monster.Location);
            await HarvestAsync(dir);
            _nextHarvestTime = DateTime.UtcNow + HarvestDelay;

            await Task.Delay(HarvestDelay);
        }

        _awaitingHarvest = false;
        _harvestTargetId = null;
    }
}
