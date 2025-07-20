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

public class GameClient
{
    private readonly Config _config;
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
    private UserItem[]? _inventory;
    private UserItem[]? _equipment;

    private uint? _lastAttackTarget;
    private uint? _lastStruckAttacker;

    private bool _dead;

    private DateTime _lastMoveTime = DateTime.MinValue;
    private bool _canRun;

    // store information on nearby objects
    private readonly ConcurrentDictionary<uint, TrackedObject> _trackedObjects = new();

    // Use a dictionary for faster lookups by item index
    public static readonly Dictionary<int, ItemInfo> ItemInfoDict = new();

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

    public GameClient(Config config)
    {
        _config = config;
    }

    private Task RandomStartupDelayAsync() => Task.Delay(_random.Next(1000, 3000));

    public async Task ConnectAsync()
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_config.ServerIP, _config.ServerPort);
        _stream = _client.GetStream();
        Console.WriteLine("Connected to server");
        _canRun = false;
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(KeepAliveLoop);
    }

    public async Task LoginAsync()
    {
        if (_stream == null) return;
        Console.WriteLine("Logging in...");
        // send client version (empty hash for simplicity)
        var ver = new C.ClientVersion { VersionHash = Array.Empty<byte>() };
        await RandomStartupDelayAsync();
        await SendAsync(ver);

        // send login details
        var login = new C.Login { AccountID = _config.AccountID, Password = _config.Password };
        await RandomStartupDelayAsync();
        await SendAsync(login);

        // StartGame will be sent once LoginSuccess is received
    }

    private async Task CreateAccountAsync()
    {
        if (_stream == null) return;
        Console.WriteLine($"Creating account '{_config.AccountID}'...");
        var acc = new C.NewAccount
        {
            AccountID = _config.AccountID,
            Password = _config.Password,
            BirthDate = DateTime.UtcNow.Date,
            UserName = _config.AccountID,
            SecretQuestion = string.Empty,
            SecretAnswer = string.Empty,
            EMailAddress = string.Empty
        };
        await RandomStartupDelayAsync();
        await SendAsync(acc);
    }

    private async Task CreateCharacterAsync()
    {
        if (_stream == null) return;
        Console.WriteLine($"Creating character '{_config.CharacterName}'...");
        var chr = new C.NewCharacter
        {
            Name = _config.CharacterName,
            Gender = (MirGender)_random.Next(Enum.GetValues<MirGender>().Length),
            Class = (MirClass)_random.Next(Enum.GetValues<MirClass>().Length)
        };
        await RandomStartupDelayAsync();
        await SendAsync(chr);
    }

    public async Task WalkAsync(MirDirection direction)
    {
        if (_stream == null) return;
        var target = Functions.PointMove(_currentLocation, direction, 1);
        Console.WriteLine($"I am walking to {target.X}, {target.Y}");
        var walk = new C.Walk { Direction = direction };
        await SendAsync(walk);
        _lastMoveTime = DateTime.UtcNow;
        _canRun = true;
    }

    public async Task RunAsync(MirDirection direction)
    {
        if (_stream == null) return;
        var target = Functions.PointMove(_currentLocation, direction, 2);
        Console.WriteLine($"I am running to {target.X}, {target.Y}");
        var run = new C.Run { Direction = direction };
        await SendAsync(run);
        _lastMoveTime = DateTime.UtcNow;
    }

    private bool IsCellBlocked(Point p)
    {
        if (_mapData == null || !_mapData.IsWalkable(p.X, p.Y))
            return true;

        foreach (var obj in _trackedObjects.Values)
        {
            if (obj.Id == _objectId || obj.Dead) continue;
            if (obj.Type == ObjectType.Player || obj.Type == ObjectType.Monster || obj.Type == ObjectType.Merchant)
            {
                if (obj.Location == p)
                    return true;
            }
        }

        return false;
    }

    public bool CanWalk(MirDirection direction)
    {
        var target = Functions.PointMove(_currentLocation, direction, 1);
        return !IsCellBlocked(target);
    }

    public bool CanRun(MirDirection direction)
    {
        if (!_canRun) return false;
        if (DateTime.UtcNow - _lastMoveTime > TimeSpan.FromMilliseconds(900)) return false;

        var first = Functions.PointMove(_currentLocation, direction, 1);
        var second = Functions.PointMove(_currentLocation, direction, 2);

        if (IsCellBlocked(first) || IsCellBlocked(second)) return false;

        return true;
    }

    public async Task AttackAsync(MirDirection direction)
    {
        if (_stream == null) return;
        var attack = new C.Attack { Direction = direction, Spell = Spell.None };
        await SendAsync(attack);
    }

    public async Task TownReviveAsync()
    {
        if (_stream == null) return;
        await SendAsync(new C.TownRevive());
    }

    public async Task EquipItemAsync(UserItem item, EquipmentSlot slot)
    {
        if (_stream == null) return;
        var equip = new C.EquipItem
        {
            Grid = MirGridType.Inventory,
            UniqueID = item.UniqueID,
            To = (int)slot
        };
        await SendAsync(equip);
    }

    private int FindFreeInventorySlot()
    {
        if (_inventory == null) return -1;
        for (int i = 0; i < _inventory.Length; i++)
        {
            if (_inventory[i] == null) return i;
        }
        return -1;
    }

    public async Task UnequipItemAsync(EquipmentSlot slot)
    {
        if (_stream == null || _equipment == null) return;
        var item = _equipment[(int)slot];
        if (item == null) return;
        int index = FindFreeInventorySlot();
        if (index < 0) return;

        var remove = new C.RemoveItem
        {
            Grid = MirGridType.Inventory,
            UniqueID = item.UniqueID,
            To = index
        };

        await SendAsync(remove);
    }


    public bool CanEquipItem(UserItem item, EquipmentSlot slot)
    {
        if (_playerClass == null) return false;
        if (item.Info == null) return false;

        if (!IsItemForSlot(item.Info, slot)) return false;

        if (item.Info.RequiredGender != RequiredGender.None)
        {
            RequiredGender genderFlag = _gender == MirGender.Male ? RequiredGender.Male : RequiredGender.Female;
            if (!item.Info.RequiredGender.HasFlag(genderFlag))
                return false;
        }

        RequiredClass playerClassFlag = _playerClass switch
        {
            MirClass.Warrior => RequiredClass.Warrior,
            MirClass.Wizard => RequiredClass.Wizard,
            MirClass.Taoist => RequiredClass.Taoist,
            MirClass.Assassin => RequiredClass.Assassin,
            MirClass.Archer => RequiredClass.Archer,
            _ => RequiredClass.None
        };

        if (!item.Info.RequiredClass.HasFlag(playerClassFlag))
            return false;

        switch (item.Info.RequiredType)
        {
            case RequiredType.Level:
                if (_level < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MaxLevel:
                if (_level > item.Info.RequiredAmount) return false;
                break;
        }

        return true;
    }

    private static bool IsItemForSlot(ItemInfo info, EquipmentSlot slot)
    {
        return slot switch
        {
            EquipmentSlot.Weapon => info.Type == ItemType.Weapon,
            EquipmentSlot.Armour => info.Type == ItemType.Armour,
            EquipmentSlot.Helmet => info.Type == ItemType.Helmet,
            EquipmentSlot.Torch => info.Type == ItemType.Torch,
            EquipmentSlot.Necklace => info.Type == ItemType.Necklace,
            EquipmentSlot.BraceletL => info.Type == ItemType.Bracelet,
            EquipmentSlot.BraceletR => info.Type == ItemType.Bracelet,
            EquipmentSlot.RingL => info.Type == ItemType.Ring,
            EquipmentSlot.RingR => info.Type == ItemType.Ring,
            EquipmentSlot.Amulet => info.Type == ItemType.Amulet,
            EquipmentSlot.Belt => info.Type == ItemType.Belt,
            EquipmentSlot.Boots => info.Type == ItemType.Boots,
            EquipmentSlot.Stone => info.Type == ItemType.Stone,
            EquipmentSlot.Mount => info.Type == ItemType.Mount,
            _ => false
        };
    }

    private async Task SendAsync(Packet p)
    {
        if (_stream == null) return;
        var data = p.GetPacketBytes().ToArray();
        await _stream.WriteAsync(data, 0, data.Length);
    }

    private async Task ReceiveLoop()
    {
        if (_stream == null) return;
        try
        {
            while (true)
            {
                int count = await _stream.ReadAsync(_buffer, 0, _buffer.Length);
                if (count == 0) break;
                var tmp = new byte[_rawData.Length + count];
                Buffer.BlockCopy(_rawData, 0, tmp, 0, _rawData.Length);
                Buffer.BlockCopy(_buffer, 0, tmp, _rawData.Length, count);
                _rawData = tmp;

                Packet? p;
                while ((p = Packet.ReceivePacket(_rawData, out _rawData)) != null)
                {
                    HandlePacket(p);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Receive error: {ex.Message}");
        }
    }

    private void HandlePacket(Packet p)
    {
        switch (p)
        {
            case S.Login l:
                if (l.Result == 3)
                {
                    Console.WriteLine("Account not found, creating...");
                    _ = CreateAccountAsync();
                }
                else if (l.Result != 4)
                {
                    Console.WriteLine($"Login failed: {l.Result}");
                }
                else
                {
                    Console.WriteLine("Wrong password");
                }
                break;
            case S.NewAccount na:
                if (na.Result == 8)
                {
                    Console.WriteLine("Account created");
                    _ = LoginAsync();
                }
                else
                {
                    Console.WriteLine($"Account creation failed: {na.Result}");
                }
                break;
            case S.LoginSuccess ls:
                var match = ls.Characters.FirstOrDefault(c => c.Name.Equals(_config.CharacterName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Console.WriteLine($"Character '{_config.CharacterName}' not found, creating...");
                    _ = CreateCharacterAsync();
                }
                else
                {
                    Console.WriteLine($"Selected character '{match.Name}' (Index {match.Index})");
                    var start = new C.StartGame { CharacterIndex = match.Index };
                    _ = Task.Run(async () => { await RandomStartupDelayAsync(); await SendAsync(start); });
                }
                break;
            case S.NewCharacterSuccess ncs:
                Console.WriteLine("Character created");
                var startNew = new C.StartGame { CharacterIndex = ncs.CharInfo.Index };
                _ = Task.Run(async () => { await RandomStartupDelayAsync(); await SendAsync(startNew); });
                break;
            case S.NewCharacter nc:
                Console.WriteLine($"Character creation failed: {nc.Result}");
                break;
            case S.StartGame sg:
                var reason = sg.Result switch
                {
                    0 => "Disabled",
                    1 => "Not logged in",
                    2 => "Character not found",
                    3 => "Start Game Error",
                    4 => "Success",
                    _ => "Unknown"
                };
                Console.WriteLine($"StartGame Result: {sg.Result} ({reason})");
                break;
            case S.StartGameBanned ban:
                Console.WriteLine($"StartGame Banned: {ban.Reason} until {ban.ExpiryDate}");
                break;
            case S.StartGameDelay delay:
                Console.WriteLine($"StartGame delayed for {delay.Milliseconds} ms");
                break;
            case S.MapInformation mi:
                _currentMapFile = Path.Combine(MapManager.MapDirectory, mi.FileName + ".map");
                _ = LoadMapAsync();
                break;
            case S.MapChanged mc:
                _currentMapFile = Path.Combine(MapManager.MapDirectory, mc.FileName + ".map");
                _currentLocation = mc.Location;
                _ = LoadMapAsync();
                break;
            case S.UserInformation info:
                _objectId = info.ObjectID;
                _playerClass = info.Class;
                _playerName = info.Name;
                _currentLocation = info.Location;
                _gender = info.Gender;
                _level = info.Level;
                _inventory = info.Inventory;
                _equipment = info.Equipment;
                BindAll(_inventory);
                BindAll(_equipment);
                Console.WriteLine($"Logged in as {_playerName}");
                Console.WriteLine($"I am currently at location {_currentLocation.X}, {_currentLocation.Y}");
                _classTcs.TrySetResult(info.Class);
                break;
            case S.UserLocation loc:
                _currentLocation = loc.Location;
                break;
            case S.TimeOfDay tod:
                _timeOfDay = tod.Lights;
                break;
            case S.ObjectPlayer op:
                _trackedObjects[op.ObjectID] = new TrackedObject(op.ObjectID, ObjectType.Player, op.Name, op.Location, op.Direction);
                break;
            case S.ObjectMonster om:
                _trackedObjects[om.ObjectID] = new TrackedObject(om.ObjectID, ObjectType.Monster, om.Name, om.Location, om.Direction, om.AI, om.Dead);
                break;
            case S.ObjectNPC on:
                _trackedObjects[on.ObjectID] = new TrackedObject(on.ObjectID, ObjectType.Merchant, on.Name, on.Location, on.Direction);
                break;
            case S.ObjectItem oi:
                _trackedObjects[oi.ObjectID] = new TrackedObject(oi.ObjectID, ObjectType.Item, oi.Name, oi.Location, MirDirection.Up);
                break;
            case S.ObjectGold og:
                _trackedObjects[og.ObjectID] = new TrackedObject(og.ObjectID, ObjectType.Item, "Gold", og.Location, MirDirection.Up);
                break;
            case S.ObjectTurn ot:
                if (_trackedObjects.TryGetValue(ot.ObjectID, out var objT))
                {
                    objT.Location = ot.Location;
                    objT.Direction = ot.Direction;
                }
                break;
            case S.ObjectWalk ow:
                if (_trackedObjects.TryGetValue(ow.ObjectID, out var objW))
                {
                    objW.Location = ow.Location;
                    objW.Direction = ow.Direction;
                }
                if (ow.ObjectID == _objectId)
                {
                    _currentLocation = ow.Location;
                    _lastMoveTime = DateTime.UtcNow;
                    _canRun = true;
                }
                break;
            case S.ObjectRun oru:
                if (_trackedObjects.TryGetValue(oru.ObjectID, out var objR))
                {
                    objR.Location = oru.Location;
                    objR.Direction = oru.Direction;
                }
                if (oru.ObjectID == _objectId)
                {
                    _currentLocation = oru.Location;
                    _lastMoveTime = DateTime.UtcNow;
                }
                break;
            case S.Struck st:
                _lastStruckAttacker = st.AttackerID;
                break;
            case S.ObjectStruck os:
                if (os.AttackerID == _objectId)
                {
                    _lastAttackTarget = os.ObjectID;
                    if (_trackedObjects.TryGetValue(os.ObjectID, out var targ) && targ.Type == ObjectType.Monster)
                    {
                        targ.EngagedWith = _objectId;
                        targ.LastEngagedTime = DateTime.UtcNow;
                    }
                }
                else if (_trackedObjects.TryGetValue(os.AttackerID, out var atk) &&
                         _trackedObjects.TryGetValue(os.ObjectID, out var target))
                {
                    // player attacking monster
                    if (atk.Type == ObjectType.Player && atk.Id != _objectId && target.Type == ObjectType.Monster)
                    {
                        target.EngagedWith = atk.Id;
                        target.LastEngagedTime = DateTime.UtcNow;
                    }
                    // monster attacking player
                    else if (atk.Type == ObjectType.Monster && target.Type == ObjectType.Player && target.Id != _objectId)
                    {
                        atk.EngagedWith = target.Id;
                        atk.LastEngagedTime = DateTime.UtcNow;
                    }
                }
                break;
            case S.DamageIndicator di:
                if (di.ObjectID == _objectId && di.Type != DamageType.Miss && _lastStruckAttacker.HasValue)
                {
                    string name = _trackedObjects.TryGetValue(_lastStruckAttacker.Value, out var atk) ? atk.Name : "Unknown";
                    Console.WriteLine($"{name} has attacked me for {-di.Damage} damage");
                    _lastStruckAttacker = null;
                }
                else if (_lastAttackTarget.HasValue && di.ObjectID == _lastAttackTarget.Value)
                {
                    if (di.Type == DamageType.Miss)
                    {
                        if (_trackedObjects.TryGetValue(di.ObjectID, out var targ))
                            Console.WriteLine($"I attacked {targ.Name} and missed");
                        else
                            Console.WriteLine("I attacked an unknown target and missed");
                    }
                    else
                    {
                        string name = _trackedObjects.TryGetValue(di.ObjectID, out var targ) ? targ.Name : "Unknown";
                        Console.WriteLine($"I have damaged {name} for {-di.Damage} damage");
                    }
                    _lastAttackTarget = null;
                }
                break;
            case S.ObjectDied od:
                if (od.ObjectID == _objectId)
                {
                    Console.WriteLine("I have died.");
                    _dead = true;
                }
                else if (_trackedObjects.TryGetValue(od.ObjectID, out var objD))
                {
                    objD.Dead = true;
                }
                break;
            case S.ObjectRemove ore:
                _trackedObjects.TryRemove(ore.ObjectID, out _);
                break;
            case S.Revived:
                if (_dead)
                {
                    Console.WriteLine("I have been revived.");
                }
                _dead = false;
                break;
            case S.ObjectRevived orv:
                if (orv.ObjectID == _objectId)
                {
                    if (_dead)
                    {
                        Console.WriteLine("I have been revived.");
                    }
                    _dead = false;
                }
                else if (_trackedObjects.TryGetValue(orv.ObjectID, out var objRev))
                {
                    objRev.Dead = false;
                }
                break;
            case S.NewItemInfo nii:
                // Replace or add the item info in the dictionary
                ItemInfoDict[nii.Info.Index] = nii.Info;
                break;
            case S.GainedItem gi:
                Bind(gi.Item);
                if (_inventory != null)
                {
                    for (int i = 0; i < _inventory.Length; i++)
                    {
                        if (_inventory[i] == null)
                        {
                            _inventory[i] = gi.Item;
                            break;
                        }
                    }
                }
                break;
            case S.MoveItem mi:
                if (mi.Grid == MirGridType.Inventory && _inventory != null && mi.Success && mi.From >= 0 && mi.To >= 0 && mi.From < _inventory.Length && mi.To < _inventory.Length)
                {
                    var tmp = _inventory[mi.To];
                    _inventory[mi.To] = _inventory[mi.From];
                    _inventory[mi.From] = tmp;
                }
                break;
            case S.EquipItem ei:
                if (ei.Grid == MirGridType.Inventory && ei.Success && _inventory != null && _equipment != null)
                {
                    int invIndex = Array.FindIndex(_inventory, x => x != null && x.UniqueID == ei.UniqueID);
                    if (invIndex >= 0 && ei.To >= 0 && ei.To < _equipment.Length)
                    {
                        var temp = _equipment[ei.To];
                        _equipment[ei.To] = _inventory[invIndex];
                        _inventory[invIndex] = temp;
                    }
                }
                break;
            case S.RemoveItem ri:
                if (ri.Grid == MirGridType.Inventory && ri.Success && _inventory != null && _equipment != null)
                {
                    int eqIndex = Array.FindIndex(_equipment, x => x != null && x.UniqueID == ri.UniqueID);
                    if (eqIndex >= 0 && ri.To >= 0 && ri.To < _inventory.Length)
                    {
                        _inventory[ri.To] = _equipment[eqIndex];
                        _equipment[eqIndex] = null;
                    }
                }
                break;
            case S.DeleteItem di:
                if (_inventory != null)
                {
                    int idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == di.UniqueID);
                    if (idx >= 0)
                    {
                        var it = _inventory[idx];
                        if (it != null)
                        {
                            if (it.Count > di.Count)
                                it.Count -= di.Count;
                            else
                                _inventory[idx] = null;
                        }
                    }
                }
                break;
            case S.RefreshItem rfi:
                var newItem = rfi.Item;
                Bind(newItem);
                if (_inventory != null)
                {
                    int idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == newItem.UniqueID);
                    if (idx >= 0) _inventory[idx] = newItem;
                }
                if (_equipment != null)
                {
                    int idx = Array.FindIndex(_equipment, x => x != null && x.UniqueID == newItem.UniqueID);
                    if (idx >= 0) _equipment[idx] = newItem;
                }
                break;
            case S.KeepAlive keep:
                _pingTime = Environment.TickCount64 - keep.Time;
                break;
            default:
                // ignore unhandled packets
                break;
        }
    }

    private async Task LoadMapAsync()
    {
        if (string.IsNullOrEmpty(_currentMapFile)) return;
        _mapData = await MapManager.GetMapAsync(_currentMapFile);
    }

    private async Task KeepAliveLoop()
    {
        while (_stream != null)
        {
            await Task.Delay(5000);
            try
            {
                await SendAsync(new C.KeepAlive { Time = Environment.TickCount64 });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"KeepAlive error: {ex.Message}");
            }
        }
    }
}
