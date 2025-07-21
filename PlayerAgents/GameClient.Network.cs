using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using C = ClientPackets;
using S = ServerPackets;
using PlayerAgents.Map;

public partial class GameClient
{
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
                if (loc.Location == _currentLocation)
                {
                    // movement request denied, revert to walking
                    _canRun = false;
                }
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
                    if (atk.Type == ObjectType.Player && atk.Id != _objectId && target.Type == ObjectType.Monster)
                    {
                        target.EngagedWith = atk.Id;
                        target.LastEngagedTime = DateTime.UtcNow;
                    }
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
            case S.Death death:
                Console.WriteLine("I have died.");
                _dead = true;
                _currentLocation = death.Location;
                break;
            case S.ObjectDied od:
                if (_trackedObjects.TryGetValue(od.ObjectID, out var objD))
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
