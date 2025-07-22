using System;
using System.Drawing;
using C = ClientPackets;

public sealed partial class GameClient
{
    public async Task WalkAsync(MirDirection direction)
    {
        if (_stream == null) return;
        var target = Functions.PointMove(_currentLocation, direction, 1);
        await TryOpenDoorAsync(target);
        Console.WriteLine($"I am walking to {target.X}, {target.Y}");
        var walk = new C.Walk { Direction = direction };
        await SendAsync(walk);
        _lastMoveTime = DateTime.UtcNow;
        _canRun = true;
    }

    public async Task RunAsync(MirDirection direction)
    {
        if (_stream == null) return;
        var first = Functions.PointMove(_currentLocation, direction, 1);
        var target = Functions.PointMove(_currentLocation, direction, 2);
        await TryOpenDoorAsync(first);
        await TryOpenDoorAsync(target);
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

    private async Task TryOpenDoorAsync(Point p)
    {
        if (_mapData == null) return;
        byte door = _mapData.GetDoorIndex(p.X, p.Y);
        if (door > 0)
            await SendAsync(new C.Opendoor { DoorIndex = door });
    }

    public bool CanWalk(MirDirection direction)
    {
        var target = Functions.PointMove(_currentLocation, direction, 1);
        return !IsCellBlocked(target);
    }

    public bool CanRun(MirDirection direction)
    {
        if (!_canRun) return false;
        var now = DateTime.UtcNow;
        if (now - _lastMoveTime > TimeSpan.FromMilliseconds(900)) return false;

        var first = Functions.PointMove(_currentLocation, direction, 1);
        var second = Functions.PointMove(_currentLocation, direction, 2);

        return !(IsCellBlocked(first) || IsCellBlocked(second));
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

    private int FindFreeInventorySlot() =>
        _inventory == null ? -1 : Array.FindIndex(_inventory, item => item == null);

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
        var use = new C.UseItem
        {
            UniqueID = item.UniqueID,
            Grid = MirGridType.Inventory
        };
        await SendAsync(use);
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
}
