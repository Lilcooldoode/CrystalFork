using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using C = ClientPackets;
using S = ServerPackets;
using Shared;
using PlayerAgents.Map;

public sealed partial class GameClient
{
    private readonly Config _config;
    private readonly NpcMemoryBank _npcMemory;
    private readonly MapMovementMemoryBank _movementMemory;
    private readonly MapExpRateMemoryBank _expRateMemory;
    private readonly IAgentLogger? _logger;
    private bool _suppressNextMovement;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _pingTime;
    private readonly byte[] _buffer = new byte[1024 * 8];
    private readonly MemoryStream _receiveStream = new();
    private readonly Random _random = new();
    private MirClass? _playerClass;
    private BaseStats? _baseStats;
    private readonly TaskCompletionSource<MirClass> _classTcs = new();
    private Point _currentLocation = Point.Empty;
    private string _playerName = string.Empty;
    private string _currentAction = string.Empty;
    private uint _objectId;
    private string _currentMapFile = string.Empty;
    private string _currentMapName = string.Empty;
    private PlayerAgents.Map.MapData? _mapData;

    public string PlayerName => string.IsNullOrEmpty(_playerName) ? _config.CharacterName : _playerName;
    public string CurrentAction => _currentAction;
    public ushort Level => _level;
    public string CurrentMapFile => _currentMapFile;
    public string CurrentMapName => _currentMapName;

    private LightSetting _timeOfDay = LightSetting.Normal;

    private MirGender _gender;
    private ushort _level;
    private long _experience;
    private int _hp;
    private int _mp;
    private UserItem[]? _inventory;
    private UserItem[]? _equipment;
    private UserItem? _lastPickedItem;
    private uint _gold;

    private int _cachedMaxBagWeight;
    private int _cachedMaxHP;
    private int _cachedMaxMP;
    private bool _statsDirty = true;

    private uint? _lastAttackTarget;
    private uint? _lastStruckAttacker;

    private bool _dead;

    private DateTime _lastMoveTime = DateTime.MinValue;
    private bool _canRun;

    private DateTime _mapStartTime = DateTime.MinValue;
    private long _mapStartExp;
    private ushort _mapStartLevel;
    private MirClass? _mapStartClass;
    private long _mapExpGained;

    // store information on nearby objects
    private readonly ConcurrentDictionary<uint, TrackedObject> _trackedObjects = new();
    private readonly ConcurrentDictionary<System.Drawing.Point, int> _blockingCells = new();

    private static bool IsBlocking(TrackedObject obj) =>
        !obj.Dead && (obj.Type == ObjectType.Player || obj.Type == ObjectType.Monster || obj.Type == ObjectType.Merchant);

    private void AddTrackedObject(TrackedObject obj)
    {
        _trackedObjects[obj.Id] = obj;
        if (IsBlocking(obj))
            _blockingCells.AddOrUpdate(obj.Location, 1, (_, v) => v + 1);
    }

    private void UpdateTrackedObject(uint id, Point newLoc, MirDirection dir)
    {
        if (_trackedObjects.TryGetValue(id, out var obj))
        {
            var oldLoc = obj.Location;
            obj.Location = newLoc;
            obj.Direction = dir;
            if (IsBlocking(obj) && oldLoc != newLoc)
            {
                _blockingCells.AddOrUpdate(newLoc, 1, (_, v) => v + 1);
                if (_blockingCells.AddOrUpdate(oldLoc, 0, (_, v) => Math.Max(0, v - 1)) == 0)
                    _blockingCells.TryRemove(oldLoc, out _);
            }
        }
    }

    private void RemoveTrackedObject(uint id)
    {
        if (_trackedObjects.TryRemove(id, out var obj))
        {
            if (IsBlocking(obj))
            {
                var oldLoc = obj.Location;
                if (_blockingCells.AddOrUpdate(oldLoc, 0, (_, v) => Math.Max(0, v - 1)) == 0)
                    _blockingCells.TryRemove(oldLoc, out _);
            }
        }
    }

    private readonly ConcurrentDictionary<uint, NpcEntry> _npcEntries = new();
    private uint? _dialogNpcId;
    private readonly Queue<uint> _npcQueue = new();
    private readonly Queue<(string key, Func<Task> action)> _npcActionTasks = new();
    private bool _processingNpcAction;
    private DateTime _npcInteractionStart;
    private bool _skipNextGoods;
    public bool IsProcessingNpc => _dialogNpcId.HasValue;

    private NPCInteraction? _npcInteraction;

    private readonly Dictionary<ulong, (NpcEntry entry, ItemType type)> _pendingSellChecks = new();
    private readonly Dictionary<ulong, (NpcEntry entry, ItemType type)> _pendingRepairChecks = new();

    private TaskCompletionSource<S.NPCResponse>? _npcResponseTcs;
    private TaskCompletionSource<bool>? _npcGoodsTcs;
    private TaskCompletionSource<bool>? _npcSellTcs;
    private TaskCompletionSource<bool>? _npcRepairTcs;
    private readonly Dictionary<ulong, TaskCompletionSource<S.SellItem>> _sellItemTcs = new();
    private readonly Dictionary<ulong, TaskCompletionSource<S.RepairItem>> _repairItemTcs = new();
    private const int NpcResponseDebounceMs = 100;

    private List<UserItem>? _lastNpcGoods;
    private PanelType _lastNpcGoodsType;


    // Use a dictionary for faster lookups by item index
    public static readonly Dictionary<int, ItemInfo> ItemInfoDict = new();

    private static readonly HashSet<byte> AutoHarvestAIs = new() { 1, 2, 4, 5, 7, 9 };

    private bool _awaitingHarvest;
    private uint? _harvestTargetId;
    private bool _harvestComplete;
    private static readonly TimeSpan HarvestDelay = TimeSpan.FromMilliseconds(600);
    private DateTime _nextHarvestTime = DateTime.MinValue;

    public bool IsHarvesting => _awaitingHarvest;
    public bool IgnoreNpcInteractions { get; set; }

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

    private static int DefaultItemScore(UserItem item, EquipmentSlot slot)
    {
        int score = 0;
        if (item.Info != null)
            score += item.Info.Stats.Count;
        if (item.AddedStats != null)
            score += item.AddedStats.Count;
        return score;
    }

    private int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        if (ItemScoreFunc != null)
            return ItemScoreFunc(item, slot);
        return DefaultItemScore(item, slot);
    }

    private static bool MatchesDesiredItem(UserItem item, DesiredItem desired)
    {
        if (item.Info == null) return false;
        if (item.Info.Type != desired.Type) return false;
        if (desired.Shape.HasValue && item.Info.Shape != desired.Shape.Value) return false;
        if (desired.HpPotion.HasValue)
        {
            bool healsHP = item.Info.Stats[Stat.HP] > 0 || item.Info.Stats[Stat.HPRatePercent] > 0;
            bool healsMP = item.Info.Stats[Stat.MP] > 0 || item.Info.Stats[Stat.MPRatePercent] > 0;
            if (desired.HpPotion.Value && !healsHP) return false;
            if (!desired.HpPotion.Value && !healsMP) return false;
        }

        return true;
    }

    private bool NeedMoreOfDesiredItem(DesiredItem desired)
    {
        if (_inventory == null) return false;
        var matching = _inventory.Where(i => i != null && MatchesDesiredItem(i, desired)).ToList();

        if (desired.Count.HasValue)
            return matching.Count < desired.Count.Value;

        if (desired.WeightFraction > 0)
        {
            int requiredWeight = (int)Math.Ceiling(GetMaxBagWeight() * desired.WeightFraction);
            int currentWeight = matching.Sum(i => i.Weight);
            return currentWeight < requiredWeight;
        }

        return false;
    }

    private static bool CanBeEquipped(ItemInfo info)
    {
        foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
        {
            if (IsItemForSlot(info, slot))
                return true;
        }
        return false;
    }

    private async Task EquipIfBetterAsync(UserItem item)
    {
        if (_equipment == null || item.Info == null) return;

        int bestSlot = -1;
        int bestDiff = 0;

        for (int slot = 0; slot < _equipment.Length; slot++)
        {
            var equipSlot = (EquipmentSlot)slot;
            if (!IsItemForSlot(item.Info, equipSlot)) continue;
            if (!CanEquipItem(item, equipSlot)) continue;

            var current = _equipment[slot];
            int newScore = GetItemScore(item, equipSlot);
            int currentScore = current != null ? GetItemScore(current, equipSlot) : -1;
            int diff = newScore - currentScore;
            if (diff > bestDiff)
            {
                bestDiff = diff;
                bestSlot = slot;
            }
        }

        if (bestSlot >= 0 && bestDiff > 0)
        {
            await EquipItemAsync(item, (EquipmentSlot)bestSlot);
            await Task.Delay(200);
            _lastPickedItem = null;
        }
    }

    private async Task BuyNeededItemsFromGoodsAsync(List<UserItem> goods, PanelType type)
    {
        if (goods.Count == 0) return;

        var desired = DesiredItemsProvider?.Invoke();
        if (desired == null && _equipment == null) return;

        foreach (var g in goods)
            Bind(g);

        int currentWeight = GetCurrentBagWeight();
        int maxWeight = GetMaxBagWeight();
        int freeSlots = _inventory?.Count(i => i == null) ?? int.MaxValue;

        foreach (var item in goods)
        {
            if (item.Info == null) continue;

            bool need = false;

            if (_equipment != null)
            {
                for (int slot = 0; slot < _equipment.Count(); slot++)
                {
                    var equipSlot = (EquipmentSlot)slot;
                    if (!IsItemForSlot(item.Info, equipSlot)) continue;
                    if (!CanEquipItem(item, equipSlot)) continue;
                    var current = _equipment[slot];
                    int newScore = GetItemScore(item, equipSlot);
                    int currentScore = current != null ? GetItemScore(current, equipSlot) : -1;
                    if (newScore > currentScore)
                    {
                        need = true;
                        break;
                    }
                }
            }

            if (!need && desired != null)
            {
                foreach (var d in desired)
                {
                    if (MatchesDesiredItem(item, d) && NeedMoreOfDesiredItem(d))
                    {
                        need = true;
                        break;
                    }
                }
            }

            if (need && _gold >= item.Info.Price)
            {
                if (freeSlots <= 0 || currentWeight + item.Weight > maxWeight)
                    continue;

                if (_dialogNpcId.HasValue && _npcEntries.TryGetValue(_dialogNpcId.Value, out var npc))
                    Console.WriteLine($"I am buying {item.Info.FriendlyName} from {npc.Name} for {item.Info.Price} gold");
                await BuyItemAsync(item.UniqueID, 1, type);
                await Task.Delay(200);
                if (_lastPickedItem != null && _lastPickedItem.Info != null &&
                    _lastPickedItem.Info.Index == item.Info.Index && CanBeEquipped(_lastPickedItem.Info))
                {
                    await EquipIfBetterAsync(_lastPickedItem);
                }

                freeSlots--;
                currentWeight += item.Weight;
            }
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
    public IEnumerable<Point> BlockingCells => _blockingCells.Keys;
    public bool IsMapLoaded => _mapData != null && _mapData.Width > 0 && _mapData.Height > 0;
    public Point CurrentLocation => _currentLocation;
    public long PingTime => _pingTime;
    public uint ObjectId => _objectId;
    public uint Gold => _gold;
    public UserItem? LastPickedItem => _lastPickedItem;
    public int HP => _hp;
    public int MP => _mp;
    public Func<UserItem, EquipmentSlot, int>? ItemScoreFunc { get; set; }
    public Func<IReadOnlyList<DesiredItem>>? DesiredItemsProvider { get; set; }

    private void ReportStatus()
    {
        var status = new AgentStatus
        {
            Level = _level,
            MapFile = _currentMapFile,
            MapName = _currentMapName,
            X = _currentLocation.X,
            Y = _currentLocation.Y,
            Action = _currentAction
        };
        _logger?.UpdateStatus(PlayerName, status);
    }

    public void UpdateAction(string action)
    {
        _currentAction = action;
        ReportStatus();
    }

    public GameClient(Config config, NpcMemoryBank npcMemory, MapMovementMemoryBank movementMemory, MapExpRateMemoryBank expRateMemory, IAgentLogger? logger = null)
    {
        _config = config;
        _npcMemory = npcMemory;
        _movementMemory = movementMemory;
        _expRateMemory = expRateMemory;
        _logger = logger;
    }

    private void StartMapExpTracking(string mapFile)
    {
        _mapStartTime = DateTime.UtcNow;
        _mapStartExp = _experience;
        _mapExpGained = 0;
        _mapStartLevel = _level;
        _mapStartClass = _playerClass;
    }

    private void FinalizeMapExpRate()
    {
        if (string.IsNullOrEmpty(_currentMapFile)) return;
        if (_mapStartTime == DateTime.MinValue) return;
        var elapsed = DateTime.UtcNow - _mapStartTime;
        if (elapsed >= TimeSpan.FromMinutes(15))
        {
            if (_mapStartClass != null)
            {
                double rate = _mapExpGained / elapsed.TotalHours;
                _expRateMemory.AddRate(_currentMapFile, _mapStartClass.Value, _mapStartLevel, rate);
            }
        }
    }

    public void ProcessMapExpRateInterval()
    {
        if (string.IsNullOrEmpty(_currentMapFile)) return;
        if (_mapStartTime == DateTime.MinValue) return;
        var elapsed = DateTime.UtcNow - _mapStartTime;
        if (elapsed >= TimeSpan.FromMinutes(15))
        {
            if (_mapStartClass != null)
            {
                double rate = _mapExpGained / elapsed.TotalHours;
                _expRateMemory.AddRate(_currentMapFile, _mapStartClass.Value, _mapStartLevel, rate);
            }
            StartMapExpTracking(_currentMapFile);
        }
    }

    private Task RandomStartupDelayAsync() => Task.Delay(_random.Next(1000, 3000));

    private void MarkStatsDirty() => _statsDirty = true;

    private void RecalculateStats()
    {
        if (_playerClass == null)
        {
            _cachedMaxBagWeight = int.MaxValue;
            _cachedMaxHP = int.MaxValue;
            _cachedMaxMP = int.MaxValue;
            _statsDirty = false;
            return;
        }

        _baseStats ??= new BaseStats(_playerClass.Value);

        int baseWeight = _baseStats.Stats.First(s => s.Type == Stat.BagWeight).Calculate(_playerClass.Value, _level);
        int extraWeight = 0;
        int extraHP = 0;
        int extraMP = 0;
        if (_equipment != null)
        {
            foreach (var item in _equipment)
            {
                if (item == null || item.Info == null) continue;
                extraWeight += item.Info.Stats[Stat.BagWeight];
                extraWeight += item.AddedStats[Stat.BagWeight];
                extraHP += item.Info.Stats[Stat.HP];
                extraHP += item.AddedStats[Stat.HP];
                extraMP += item.Info.Stats[Stat.MP];
                extraMP += item.AddedStats[Stat.MP];
            }
        }
        _cachedMaxBagWeight = baseWeight + extraWeight;
        int baseHP = _baseStats.Stats.First(s => s.Type == Stat.HP).Calculate(_playerClass.Value, _level);
        int baseMP = _baseStats.Stats.First(s => s.Type == Stat.MP).Calculate(_playerClass.Value, _level);
        _cachedMaxHP = baseHP + extraHP;
        _cachedMaxMP = baseMP + extraMP;
        _statsDirty = false;
    }

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
        if (_statsDirty) RecalculateStats();
        return _cachedMaxBagWeight;
    }

    public int GetMaxHP()
    {
        if (_statsDirty) RecalculateStats();
        return _cachedMaxHP;
    }

    public int GetMaxMP()
    {
        if (_statsDirty) RecalculateStats();
        return _cachedMaxMP;
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

    public int GetStatTotal(Stat stat)
    {
        int total = 0;

        if (_playerClass != null)
        {
            _baseStats ??= new BaseStats(_playerClass.Value);
            var baseStat = _baseStats.Stats.FirstOrDefault(s => s.Type == stat);
            if (baseStat != null)
                total += baseStat.Calculate(_playerClass.Value, _level);
        }

        if (_equipment != null)
        {
            foreach (var item in _equipment)
            {
                if (item == null || item.Info == null) continue;
                total += item.Info.Stats[stat];
                total += item.AddedStats[stat];
            }
        }

        return total;
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

    private async Task DetermineSellTypesAsync(NpcEntry entry)
    {
        if (_inventory == null) return;
        var seen = new HashSet<ItemType>();
        if (entry.SellItemTypes != null) seen.UnionWith(entry.SellItemTypes);
        if (entry.CannotSellItemTypes != null) seen.UnionWith(entry.CannotSellItemTypes);
        foreach (var item in _inventory)
        {
            if (item == null || item.Info == null) continue;
            if (seen.Contains(item.Info.Type)) continue;
            seen.Add(item.Info.Type);
            _pendingSellChecks[item.UniqueID] = (entry, item.Info.Type);
            Console.WriteLine($"I am selling {item.Info.FriendlyName} to {entry.Name}");
            using var cts = new CancellationTokenSource(2000);
            var waitTask = WaitForSellItemAsync(item.UniqueID, cts.Token);
            await SendAsync(new C.SellItem { UniqueID = item.UniqueID, Count = 1 });
            try
            {
                await waitTask;
            }
            catch (OperationCanceledException)
            {
            }
            await Task.Delay(200);
        }
    }

    private async Task DetermineRepairTypesAsync(NpcEntry entry)
    {
        if (_inventory == null) return;
        var seen = new HashSet<ItemType>();
        if (entry.RepairItemTypes != null) seen.UnionWith(entry.RepairItemTypes);
        if (entry.CannotRepairItemTypes != null) seen.UnionWith(entry.CannotRepairItemTypes);
        foreach (var item in _inventory)
        {
            if (item == null || item.Info == null) continue;
            if (item.CurrentDura == item.MaxDura) continue;
            if (seen.Contains(item.Info.Type)) continue;
            seen.Add(item.Info.Type);
            _pendingRepairChecks[item.UniqueID] = (entry, item.Info.Type);
            Console.WriteLine($"I am repairing {item.Info.FriendlyName} at {entry.Name}");
            using var cts = new CancellationTokenSource(2000);
            var waitTask = WaitForRepairItemAsync(item.UniqueID, cts.Token);
            try
            {

                await SendAsync(new C.RepairItem { UniqueID = item.UniqueID });
                await waitTask;
            }
            catch (OperationCanceledException)
            {
            }
            await Task.Delay(200);
        }
    }

    private async Task HandleNpcSellAsync(NpcEntry entry)
    {
        await DetermineSellTypesAsync(entry);
        _npcSellTcs?.TrySetResult(true);
        _npcSellTcs = null;
        ProcessNpcActionQueue();
    }

    private async Task HandleNpcRepairAsync(NpcEntry entry)
    {
        await DetermineRepairTypesAsync(entry);
        _npcRepairTcs?.TrySetResult(true);
        _npcRepairTcs = null;
        ProcessNpcActionQueue();
    }

    private void ProcessNpcGoods(IEnumerable<UserItem> goods, PanelType type)
    {
        if (!_dialogNpcId.HasValue) return;
        if (!_npcEntries.TryGetValue(_dialogNpcId.Value, out var entry)) return;

        if (_skipNextGoods)
        {
            _skipNextGoods = false;
            return;
        }

        if (type != PanelType.Buy && type != PanelType.BuySub)
            return;

        _lastNpcGoods = goods.Select(g =>
        {
            Bind(g);
            return g;
        }).ToList();
        _lastNpcGoodsType = type;

        entry.CanBuy = true;
        entry.BuyItemIndexes ??= new List<int>();
        foreach (var it in _lastNpcGoods)
        {
            int index = it.Info?.Index ?? it.ItemIndex;
            if (!entry.BuyItemIndexes.Contains(index))
                entry.BuyItemIndexes.Add(index);
        }

        _npcMemory.SaveChanges();
        _npcGoodsTcs?.TrySetResult(true);
        _npcGoodsTcs = null;
    }

    private void TryFinishNpcInteraction()
    {
        if (_dialogNpcId.HasValue &&
            _pendingSellChecks.Count == 0 &&
            _pendingRepairChecks.Count == 0 &&
            _npcActionTasks.Count == 0 &&
            !_processingNpcAction)
        {
            _dialogNpcId = null;
            _npcInteraction = null;
            ProcessNextNpcInQueue();
        }
    }

    private void ProcessNextNpcInQueue()
    {
        if (IgnoreNpcInteractions) return;
        while (_npcQueue.Count > 0)
        {
            var id = _npcQueue.Dequeue();
            if (_npcEntries.TryGetValue(id, out var entry))
            {
                StartNpcInteraction(id, entry);
                break;
            }
        }
    }

    private async void ProcessNpcActionQueue()
    {
        if (_processingNpcAction || !_dialogNpcId.HasValue || _npcInteraction == null) return;
        if (_pendingSellChecks.Count > 0 || _pendingRepairChecks.Count > 0) return;

        if (_npcActionTasks.Count == 0)
        {
            TryFinishNpcInteraction();
            return;
        }

        var item = _npcActionTasks.Dequeue();
        _processingNpcAction = true;
        await item.action();
    }

    private async void StartNpcInteraction(uint id, NpcEntry entry)
    {
        _dialogNpcId = id;
        _npcInteractionStart = DateTime.UtcNow;
        _npcActionTasks.Clear();
        _processingNpcAction = false;
        Console.WriteLine($"I am speaking with NPC {entry.Name}");
        _npcInteraction = new NPCInteraction(this, id);
        var page = await _npcInteraction.BeginAsync();
        HandleNpcDialogPage(page, entry);
    }

    private Func<Task> CreateBuyTask(string key) => async () =>
    {
        if (_npcInteraction == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = WaitForNpcGoodsAsync(cts.Token);
        try
        {
            if (_dialogNpcId.HasValue && _npcEntries.TryGetValue(_dialogNpcId.Value, out var entry))
                Console.WriteLine($"I am looking at {entry.Name}'s goods list");
            await _npcInteraction.SelectFromMainAsync(key);
            await waitTask;
        }
        finally
        {
            _processingNpcAction = false;
            ProcessNpcActionQueue();
        }
    };

    private Func<Task> CreateSellTask(string key) => async () =>
    {
        if (_npcInteraction == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = WaitForNpcSellAsync(cts.Token);
        try
        {
            await _npcInteraction.SelectFromMainAsync(key);
            await waitTask;
        }
        finally
        {
            _processingNpcAction = false;
            ProcessNpcActionQueue();
        }
    };

    private Func<Task> CreateRepairTask(string key) => async () =>
    {
        if (_npcInteraction == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = WaitForNpcRepairAsync(cts.Token);
        try
        {
            await _npcInteraction.SelectFromMainAsync(key);
            await waitTask;
        }
        finally
        {
            _processingNpcAction = false;
            ProcessNpcActionQueue();
        }
    };

    private Func<Task> CreateCheckBuyTask(string key) => async () =>
    {
        if (_npcInteraction == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = WaitForNpcGoodsAsync(cts.Token);
        try
        {
            if (_dialogNpcId.HasValue && _npcEntries.TryGetValue(_dialogNpcId.Value, out var entry))
                Console.WriteLine($"I am looking at {entry.Name}'s goods list");
            await _npcInteraction.SelectFromMainAsync(key);
            await waitTask;
            if (_lastNpcGoods != null)
                await BuyNeededItemsFromGoodsAsync(_lastNpcGoods, _lastNpcGoodsType);
        }
        finally
        {
            _processingNpcAction = false;
            ProcessNpcActionQueue();
        }
    };

    private void HandleNpcDialogPage(NpcDialogPage page, NpcEntry entry)
    {
        var keyList = page.Buttons.Select(b => b.Key).ToList();
        var keys = new HashSet<string>(keyList.Select(k => k.ToUpper()));

        bool changed = false;
        bool needBuyCheck = false;

        bool hasBuy = keys.Overlaps(new[] { "@BUY", "@BUYSELL", "@BUYNEW", "@BUYSELLNEW", "@PEARLBUY" });
        bool hasSell = keys.Overlaps(new[] { "@SELL", "@BUYSELL", "@BUYSELLNEW" });
        bool hasRepair = keys.Overlaps(new[] { "@REPAIR", "@SREPAIR" });

        string? buyKey = null;
        string? sellKey = null;
        string? repairKey = null;

        if (hasBuy)
        {
            if (!entry.CanBuy)
            {
                entry.CanBuy = true;
                changed = true;
            }
            string[] buyKeys = { "@BUYSELLNEW", "@BUYSELL", "@BUYNEW", "@PEARLBUY", "@BUY" };
            buyKey = keyList.FirstOrDefault(k => buyKeys.Contains(k.ToUpper())) ?? "@BUY";
            if (entry.BuyItemIndexes == null)
            {
                needBuyCheck = true;
                if (buyKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
                {
                    _skipNextGoods = true;
                    buyKey = null;
                }
            }
        }

        if (hasSell)
        {
            if (!entry.CanSell)
            {
                entry.CanSell = true;
                changed = true;
            }
            if (entry.SellItemTypes == null && entry.CannotSellItemTypes == null)
            {
                string[] sellKeys = { "@BUYSELLNEW", "@BUYSELL", "@SELL" };
                sellKey = keyList.FirstOrDefault(k => sellKeys.Contains(k.ToUpper())) ?? "@SELL";
                if (sellKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
                {
                    _skipNextGoods = true;
                    sellKey = null;
                }
            }
        }

        if (hasRepair)
        {
            if (!entry.CanRepair)
            {
                entry.CanRepair = true;
                changed = true;
            }
            if (entry.RepairItemTypes == null && entry.CannotRepairItemTypes == null)
            {
                string[] repairKeys = { "@SREPAIR", "@REPAIR" };
                repairKey = keyList.FirstOrDefault(k => repairKeys.Contains(k.ToUpper())) ?? "@REPAIR";
                if (repairKey.Equals("@BUYBACK", StringComparison.OrdinalIgnoreCase))
                {
                    _skipNextGoods = true;
                    repairKey = null;
                }
            }
        }

        if (needBuyCheck && buyKey != null)
        {
            _npcActionTasks.Enqueue((buyKey, CreateBuyTask(buyKey)));
        }
        if (sellKey != null)
        {
            _npcActionTasks.Enqueue((sellKey, CreateSellTask(sellKey)));
        }
        if (repairKey != null)
        {
            _npcActionTasks.Enqueue((repairKey, CreateRepairTask(repairKey)));
        }
        if (buyKey != null)
        {
            _npcActionTasks.Enqueue((buyKey, CreateCheckBuyTask(buyKey)));
        }

        if (changed)
            _npcMemory.SaveChanges();

        ProcessNpcActionQueue();
    }

    private void CheckNpcInteractionTimeout()
    {
        if (_dialogNpcId.HasValue &&
            DateTime.UtcNow - _npcInteractionStart > TimeSpan.FromSeconds(10))
        {
            _dialogNpcId = null;
            _pendingSellChecks.Clear();
            _pendingRepairChecks.Clear();
            ProcessNextNpcInQueue();
        }
    }

    public void ResumeNpcInteractions()
    {
        ProcessNextNpcInQueue();
    }

    private static void FireAndForget(Task task)
    {
        task.ContinueWith(t => Console.WriteLine(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
    }
}
