using System;
using System.Drawing;
using System.Linq;
using Shared;
using C = ClientPackets;

public sealed partial class GameClient
{
    private void AddPendingMovementCell(Point p)
    {
        if (!IsKnownMovementCell(p))
            _pendingMovementAction.Add(p);
    }

    public async Task WalkAsync(MirDirection direction)
    {
        if (_stream == null) return;
        if (_movementSaveCts != null) return;
        CancelMovementDeleteCheck();
        var target = Functions.PointMove(_currentLocation, direction, 1);
        if (RecentlyChangedMap && IsKnownMovementCell(target))
            return;
        await TryOpenDoorAsync(target);
        Log($"I am walking to {target.X}, {target.Y}");
        _pendingMoveTarget = target;
        _pendingMovementAction.Clear();
        AddPendingMovementCell(target);
        var walk = new C.Walk { Direction = direction };
        await SendAsync(walk);
        _lastMoveTime = DateTime.UtcNow;
        _canRun = true;
    }

    public async Task RunAsync(MirDirection direction)
    {
        if (_stream == null) return;
        if (_movementSaveCts != null) return;
        CancelMovementDeleteCheck();
        int steps = _ridingMount ? 3 : 2;
        var cells = new Point[steps];
        for (int i = 0; i < steps; i++)
        {
            cells[i] = Functions.PointMove(_currentLocation, direction, i + 1);
            if (RecentlyChangedMap && IsKnownMovementCell(cells[i]))
                return;
        }
        foreach (var cell in cells)
            await TryOpenDoorAsync(cell);
        var target = cells[^1];
        Log($"I am running to {target.X}, {target.Y}");
        _pendingMoveTarget = target;
        _pendingMovementAction.Clear();
        foreach (var cell in cells)
            AddPendingMovementCell(cell);
        var run = new C.Run { Direction = direction };
        await SendAsync(run);
        _lastMoveTime = DateTime.UtcNow;
    }

    private bool IsCellBlocked(Point p)
    {
        if (_mapData == null || !_mapData.IsWalkable(p.X, p.Y))
            return true;

        return _blockingCells.ContainsKey(p);
    }

    private async Task TryOpenDoorAsync(Point p)
    {
        if (_mapData == null) return;
        byte door = _mapData.GetDoorIndex(p.X, p.Y);
        if (door > 0)
        {
            Log($"I am opening door {door} at {p.X}, {p.Y}");
            await SendAsync(new C.Opendoor { DoorIndex = door });
        }
    }

    public bool CanWalk(MirDirection direction)
    {
        var target = Functions.PointMove(_currentLocation, direction, 1);
        if (RecentlyChangedMap && IsKnownMovementCell(target))
            return false;
        return !IsCellBlocked(target);
    }

    public bool CanRun(MirDirection direction)
    {
        if (!_canRun) return false;
        var now = DateTime.UtcNow;
        if (now - _lastMoveTime > TimeSpan.FromMilliseconds(900)) return false;

        int steps = _ridingMount ? 3 : 2;
        for (int i = 1; i <= steps; i++)
        {
            var cell = Functions.PointMove(_currentLocation, direction, i);
            if (RecentlyChangedMap && IsKnownMovementCell(cell))
                return false;
            if (IsCellBlocked(cell))
                return false;
        }
        return true;
    }

    public async Task AttackAsync(MirDirection direction, Spell spell = Spell.None)
    {
        if (_stream == null) return;
        var attack = new C.Attack { Direction = direction, Spell = spell };
        if (spell == Spell.Slaying)
            _slaying = false;
        await SendAsync(attack);
    }

    public async Task ToggleSpellAsync(Spell spell, bool canUse = true)
    {
        if (_stream == null) return;
        if (!HasMagic(spell)) return;
        await SendAsync(new C.SpellToggle { Spell = spell, CanUse = canUse });
        if (spell == Spell.Slaying)
            _slaying = canUse;
        else if (spell == Spell.DoubleSlash)
            _doubleSlash = canUse;
        else if (spell == Spell.Thrusting)
            _thrusting = canUse;
    }

    public async Task RangeAttackAsync(MirDirection direction, Point targetLocation, uint targetId)
    {
        if (_stream == null) return;
        var attack = new C.RangeAttack
        {
            Direction = direction,
            Location = _currentLocation,
            TargetID = targetId,
            TargetLocation = targetLocation
        };
        await SendAsync(attack);
    }

    private static long GetSpellTimeDelay(Spell spell) => spell switch
    {
        Spell.ShoulderDash => 2500,
        Spell.BladeAvalanche => 1500,
        Spell.SlashingBurst => 1500,
        Spell.CounterAttack => 100,
        Spell.PoisonSword => 1500,
        Spell.HeavenlySword => 1200,
        Spell.CrescentSlash => 1500,
        Spell.FlashDash => 250,
        Spell.StraightShot => 1500,
        Spell.DoubleShot => 500,
        Spell.ExplosiveTrap => 1500,
        Spell.DelayedExplosion => 1500,
        Spell.BackStep => 2500,
        Spell.ElementalShot => 1500,
        Spell.BindingShot or Spell.VampireShot or Spell.PoisonShot or Spell.CrippleShot or Spell.NapalmShot or
        Spell.SummonVampire or Spell.SummonToad or Spell.SummonSnakes or Spell.Stonetrap => 1000,
        Spell.FlameField => 2500,
        _ => 1800
    };

    public async Task CastMagicAsync(Spell spell, MirDirection direction, Point targetLocation, uint targetId)
    {
        if (_stream == null) return;
        var magicInfo = _magics.FirstOrDefault(m => m.Spell == spell);
        if (magicInfo == null) return;

        long now = Environment.TickCount64;
        if (now < _spellTime)
        {
            long remaining = _spellTime - now;
            long seconds = (remaining - 1) / 1000 + 1;
            Log($"Cannot cast any spell for another {seconds} seconds.");
            return;
        }

        if (now <= magicInfo.CastTime + magicInfo.Delay)
        {
            long remaining = magicInfo.CastTime + magicInfo.Delay - now;
            long seconds = (remaining - 1) / 1000 + 1;
            Log($"Cannot cast {spell} for another {seconds} seconds.");
            return;
        }

        var magic = new C.Magic
        {
            ObjectID = _objectId,
            Spell = spell,
            Direction = direction,
            TargetID = targetId,
            Location = targetLocation,
            SpellTargetLock = false
        };
        await SendAsync(magic);
        magicInfo.CastTime = now;
        _spellTime = now + GetSpellTimeDelay(spell);
    }

    public async Task TurnAsync(MirDirection direction)
    {
        if (_stream == null) return;
        if (_movementSaveCts != null) return;
        _pendingMoveTarget = _currentLocation;
        _pendingMovementAction.Clear();
        AddPendingMovementCell(_currentLocation);
        await SendAsync(new C.Turn { Direction = direction });
        MaybeStartMovementDeleteCheck();
    }

    public async Task TownReviveAsync()
    {
        if (_stream == null) return;
        await SendAsync(new C.TownRevive());
    }

    public async Task EquipItemAsync(UserItem item, EquipmentSlot slot)
    {
        if (_stream == null) return;
        bool remount = _ridingMount;
        if (remount)
            await EnsureUnmountedAsync();

        var equip = new C.EquipItem
        {
            Grid = MirGridType.Inventory,
            UniqueID = item.UniqueID,
            To = (int)slot
        };
        await SendAsync(equip);

        if (remount)
            await EnsureMountedAsync();
    }

    public async Task EquipMountItemAsync(UserItem item, MountSlot slot)
    {
        if (_stream == null || _equipment == null) return;
        bool remount = _ridingMount;
        if (remount)
            await EnsureUnmountedAsync();

        var mount = _equipment.Length > (int)EquipmentSlot.Mount ? _equipment[(int)EquipmentSlot.Mount] : null;
        if (mount == null)
        {
            if (remount)
                await EnsureMountedAsync();
            return;
        }
        var equip = new C.EquipSlotItem
        {
            Grid = MirGridType.Inventory,
            UniqueID = item.UniqueID,
            To = (int)slot,
            GridTo = MirGridType.Mount,
            ToUniqueID = mount.UniqueID
        };
        await SendAsync(equip);

        if (remount)
            await EnsureMountedAsync();
    }

    private int FindFreeInventorySlot() =>
        _inventory == null ? -1 : Array.FindIndex(_inventory, item => item == null);

    public async Task UnequipItemAsync(EquipmentSlot slot)
    {
        if (_stream == null || _equipment == null) return;
        bool remount = _ridingMount;
        if (remount)
            await EnsureUnmountedAsync();

        var item = _equipment[(int)slot];
        if (item == null)
        {
            if (remount)
                await EnsureMountedAsync();
            return;
        }
        int index = FindFreeInventorySlot();
        if (index < 0)
        {
            if (remount)
                await EnsureMountedAsync();
            return;
        }
        Log($"Unequipping {item.Info.Name}...");
        var remove = new C.RemoveItem
        {
            Grid = MirGridType.Inventory,
            UniqueID = item.UniqueID,
            To = index
        };

        await SendAsync(remove);

        if (remount)
            await EnsureMountedAsync();
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
            case RequiredType.MaxDC:
                if (GetStatTotal(Stat.MaxDC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MaxMC:
                if (GetStatTotal(Stat.MaxMC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MaxSC:
                if (GetStatTotal(Stat.MaxSC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MinDC:
                if (GetStatTotal(Stat.MinDC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MinMC:
                if (GetStatTotal(Stat.MinMC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MinSC:
                if (GetStatTotal(Stat.MinSC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MaxLevel:
                if (_level > item.Info.RequiredAmount) return false;
                break;
        }

        if (_equipment != null)
        {
            var current = _equipment.Length > (int)slot ? _equipment[(int)slot] : null;
            if (item.Info.Type == ItemType.Weapon || item.Info.Type == ItemType.Torch)
            {
                int weight = GetCurrentHandWeight();
                if (current?.Info != null)
                    weight -= current.Weight;
                if (weight + item.Weight > GetMaxHandWeight())
                    return false;
            }
            else
            {
                int weight = GetCurrentWearWeight();
                if (current?.Info != null)
                    weight -= current.Weight;
                if (weight + item.Weight > GetMaxWearWeight())
                    return false;
            }
        }

        return true;
    }

    public bool CanEquipMountItem(UserItem item, MountSlot slot)
    {
        if (_playerClass == null) return false;
        if (item.Info == null) return false;
        if (!IsItemForMountSlot(item.Info, slot)) return false;

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
            case RequiredType.MaxDC:
                if (GetStatTotal(Stat.MaxDC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MaxMC:
                if (GetStatTotal(Stat.MaxMC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MaxSC:
                if (GetStatTotal(Stat.MaxSC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MinDC:
                if (GetStatTotal(Stat.MinDC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MinMC:
                if (GetStatTotal(Stat.MinMC) < item.Info.RequiredAmount) return false;
                break;
            case RequiredType.MinSC:
                if (GetStatTotal(Stat.MinSC) < item.Info.RequiredAmount) return false;
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

    private static bool IsItemForMountSlot(ItemInfo info, MountSlot slot)
    {
        return slot switch
        {
            MountSlot.Reins => info.Type == ItemType.Reins,
            MountSlot.Bells => info.Type == ItemType.Bells,
            MountSlot.Saddle => info.Type == ItemType.Saddle,
            MountSlot.Ribbon => info.Type == ItemType.Ribbon,
            MountSlot.Mask => info.Type == ItemType.Mask,
            _ => false
        };
    }

    public async Task EnsureMountedAsync()
    {
        if (_stream == null || _equipment == null) return;
        if (_ridingMount) return;

        var mount = _equipment.Length > (int)EquipmentSlot.Mount ? _equipment[(int)EquipmentSlot.Mount] : null;
        if (mount == null) return;
        if (mount.Slots.Length <= (int)MountSlot.Saddle || mount.Slots[(int)MountSlot.Saddle] == null) return;

        await SendChatAsync("@ride");
        _ridingMount = true;
    }

    public async Task EnsureUnmountedAsync()
    {
        if (_stream == null) return;
        if (!_ridingMount) return;

        await SendChatAsync("@ride");
        _ridingMount = false;
    }

    public async Task ChangePetModeAsync(PetMode mode)
    {
        if (_stream == null) return;
        await SendAsync(new C.ChangePMode { Mode = mode });
    }

    public async Task PickUpAsync()
    {
        if (_stream == null) return;
        await SendAsync(new C.PickUp());
    }

    public async Task HarvestAsync(MirDirection direction)
    {
        if (_stream == null) return;
        await SendAsync(new C.Harvest { Direction = direction });
    }

    public async Task UseItemAsync(UserItem item)
    {
        if (_stream == null) return;
        bool remount = _ridingMount;
        if (remount)
            await EnsureUnmountedAsync();

        var use = new C.UseItem
        {
            UniqueID = item.UniqueID,
            Grid = MirGridType.Inventory
        };
        await SendAsync(use);

        if (remount)
            await EnsureMountedAsync();
    }

    public async Task DropItemAsync(UserItem item)
    {
        if (_stream == null) return;
        var drop = new C.DropItem
        {
            UniqueID = item.UniqueID,
            Count = item.Count,
            HeroInventory = false
        };
        await SendAsync(drop);
    }

    public async Task<bool> StoreItemAsync(UserItem item)
    {
        if (_stream == null || _inventory == null || _storage == null) return false;
        int from = Array.FindIndex(_inventory, x => x != null && x.UniqueID == item.UniqueID);
        string itemName = item.Info?.FriendlyName ?? "item";
        if (from < 0)
        {
            LogError($"Failed to locate {itemName} in inventory for storage");
            _pendingStorage.Remove(item);
            UpdateLastStorageAction($"Could not find {itemName} in inventory");
            return false;
        }

        if (item.Info?.Bind.HasFlag(BindMode.DontStore) == true)
        {
            LogError($"Cannot store {itemName}; item is bound to inventory");
            _pendingStorage.Remove(item);
            UpdateLastStorageAction($"Cannot store {itemName}; binding restriction");
            return false;
        }

        int to = Array.FindIndex(_storage, x => x == null);
        if (to < 0)
        {
            LogError($"No free storage slots available for {itemName}");
            _pendingStorage.Remove(item);
            UpdateLastStorageAction($"No free storage slots for {itemName}");
            return false;
        }

        Log($"I am storing {itemName} from slot {from} to storage slot {to}");
        UpdateLastStorageAction($"Storing {itemName} from {from} to {to}");
        using var cts = new CancellationTokenSource(2000);
        var waitTask = WaitForStoreItemAsync(cts.Token);
        var store = new C.StoreItem { From = from, To = to };
        try
        {
            await SendAsync(store);
            bool result = await waitTask;
            if (result)
            {
                _pendingStorage.Remove(item);
                UpdateLastStorageAction($"Stored {itemName} to slot {to}");
            }
            else
            {
                LogError($"Server rejected store item request for {itemName}");
                _pendingStorage.Remove(item);
                UpdateLastStorageAction($"Server rejected storing {itemName}");
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            LogError($"Timed out waiting for store item response for {itemName}");
            _pendingStorage.Remove(item);
            UpdateLastStorageAction($"Timeout storing {itemName}");
            return false;
        }
    }

    public async Task<int> TakeBackItemAsync(int from)
    {
        if (_stream == null || _inventory == null || _storage == null) return -1;
        if (from < 0 || from >= _storage.Length) return -1;
        if (_storage[from] == null) return -1;
        int to = FindFreeInventorySlot();
        if (to < 0) return -1;
        Log($"I am taking back {_storage[from]?.Info?.FriendlyName ?? "item"} from storage slot {from} to inventory slot {to}");
        using var cts = new CancellationTokenSource(2000);
        var waitTask = WaitForTakeBackItemAsync(cts.Token);
        var req = new C.TakeBackItem { From = from, To = to };
        await SendAsync(req);
        try
        {
            bool result = await waitTask;
            if (result && _inventory != null)
            {
                var invItem = _inventory[to];
                if (invItem != null)
                    await EquipIfBetterAsync(invItem);
            }
            return result ? to : -1;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    public async Task SellItemAsync(UserItem item)
    {
        if (item.Info?.Bind.HasFlag(BindMode.DontSell) == true)
        {
            Log($"Skipping selling {item.Info?.FriendlyName ?? "item"}; binding restriction");
            return;
        }
        await SellItemAsync(item.UniqueID, item.Count);
    }

    public async Task SellItemAsync(ulong uniqueId, ushort count)
    {
        if (_stream == null) return;
        var sell = new C.SellItem
        {
            UniqueID = uniqueId,
            Count = count
        };
        await SendAsync(sell);
    }

    public async Task BuyItemAsync(ulong uniqueId, ushort count, PanelType type)
    {
        if (_stream == null) return;
        var buy = new C.BuyItem
        {
            ItemIndex = uniqueId,
            Count = count,
            Type = type
        };
        await SendAsync(buy);
    }

    public void StopMovement()
    {
        if (_pendingMoveTarget.HasValue && _trackedObjects.TryGetValue(_objectId, out var self))
        {
            FireAndForget(TurnAsync(self.Direction));
        }
        _pendingMoveTarget = null;
        _pendingMovementAction.Clear();
    }
}
