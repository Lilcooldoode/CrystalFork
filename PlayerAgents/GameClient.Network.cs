using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using System.Drawing;
using C = ClientPackets;
using S = ServerPackets;
using Shared;
using PlayerAgents.Map;

public sealed partial class GameClient
{
    private async Task SendAsync(Packet p)
    {
        if (_stream == null) return;
        var data = p.GetPacketBytes().ToArray();
        await _sendLock.WaitAsync();
        try
        {
            if (_stream != null)
                await _stream.WriteAsync(data, 0, data.Length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendChatAsync(string message)
    {
        if (_stream == null) return;
        await WaitForChatSlotAsync();
        await SendAsync(new C.Chat { Message = message });
    }

    private async Task WaitForChatSlotAsync()
    {
        while (true)
        {
            TimeSpan wait = TimeSpan.Zero;
            await _chatThrottleLock.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                while (_chatSendTimes.Count > 0 && now - _chatSendTimes.Peek() > ChatThrottleWindow)
                    _chatSendTimes.Dequeue();

                if (_chatSendTimes.Count < ChatThrottleLimit)
                {
                    _chatSendTimes.Enqueue(now);
                    return;
                }

                var oldest = _chatSendTimes.Peek();
                wait = ChatThrottleWindow - (now - oldest);
                if (wait < TimeSpan.Zero)
                    wait = TimeSpan.Zero;
            }
            finally
            {
                _chatThrottleLock.Release();
            }

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
            else
                await Task.Yield();
        }
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
                _receiveStream.Position = _receiveStream.Length;
                _receiveStream.Write(_buffer, 0, count);

                Packet? p;
                byte[] data = _receiveStream.ToArray();
                while ((p = Packet.ReceivePacket(data, out data)) != null)
                {
                    HandlePacket(p);
                }
                _receiveStream.SetLength(0);
                _receiveStream.Write(data, 0, data.Length);
            }
        }
        catch (Exception ex)
        {
            Log($"Receive error: {ex.Message}");
        }
    }

    private void HandlePacket(Packet p)
    {
        switch (p)
        {
            case S.Login l:
                if (l.Result == 3)
                {
                    Log("Account not found, creating...");
                    _ = CreateAccountAsync();
                }
                else if (l.Result != 4)
                {
                    Log($"Login failed: {l.Result}");
                }
                else
                {
                    Log("Wrong password");
                }
                break;
            case S.NewAccount na:
                if (na.Result == 8)
                {
                    Log("Account created");
                    _ = LoginAsync();
                }
                else
                {
                    Log($"Account creation failed: {na.Result}");
                }
                break;
            case S.LoginSuccess ls:
                var match = ls.Characters.FirstOrDefault(c => c.Name.Equals(_config.CharacterName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Log($"Character '{_config.CharacterName}' not found, creating...");
                    _ = CreateCharacterAsync();
                }
                else
                {
                    Log($"Selected character '{match.Name}' (Index {match.Index})");
                    var start = new C.StartGame { CharacterIndex = match.Index };
                    FireAndForget(Task.Run(async () => { await RandomStartupDelayAsync(); await SendAsync(start); }));
                }
                break;
            case S.NewCharacterSuccess ncs:
                Log("Character created");
                var startNew = new C.StartGame { CharacterIndex = ncs.CharInfo.Index };
                FireAndForget(Task.Run(async () => { await RandomStartupDelayAsync(); await SendAsync(startNew); }));
                break;
            case S.NewCharacter nc:
                Log($"Character creation failed: {nc.Result}");
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
                Log($"StartGame Result: {sg.Result} ({reason})");
                break;
            case S.StartGameBanned ban:
                Log($"StartGame Banned: {ban.Reason} until {ban.ExpiryDate}");
                break;
            case S.StartGameDelay delay:
                Log($"StartGame delayed for {delay.Milliseconds} ms");
                break;
            case S.ObjectTeleportOut oto:
                if (oto.ObjectID == _objectId)
                {
                    // keep pending move target so MapChanged can record the
                    // transition source correctly
                }
                break;
            case S.ObjectTeleportIn oti:
                if (oti.ObjectID == _objectId)
                {
                    _movementSaveCts?.Cancel();
                    _movementSaveCts = null;
                }
                break;
            case S.TeleportIn:
                // do not clear pending move target here so any upcoming MapChanged
                // event will use the intended move location
                break;
            case S.MapInformation mi:
                _currentMapFile = Path.Combine(MapManager.MapDirectory, mi.FileName + ".map");
                _currentMapName = mi.Title;
                _mapLight = mi.Lights;
                _mapDarkLight = mi.MapDarkLight;
                _ = LoadMapAsync();
                StartMapExpTracking(_currentMapFile);
                _lastMapChangeTime = DateTime.UtcNow;
                DeliverMapChanged();
                ReportStatus();
                if (Travelling)
                    FireAndForget(EnsureMountedAsync());
                break;
            case S.SwitchGroup sg:
                _allowGroup = sg.AllowGroup;
                break;
            case S.DeleteGroup:
                _groupMembers.Clear();
                UpdateGroupLeader();
                StartMapExpTracking(_currentMapFile);
                break;
            case S.DeleteMember dm:
                _groupMembers.Remove(dm.Name);
                UpdateGroupLeader();
                if (_groupMembers.Count == 0)
                    StartMapExpTracking(_currentMapFile);
                break;
            case S.AddMember am:
                bool wasGrouped = IsGrouped;
                if (!_groupMembers.Contains(am.Name))
                    _groupMembers.Add(am.Name);
                UpdateGroupLeader();
                if (!wasGrouped && IsGrouped)
                    PauseMapExpTracking();
                break;
            case S.GroupInvite gi:
                bool hasExpRates = _playerClass != null && _expRateMemory.HasRates(_playerClass.Value, _level);
                if (!IsGrouped && _allowGroup && _groupMembers.Count < _personality.MaxGroupCount && !Travelling && CurrentNpcInteraction == NpcInteractionType.General && hasExpRates)
                    FireAndForget(SendAsync(new C.GroupInvite { AcceptInvite = true }));
                else
                    FireAndForget(SendAsync(new C.GroupInvite { AcceptInvite = false }));
                break;
            case S.MapChanged mc:
                if (!string.IsNullOrEmpty(_currentMapFile) && !_dead && _pendingMovementAction.Count > 0)
                {
                    var srcFile = _currentMapFile;
                    var destFile = mc.FileName;
                    var destLoc = mc.Location;
                    var pending = _pendingMovementAction.ToList();
                    _movementSaveCts?.Cancel();
                    var cts = new CancellationTokenSource();
                    _movementSaveCts = cts;
                    FireAndForget(Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                            if (!cts.IsCancellationRequested)
                                foreach (var srcLoc in pending)
                                    _movementMemory.AddMovement(srcFile, srcLoc, destFile, destLoc);
                        }
                        catch (TaskCanceledException) { }
                        finally
                        {
                            _pendingMoveTarget = null;
                            _pendingMovementAction.Clear();
                            if (ReferenceEquals(_movementSaveCts, cts))
                                _movementSaveCts = null;
                        }
                    }));
                }
                CancelMovementDeleteCheck();
                PauseMapExpTracking();
                _currentMapFile = Path.Combine(MapManager.MapDirectory, mc.FileName + ".map");
                _currentMapName = mc.Title;
                _mapLight = mc.Lights;
                _mapDarkLight = mc.MapDarkLight;
                _currentLocation = mc.Location;
                _navData?.Remove(_currentLocation);
                _trackedObjects.Clear();
                _blockingCells.Clear();
                _ = LoadMapAsync();
                StartMapExpTracking(_currentMapFile);
                _lastMapChangeTime = DateTime.UtcNow;
                DeliverMapChanged();
                if (_awaitingHarvest)
                    _harvestComplete = true;
                ReportStatus();
                if (Travelling)
                    FireAndForget(EnsureMountedAsync());
                break;
            case S.UserInformation info:
                CancelMovementDeleteCheck();
                _objectId = info.ObjectID;
                _playerClass = info.Class;
                _baseStats = new BaseStats(info.Class);
                _playerName = info.Name;
                _currentLocation = info.Location;
                _navData?.Remove(_currentLocation);
                _gender = info.Gender;
                _level = info.Level;
                _experience = info.Experience;
                _hp = info.HP;
                _mp = info.MP;
                _inventory = info.Inventory;
                _equipment = info.Equipment;
                _gold = info.Gold;
                _magics.Clear();
                _magics.AddRange(info.Magics);
                BindAll(_inventory);
                BindAll(_equipment);
                MarkStatsDirty();
                Log($"Logged in as {_playerName}");
                Log($"I am currently at location {_currentLocation.X}, {_currentLocation.Y}");
                _classTcs.TrySetResult(info.Class);
                ReportStatus();
                break;
            case S.BaseStatsInfo bsi:
                _baseStats = bsi.Stats;
                MarkStatsDirty();
                break;
            case S.UserLocation loc:
                if (loc.Location == _currentLocation)
                {
                    // movement request denied, revert to walking
                    _canRun = false;
                }
                if (_pendingMoveTarget.HasValue && loc.Location != _pendingMoveTarget.Value)
                {
                    _movementSaveCts?.Cancel();
                    _movementSaveCts = null;
                    _pendingMoveTarget = null;
                    _pendingMovementAction.Clear();
                }
                if (loc.Location != _currentLocation)
                    CancelMovementDeleteCheck();
                _currentLocation = loc.Location;
                _navData?.Remove(_currentLocation);
                ReportStatus();
                break;
            case S.TimeOfDay tod:
                _timeOfDay = tod.Lights;
                break;
            case S.ObjectPlayer op:
                var playerObj = new TrackedObject(op.ObjectID, ObjectType.Player, op.Name, op.Location, op.Direction);
                playerObj.Poison = op.Poison;
                AddTrackedObject(playerObj);
                if (op.ObjectID == _objectId)
                {
                    _elementLevel = (int)op.ElementOrbLvl;
                    _elementCount = (int)op.ElementOrbEffect;
                    _hasElements = _elementCount > 0;
                }
                break;
            case S.ObjectMonster om:
                var monsterObj = new TrackedObject(om.ObjectID, ObjectType.Monster, om.Name, om.Location, om.Direction, om.AI, om.Dead);
                monsterObj.Tamed = IsTamedName(om.Name);
                monsterObj.Poison = om.Poison;
                AddTrackedObject(monsterObj);
                if (!monsterObj.Tamed && !string.IsNullOrEmpty(om.Name) && !string.IsNullOrEmpty(_currentMapFile))
                    _monsterMemory.AddSeenOnMap(om.Name, _currentMapFile);
                break;
            case S.ObjectNPC on:
                AddTrackedObject(new TrackedObject(on.ObjectID, ObjectType.Merchant, on.Name, on.Location, on.Direction));
                _navData?.Remove(on.Location);
                if (!string.IsNullOrEmpty(_currentMapFile))
                {
                    var mapId = Path.GetFileNameWithoutExtension(_currentMapFile);
                    var entry = _npcMemory.AddNpc(on.Name, mapId, on.Location);
                    _npcEntries[on.ObjectID] = entry;

                    foreach (var kv in _recentNpcInteractions.ToList())
                        if (DateTime.UtcNow - kv.Value >= TimeSpan.FromSeconds(10))
                            _recentNpcInteractions.Remove(kv.Key);

                    var key = (entry.Name, entry.MapFile, entry.X, entry.Y);
                    if (!_recentNpcInteractions.TryGetValue(key, out var last) || DateTime.UtcNow - last >= TimeSpan.FromSeconds(10))
                    {
                        if (!IgnoreNpcInteractions && NeedsNpcInteraction(entry) && !IsNpcIgnored(entry))
                        {
                            if (!_npcQueue.Contains(on.ObjectID))
                                _npcQueue.Enqueue(on.ObjectID);
                        }
                    }
                }
                break;
            case S.ObjectItem oi:
                AddTrackedObject(new TrackedObject(oi.ObjectID, ObjectType.Item, oi.Name, oi.Location, MirDirection.Up));
                break;
            case S.ObjectGold og:
                AddTrackedObject(new TrackedObject(og.ObjectID, ObjectType.Item, "Gold", og.Location, MirDirection.Up));
                break;
            case S.ObjectSpell os:
                AddTrackedObject(new TrackedObject(os.ObjectID, ObjectType.Spell, os.Spell.ToString(), os.Location, os.Direction, spell: os.Spell));
                break;
            case S.ObjectTurn ot:
                UpdateTrackedObject(ot.ObjectID, ot.Location, ot.Direction);
                break;
            case S.ObjectWalk ow:
                UpdateTrackedObject(ow.ObjectID, ow.Location, ow.Direction);
                if (ow.ObjectID == _objectId)
                {
                    CancelMovementDeleteCheck();
                    _currentLocation = ow.Location;
                    _lastMoveTime = DateTime.UtcNow;
                    _canRun = true;
                    _pendingMoveTarget = null;
                    _pendingMovementAction.Clear();
                    _navData?.Remove(_currentLocation);
                }
                break;
            case S.ObjectRun oru:
                UpdateTrackedObject(oru.ObjectID, oru.Location, oru.Direction);
                if (oru.ObjectID == _objectId)
                {
                    CancelMovementDeleteCheck();
                    _currentLocation = oru.Location;
                    _lastMoveTime = DateTime.UtcNow;
                    _pendingMoveTarget = null;
                    _pendingMovementAction.Clear();
                    _navData?.Remove(_currentLocation);
                }
                break;
            case S.Pushed push:
                CancelMovementDeleteCheck();
                _currentLocation = push.Location;
                _pendingMoveTarget = null;
                _pendingMovementAction.Clear();
                break;
            case S.ObjectPushed opu:
                UpdateTrackedObject(opu.ObjectID, opu.Location, opu.Direction);
                _pushedObjects[opu.ObjectID] = (opu.Direction, DateTime.UtcNow);
                if (opu.ObjectID == _objectId)
                {
                    CancelMovementDeleteCheck();
                    _currentLocation = opu.Location;
                    _pendingMoveTarget = null;
                    _pendingMovementAction.Clear();
                }
                break;
            case S.Struck st:
                _lastStruckAttacker = st.AttackerID;
                break;
            case S.ObjectStruck os:
                if (os.AttackerID == _objectId)
                {
                    _lastAttackTarget = os.ObjectID;
                    if (_trackedObjects.TryGetValue(os.ObjectID, out var targ) && targ.Type == ObjectType.Monster && !string.IsNullOrEmpty(targ.Name))
                    {
                        targ.EngagedWith = _objectId;
                        targ.LastEngagedTime = DateTime.UtcNow;
                    }
                }
                else if (_trackedObjects.TryGetValue(os.AttackerID, out var atk) &&
                         _trackedObjects.TryGetValue(os.ObjectID, out var target))
                {
                    if (atk.Type == ObjectType.Player && atk.Id != _objectId && target.Type == ObjectType.Monster && !string.IsNullOrEmpty(target.Name))
                    {
                        target.EngagedWith = atk.Id;
                        target.LastEngagedTime = DateTime.UtcNow;
                    }
                    else if (atk.Type == ObjectType.Monster && !string.IsNullOrEmpty(atk.Name) && target.Type == ObjectType.Player && target.Id != _objectId)
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
                    Log($"{name} has attacked me for {-di.Damage} damage");
                    int recordDamage = -di.Damage + GetStatTotal(Stat.MaxAC);
                    if (atk == null || !atk.Tamed)
                        _monsterMemory.RecordDamage(name, recordDamage);
                    _lastStruckAttacker = null;
                }
                else if (_lastAttackTarget.HasValue && di.ObjectID == _lastAttackTarget.Value)
                {
                    if (di.Type == DamageType.Miss)
                    {
                        if (_trackedObjects.TryGetValue(di.ObjectID, out var targ))
                            Log($"I attacked {targ.Name} and missed");
                        else
                            Log("I attacked an unknown target and missed");
                    }
                    else
                    {
                        string name = _trackedObjects.TryGetValue(di.ObjectID, out var targ) ? targ.Name : "Unknown";
                        Log($"I have damaged {name} for {-di.Damage} damage");
                    }
                    _lastAttackTarget = null;
                }
                break;
            case S.ObjectHealth oh:
                if (_trackedObjects.TryGetValue(oh.ObjectID, out var objH))
                    objH.HealthPercent = oh.Percent;
                break;
            case S.Death death:
                Log("I have died.");
                _dead = true;
                CancelMovementDeleteCheck();
                _currentLocation = death.Location;
                _navData?.Remove(_currentLocation);
                PlayerDied?.Invoke();

                if (_equipment != null)
                {
                    for (int i = 0; i < _equipment.Length; i++)
                    {
                        var ei = _equipment[i];
                        if (ei?.Info != null && ei.Info.Bind.HasFlag(BindMode.BreakOnDeath))
                        {
                            Log($"Deleting {ei.Info.Name} due to BreakOnDeath");
                            _equipment[i] = null;
                            RemoveFromPendingStorage(ei.UniqueID);
                            _pendingSellChecks.Remove(ei.UniqueID);
                            _pendingRepairChecks.Remove(ei.UniqueID);
                        }
                    }
                }

                break;
            case S.ObjectDied od:
                if (_trackedObjects.TryGetValue(od.ObjectID, out var objD))
                {
                    bool wasBlocking = IsBlocking(objD);
                    objD.Dead = true;
                    if (wasBlocking)
                        _blockingCells.TryRemove(objD.Location, out _);
                    if (objD.Type == ObjectType.Monster)
                        MonsterDied?.Invoke(od.ObjectID);
                    if (objD.Type == ObjectType.Monster && !string.IsNullOrEmpty(objD.Name) && AutoHarvestAIs.Contains(objD.AI) && objD.EngagedWith == _objectId)
                    {
                        FireAndForget(Task.Run(async () => await HarvestLoopAsync(objD)));
                    }
                    else if (objD.Type == ObjectType.Player && od.ObjectID != _objectId)
                    {
                        foreach (var m in _trackedObjects.Values)
                        {
                            if (m.Type == ObjectType.Monster && !string.IsNullOrEmpty(m.Name) && m.EngagedWith == od.ObjectID)
                            {
                                m.EngagedWith = null;
                                if (_awaitingHarvest && !m.Dead && Functions.MaxDistance(_currentLocation, m.Location) <= 2)
                                    CancelHarvesting();
                            }
                        }
                    }
                }
                if (_tameTargetId.HasValue && od.ObjectID == _tameTargetId.Value)
                    _tameTargetId = null;
                break;
            case S.ObjectName on:
                if (_trackedObjects.TryGetValue(on.ObjectID, out var objN))
                {
                    var oldName = objN.Name;
                    objN.Name = on.Name;
                    objN.Tamed = IsTamedName(on.Name);
                    if (_tameTargetId.HasValue && on.ObjectID == _tameTargetId.Value &&
                        objN.Type == ObjectType.Monster && oldName != on.Name && objN.Tamed)
                    {
                        MonsterMemory.SetCanTame(oldName);
                        _tameTargetId = null;
                    }
                }
                MonsterNameChanged?.Invoke(on.ObjectID, on.Name);
                break;
            case S.ObjectColourChanged oc:
                if (_trackedObjects.TryGetValue(oc.ObjectID, out var objC))
                {
                    if (_tameTargetId.HasValue && oc.ObjectID == _tameTargetId.Value &&
                        objC.Type == ObjectType.Monster)
                {
                    if (IsTamedName(objC.Name) || oc.NameColour == Color.Red)
                    {
                        MonsterMemory.SetCanTame(objC.Name);
                        _tameTargetId = null;
                    }
                }
                    if (objC.Type == ObjectType.Monster)
                        objC.Tamed = IsTamedName(objC.Name);
                }
                MonsterColourChanged?.Invoke(oc.ObjectID, oc.NameColour);
                break;
            case S.ObjectPoisoned opoi:
                if (_trackedObjects.TryGetValue(opoi.ObjectID, out var objP))
                    objP.Poison = opoi.Poison;
                break;
            case S.SetElemental se:
                if (se.ObjectID == _objectId)
                {
                    _hasElements = se.Enabled;
                    _elementLevel = (int)se.Value;
                    if (se.ElementType > 0)
                        _elementCount = (int)se.ElementType;
                    else if (!_hasElements)
                        _elementCount = 0;
                }
                break;
            case S.ObjectHarvested oh:
                UpdateTrackedObject(oh.ObjectID, oh.Location, oh.Direction);
                if (_harvestTargetId.HasValue && oh.ObjectID == _harvestTargetId.Value)
                    _harvestComplete = true;
                break;
            case S.ObjectRemove ore:
                RemoveTrackedObject(ore.ObjectID);
                if (_harvestTargetId.HasValue && ore.ObjectID == _harvestTargetId.Value)
                    _harvestComplete = true;
                if (_lastAttackTarget.HasValue && ore.ObjectID == _lastAttackTarget.Value)
                    _lastAttackTarget = null;
                if (_tameTargetId.HasValue && ore.ObjectID == _tameTargetId.Value)
                    _tameTargetId = null;
                break;
            case S.Revived:
                _movementSaveCts?.Cancel();
                _movementSaveCts = null;
                if (_dead)
                {
                    Log("I have been revived.");
                }
                _dead = false;
                _hp = GetMaxHP();
                _mp = GetMaxMP();
                break;
            case S.ObjectRevived orv:
                if (orv.ObjectID == _objectId)
                {
                    if (_dead)
                    {
                        Log("I have been revived.");
                    }
                    _dead = false;
                    _hp = GetMaxHP();
                    _mp = GetMaxMP();
                }
                else if (_trackedObjects.TryGetValue(orv.ObjectID, out var objRev))
                {
                    objRev.Dead = false;
                    if (IsBlocking(objRev))
                        _blockingCells.AddOrUpdate(objRev.Location, 1, (_, v) => v + 1);
                }
                break;
            case S.ObjectHide oh:
                SetTrackedObjectHidden(oh.ObjectID, true);
                break;
            case S.ObjectShow os:
                SetTrackedObjectHidden(os.ObjectID, false);
                break;
            case S.NewItemInfo nii:
                ItemInfoDict[nii.Info.Index] = nii.Info;
                break;
            case S.GainedItem gi:
                Bind(gi.Item);
                UserItem? invItem = null;
                invItem = AddItem(gi.Item);
                _lastPickedItem = invItem ?? gi.Item;
                if (gi.Item.Info != null)
                    Log($"Gained item: {gi.Item.Info.FriendlyName}");
                // Let the AI handle overweight checks so it can track dropped items
                break;
            case S.GainedGold gg:
                _gold += gg.Gold;
                Log($"Gained {gg.Gold} gold");
                break;
            case S.GainExperience ge:
                _experience += ge.Amount;
                _mapExpGained += ge.Amount;
                Log($"I gained {ge.Amount} experience");
                break;
            case S.LevelChanged lc:
                _level = lc.Level;
                _experience = lc.Experience;
                MarkStatsDirty();
                ReportStatus();
                // reset exp rate tracking when our level changes
                if (!string.IsNullOrEmpty(_currentMapFile))
                    StartMapExpTracking(_currentMapFile);
                break;
            case S.Chat chat:
                HandleTradeFailChat(chat.Message);
                if (chat.Type == ChatType.System &&
                    (chat.Message.Contains("Can not pick up", StringComparison.OrdinalIgnoreCase) ||
                     chat.Message.Contains("cannot pick up", StringComparison.OrdinalIgnoreCase) ||
                     chat.Message.Contains("do not own this item", StringComparison.OrdinalIgnoreCase)))
                {
                    PickUpFailed?.Invoke();
                }
                if (chat.Type == ChatType.WhisperIn)
                    HandleWhisper(chat.Message);
                break;
            case S.ObjectChat oc:
                HandleTradeFailChat(oc.Text);
                if (oc.Type == ChatType.System &&
                    (oc.Text.Contains("Can not pick up", StringComparison.OrdinalIgnoreCase) ||
                     oc.Text.Contains("cannot pick up", StringComparison.OrdinalIgnoreCase) ||
                     oc.Text.Contains("do not own this item", StringComparison.OrdinalIgnoreCase)))
                {
                    PickUpFailed?.Invoke();
                }
                if (oc.Type == ChatType.WhisperIn)
                    HandleWhisper(oc.Text);
                break;
            case S.LoseGold lg:
                if (lg.Gold > _gold) _gold = 0;
                else _gold -= lg.Gold;
                Log($"Lost {lg.Gold} gold");
                break;
            case S.HealthChanged hc:
                _hp = hc.HP;
                _mp = hc.MP;
                _dead = hc.HP <= 0;
                ReportStatus();
                break;
            case S.UseItem ui:
                if (ui.Success && _inventory != null)
                {
                    int idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == ui.UniqueID);
                    if (idx >= 0)
                    {
                        var it = _inventory[idx];
                        if (it != null)
                        {
                            if (it.Count > 1)
                            {
                                it.Count--;
                            }
                            else
                            {
                                _inventory[idx] = null;
                                RemoveFromPendingStorage(ui.UniqueID);
                            }
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
                        RemoveFromPendingStorage(ei.UniqueID);
                        MarkStatsDirty();
                    }
                }
            break;
            case S.EquipSlotItem esi:
                if (esi.Grid == MirGridType.Inventory && esi.GridTo == MirGridType.Mount && esi.Success && _inventory != null && _equipment != null)
                {
                    int invIndex = Array.FindIndex(_inventory, x => x != null && x.UniqueID == esi.UniqueID);
                    var mount = _equipment.Length > (int)EquipmentSlot.Mount ? _equipment[(int)EquipmentSlot.Mount] : null;
                    if (invIndex >= 0 && mount != null && esi.To >= 0 && esi.To < mount.Slots.Length)
                    {
                        mount.Slots[esi.To] = _inventory[invIndex];
                        _inventory[invIndex] = null;
                        RemoveFromPendingStorage(esi.UniqueID);
                        MarkStatsDirty();
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
                        MarkStatsDirty();
                    }
                }
                break;
            case S.DropItem di:
                if (di.Success && _inventory != null)
                {
                    int idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == di.UniqueID);
                    if (idx >= 0)
                    {
                        var it = _inventory[idx];
                        if (it != null)
                        {
                            if (di.Count >= it.Count)
                            {
                                _inventory[idx] = null;
                                RemoveFromPendingStorage(di.UniqueID);
                            }
                            else
                            {
                                it.Count -= di.Count;
                            }
                        }
                    }
                    if (_lastPickedItem != null && _lastPickedItem.UniqueID == di.UniqueID)
                        _lastPickedItem = null;
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
                            {
                                it.Count -= di.Count;
                            }
                            else
                            {
                                _inventory[idx] = null;
                                RemoveFromPendingStorage(di.UniqueID);
                            }
                        }
                    }
                }
                if (_equipment != null)
                {
                    int idx = Array.FindIndex(_equipment, x => x != null && x.UniqueID == di.UniqueID);
                    if (idx >= 0)
                    {
                        var it = _equipment[idx];
                        if (it != null)
                        {
                            if (it.Count > di.Count)
                                it.Count -= di.Count;
                            else
                                _equipment[idx] = null;
                            MarkStatsDirty();
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
                    MarkStatsDirty();
                }
                break;
            case S.UserStorage us:
                _storage = us.Storage;
                BindAll(_storage);
                _storageLoadedTcs?.TrySetResult(true);
                _storageLoadedTcs = null;
                break;
            case S.ResizeStorage rs:
                if (_storage == null || _storage.Length != rs.Size)
                {
                    Array.Resize(ref _storage, rs.Size);
                }
                break;
            case S.StoreItem store:
                if (store.Success && _inventory != null && _storage != null)
                {
                    if (store.From >= 0 && store.From < _inventory.Length && store.To >= 0 && store.To < _storage.Length)
                    {
                        var moved = _inventory[store.From];
                        _storage[store.To] = moved;
                        _inventory[store.From] = null;
                        if (moved != null)
                            RemoveFromPendingStorage(moved.UniqueID);
                    }
                }
                _storeItemTcs?.TrySetResult(store.Success);
                _storeItemTcs = null;
                break;
            case S.TakeBackItem tb:
                if (tb.Success && _inventory != null && _storage != null)
                {
                    if (tb.From >= 0 && tb.From < _storage.Length && tb.To >= 0 && tb.To < _inventory.Length)
                    {
                        _inventory[tb.To] = _storage[tb.From];
                        _storage[tb.From] = null;
                    }
                }
                _takeBackItemTcs?.TrySetResult(tb.Success);
                _takeBackItemTcs = null;
                break;
            case S.NPCResponse nr:
                DeliverNpcResponse(nr);
                break;
            case S.NPCGoods goods:
                ProcessNpcGoods(goods.List, goods.Type);
                break;
            case S.NPCPearlGoods pearlGoods:
                ProcessNpcGoods(pearlGoods.List, pearlGoods.Type);
                break;
            case S.NPCStorage:
                if (_dialogNpcId.HasValue && _npcEntries.TryGetValue(_dialogNpcId.Value, out var npcStoreEntry))
                {
                    if (!npcStoreEntry.CanStore)
                    {
                        npcStoreEntry.CanStore = true;
                        _npcMemory.SaveChanges();
                    }
                }
                _userStorageTcs?.TrySetResult(true);
                _userStorageTcs = null;
                break;
            case S.NPCSell:
                if (_dialogNpcId.HasValue && _npcEntries.TryGetValue(_dialogNpcId.Value, out var npcSellEntry) && npcSellEntry.CanSell)
                {
                    if (HasUnknownSellTypes(npcSellEntry))
                    {
                        FireAndForget(Task.Run(async () => { await HandleNpcSellAsync(npcSellEntry); _npcSellTcs?.TrySetResult(true); _npcSellTcs = null; }));
                    }
                    else
                    {
                        ProcessNpcActionQueue();
                        _npcSellTcs?.TrySetResult(true);
                        _npcSellTcs = null;
                    }
                }
                else
                {
                    ProcessNpcActionQueue();
                    _npcSellTcs?.TrySetResult(true);
                    _npcSellTcs = null;
                }
                break;
            case S.NPCRepair:
            case S.NPCSRepair:
                if (_dialogNpcId.HasValue && _npcEntries.TryGetValue(_dialogNpcId.Value, out var npcRepairEntry))
                {
                    bool special = p is S.NPCSRepair;
                    if (HasUnknownRepairTypes(npcRepairEntry, special))
                    {
                        FireAndForget(Task.Run(async () => { await HandleNpcRepairAsync(npcRepairEntry, special); _npcRepairTcs?.TrySetResult(true); _npcRepairTcs = null; }));
                    }
                    else
                    {
                        ProcessNpcActionQueue();
                        _npcRepairTcs?.TrySetResult(true);
                        _npcRepairTcs = null;
                    }
                }
                break;
            case S.SellItem sell:
                if (_sellItemTcs.TryGetValue(sell.UniqueID, out var sellTcs))
                {
                    sellTcs.TrySetResult(sell);
                    _sellItemTcs.Remove(sell.UniqueID);
                }
                if (_pendingSellChecks.TryGetValue(sell.UniqueID, out var infoSell))
                {
                    if (sell.Success)
                    {
                        string itemName = string.Empty;
                        var inventoryItem = _inventory != null ? Array.Find(_inventory, x => x != null && x.UniqueID == sell.UniqueID) : null;
                        var eqItem = inventoryItem == null && _equipment != null ? Array.Find(_equipment, x => x != null && x.UniqueID == sell.UniqueID) : null;
                        var it = inventoryItem ?? eqItem;
                        if (it != null && it.Info != null) itemName = it.Info.FriendlyName;
                        infoSell.entry.SellItemTypes ??= new List<ItemType>();
                        if (!infoSell.entry.SellItemTypes.Contains(infoSell.type))
                        {
                            infoSell.entry.SellItemTypes.Add(infoSell.type);
                            _npcMemory.SaveChanges();
                        }
                        if (_inventory != null)
                        {
                            int idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == sell.UniqueID);
                            if (idx >= 0)
                            {
                                var item = _inventory[idx];
                                if (item != null)
                                {
                                    if (item.Count <= sell.Count)
                                    {
                                        _inventory[idx] = null;
                                        RemoveFromPendingStorage(sell.UniqueID);
                                    }
                                    else
                                    {
                                        item.Count -= sell.Count;
                                    }
                                }
                            }
                        }
                        if (_equipment != null)
                        {
                            int idx = Array.FindIndex(_equipment, x => x != null && x.UniqueID == sell.UniqueID);
                            if (idx >= 0)
                            {
                                var item = _equipment[idx];
                                if (item != null)
                                {
                                    if (item.Count <= sell.Count)
                                        _equipment[idx] = null;
                                    else
                                        item.Count -= sell.Count;
                                }
                            }
                        }
                        if (_lastPickedItem != null && _lastPickedItem.UniqueID == sell.UniqueID)
                            _lastPickedItem = null;
                        Log($"Sold {itemName} x{sell.Count} to {infoSell.entry.Name}");
                    }
                    _pendingSellChecks.Remove(sell.UniqueID);
                }
                break;
            case S.RepairItem repair:
                // Server acknowledges the repair request but success will be
                // indicated by ItemRepaired or a chat message.
                break;
            case S.ItemRepaired ir:
                if (_repairItemTcs.TryGetValue(ir.UniqueID, out var repTcs))
                {
                    repTcs.TrySetResult(true);
                    _repairItemTcs.Remove(ir.UniqueID);
                }
                if (_pendingRepairChecks.ContainsKey(ir.UniqueID))
                {
                    _pendingRepairChecks.Remove(ir.UniqueID);
                }
                bool eqChanged = false;
                if (_inventory != null)
                {
                    int idx = Array.FindIndex(_inventory, x => x != null && x.UniqueID == ir.UniqueID);
                    if (idx >= 0)
                    {
                        var item = _inventory[idx];
                        if (item != null)
                        {
                            item.MaxDura = ir.MaxDura;
                            item.CurrentDura = ir.CurrentDura;
                        }
                    }
                }
                if (_equipment != null)
                {
                    int idx = Array.FindIndex(_equipment, x => x != null && x.UniqueID == ir.UniqueID);
                    if (idx >= 0)
                    {
                        var item = _equipment[idx];
                        if (item != null)
                        {
                            item.MaxDura = ir.MaxDura;
                            item.CurrentDura = ir.CurrentDura;
                            eqChanged = true;
                        }
                    }
                }
                if (eqChanged)
                    MarkStatsDirty();
                break;
            case S.NewMagic nm:
                _magics.Add(nm.Magic);
                break;
            case S.RemoveMagic rm:
                if (rm.PlaceId >= 0 && rm.PlaceId < _magics.Count)
                    _magics.RemoveAt(rm.PlaceId);
                break;
            case S.MagicLeveled ml:
                var mag = _magics.FirstOrDefault(m => m.Spell == ml.Spell);
                if (mag != null)
                {
                    mag.Level = ml.Level;
                    mag.Experience = ml.Experience;
                }
                break;
            case S.Magic m:
                if (m.Cast)
                {
                    var magic = _magics.FirstOrDefault(x => x.Spell == m.Spell);
                    if (magic != null)
                    {
                        magic.CastTime = Environment.TickCount64;
                        _spellTime = magic.CastTime + GetSpellTimeDelay(m.Spell);
                    }
                }
                break;
            case S.MagicDelay md:
                if (md.ObjectID == _objectId)
                {
                    var magic = _magics.FirstOrDefault(m => m.Spell == md.Spell);
                    if (magic != null)
                        magic.Delay = md.Delay;
                }
                break;
            case S.MagicCast mc:
                {
                    var magic = _magics.FirstOrDefault(x => x.Spell == mc.Spell);
                    if (magic != null)
                    {
                        magic.CastTime = Environment.TickCount64;
                        _spellTime = magic.CastTime + GetSpellTimeDelay(mc.Spell);
                    }
                }
                break;
            case S.ObjectMagic om:
                if (om.ObjectID == _objectId && om.Cast)
                {
                    var magic = _magics.FirstOrDefault(x => x.Spell == om.Spell);
                    if (magic != null)
                    {
                        magic.CastTime = Environment.TickCount64;
                        _spellTime = magic.CastTime + GetSpellTimeDelay(om.Spell);
                    }
                }
                break;
            case S.SpellToggle st:
                if (st.ObjectID == _objectId)
                {
                    if (st.Spell == Spell.Slaying)
                        _slaying = st.CanUse;
                    else if (st.Spell == Spell.DoubleSlash)
                        _doubleSlash = st.CanUse;
                    else if (st.Spell == Spell.Thrusting)
                        _thrusting = st.CanUse;
                }
                break;
            case S.MountUpdate mu:
                if (mu.ObjectID == _objectId)
                    _ridingMount = mu.RidingMount;
                break;
            case S.AddBuff ab:
                if (ab.Buff.ObjectID == _objectId)
                    _buffs[ab.Buff.Type] = ab.Buff.Stats;
                break;
            case S.RemoveBuff rb:
                if (rb.ObjectID == _objectId)
                    _buffs.Remove(rb.Type);
                break;
            case S.KeepAlive keep:
                _pingTime = Environment.TickCount64 - keep.Time;
                break;
            default:
                break;
        }
    }

    private void HandleWhisper(string text)
    {
        HandleDebugCommand(text);
        var parts = text.Split(new[] {"=>"}, 2, StringSplitOptions.None);
        if (parts.Length != 2) return;
        WhisperReceived?.Invoke(parts[0], parts[1].Trim());
    }

    private async Task LoadMapAsync()
    {
        if (string.IsNullOrEmpty(_currentMapFile)) return;
        _mapData = await MapManager.GetMapAsync(_currentMapFile);
        if (_mapData != null)
        {
            _navData = _navDataManager.GetNavData(_currentMapFile, _mapData.WalkableCells);
        }
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
                Log($"KeepAlive error: {ex.Message}");
            }
        }
    }

    public void BeginIsolationKeepAlive()
    {
        _isolationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _isolationCts = cts;
        FireAndForget(Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && _stream != null)
            {
                try
                {
                    await SendAsync(new C.KeepAlive { Time = Environment.TickCount64 });
                }
                catch (Exception ex)
                {
                    Log($"Isolation keepalive error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(1000, cts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }));
    }

    public void EndIsolationKeepAlive()
    {
        _isolationCts?.Cancel();
        _isolationCts = null;
    }

    private async Task MaintenanceLoop()
    {
        while (_stream != null)
        {
            await Task.Delay(5000);
            try
            {
                _npcMemory.CheckForUpdates();
                _movementMemory.CheckForUpdates();
                _expRateMemory.CheckForUpdates();
                _navData?.SaveIfNeeded(TimeSpan.FromSeconds(60));
                CheckNpcInteractionTimeout();
            }
            catch (Exception ex)
            {
                Log($"Maintenance error: {ex.Message}");
            }
        }
    }


    private void HandleTradeFailChat(string text)
    {
        if (_pendingSellChecks.Count > 0 && text.Contains("cannot sell", StringComparison.OrdinalIgnoreCase))
        {
            var kv = _pendingSellChecks.First();
            var entry = kv.Value.entry;
            var type = kv.Value.type;
            entry.CannotSellItemTypes ??= new List<ItemType>();
            if (!entry.CannotSellItemTypes.Contains(type))
            {
                entry.CannotSellItemTypes.Add(type);
                _npcMemory.SaveChanges();
            }
            _pendingSellChecks.Remove(kv.Key);
        }

        if (_pendingRepairChecks.Count > 0 && text.Contains("cannot repair", StringComparison.OrdinalIgnoreCase))
        {
            var kv = _pendingRepairChecks.First();
            if (_repairItemTcs.TryGetValue(kv.Key, out var repTcs))
            {
                repTcs.TrySetResult(false);
                _repairItemTcs.Remove(kv.Key);
            }
            _pendingRepairChecks.Remove(kv.Key);
        }
    }

    internal static bool IsTamedName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        int open = name.LastIndexOf('(');
        return open >= 0 && name.EndsWith(")") && open < name.Length - 1;
    }
}
