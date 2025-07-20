using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using C = ClientPackets;
using S = ServerPackets;
using Shared;

public class GameClient
{
    private readonly Config _config;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly ConcurrentQueue<Packet> _sendQueue = new();
    private readonly byte[] _buffer = new byte[1024 * 8];
    private byte[] _rawData = Array.Empty<byte>();
    private int? _selectedIndex;
    private readonly Random _random = new();
    private MirClass? _playerClass;
    private readonly TaskCompletionSource<MirClass> _classTcs = new();
    private Point _currentLocation = Point.Empty;
    private string _playerName = string.Empty;

    private LightSetting _timeOfDay = LightSetting.Normal;

    private MirGender _gender;
    private ushort _level;
    private UserItem[]? _inventory;
    private UserItem[]? _equipment;

    public static readonly List<ItemInfo> ItemInfoList = new();

    private static void Bind(UserItem item)
    {
        var info = ItemInfoList.Find(i => i.Index == item.ItemIndex);
        if (info != null)
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

    public MirClass? PlayerClass => _playerClass;
    public Task<MirClass> WaitForClassAsync() => _classTcs.Task;
    public LightSetting TimeOfDay => _timeOfDay;

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
        _ = Task.Run(ReceiveLoop);
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
            BirthDate = DateTime.Today,
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
                    _selectedIndex = match.Index;
                    Console.WriteLine($"Selected character '{match.Name}' (Index {match.Index})");
                    var start = new C.StartGame { CharacterIndex = match.Index };
                    _ = Task.Run(async () => { await RandomStartupDelayAsync(); await SendAsync(start); });
                }
                break;
            case S.NewCharacterSuccess ncs:
                Console.WriteLine("Character created");
                _selectedIndex = ncs.CharInfo.Index;
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
            case S.UserInformation info:
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
            case S.NewItemInfo nii:
                int idx = ItemInfoList.FindIndex(i => i.Index == nii.Info.Index);
                if (idx >= 0)
                    ItemInfoList[idx] = nii.Info;
                else
                    ItemInfoList.Add(nii.Info);
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
                    idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == di.UniqueID);
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
                    idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == newItem.UniqueID);
                    if (idx >= 0) _inventory[idx] = newItem;
                }
                if (_equipment != null)
                {
                    idx = Array.FindIndex(_equipment, x => x != null && x.UniqueID == newItem.UniqueID);
                    if (idx >= 0) _equipment[idx] = newItem;
                }
                break;
            default:
                // ignore unhandled packets
                break;
        }
    }
}
