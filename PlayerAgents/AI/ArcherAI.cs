using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Linq;
using PlayerAgents.Map;

public sealed class ArcherAI : BaseAI
{
    private DateTime _nextRangeAttackTime = DateTime.MinValue;
    private Point? _cachedShootSpot;
    private DateTime _nextShootSpotCheck = DateTime.MinValue;

    public bool HasElements => Client.HasElements;
    public int ElementCount => Client.ElementCount;

    public ArcherAI(GameClient client) : base(client) { }


    protected override double HpPotionWeightFraction => 0.10;
    protected override double MpPotionWeightFraction => 0.50;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinMC, Stat.MaxMC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };

    protected override IEnumerable<Spell> GetAttackSpells()
    {
        yield return Spell.ElementalShot;
        yield return Spell.StraightShot;
        yield return Spell.DoubleShot;
        yield return Spell.ExplosiveTrap;
    }

    private bool HasBowEquipped()
    {
        var eq = Client.Equipment;
        if (eq == null || eq.Count <= (int)EquipmentSlot.Weapon) return false;
        var weapon = eq[(int)EquipmentSlot.Weapon];
        return weapon != null && weapon.Info != null &&
               weapon.Info.Type == ItemType.Weapon &&
               weapon.Info.RequiredClass.HasFlag(RequiredClass.Archer);
    }



    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (monster.Dead)
            return;

        if (!HasBowEquipped())
        {
            await base.AttackMonsterAsync(monster, current);
            return;
        }
        var map = Client.CurrentMap;

        var trapMagic = GetMagic(Spell.ExplosiveTrap);
        if (trapMagic != null && map != null && DateTime.UtcNow >= _nextRangeAttackTime)
        {
            int maxTraps = trapMagic.Level + 1;
            int trapCount = Client.TrackedObjects.Values.Count(o => o.Type == ObjectType.Spell && o.Spell == Spell.ExplosiveTrap);
            if (trapCount < maxTraps)
            {
                int trapDist = Functions.MaxDistance(current, monster.Location);
                if (trapDist <= 4)
                {
                    var dir = Functions.DirectionFromPoint(current, monster.Location);
                    var front = Functions.PointMove(current, dir, 1);
                    if (map.IsWalkable(front.X, front.Y))
                    {
                        await Client.CastMagicAsync(Spell.ExplosiveTrap, dir, front, monster.Id);
                        RecordAttackTime();
                        _nextRangeAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
                        return;
                    }
                }
            }
        }

        var magic = ElementCount > 0
            ? GetBestMagic(Spell.ElementalShot, Spell.StraightShot, Spell.DoubleShot)
            : GetBestMagic(Spell.StraightShot, Spell.DoubleShot);
        int attackRange = magic != null && magic.Range > 0 ? magic.Range : 7;
        if (map != null &&
            Functions.MaxDistance(current, monster.Location) <= attackRange &&
            CanShoot(map, current, monster.Location) &&
            DateTime.UtcNow >= _nextRangeAttackTime)
        {
            var dir = Functions.DirectionFromPoint(current, monster.Location);
            if (magic != null)
                await Client.CastMagicAsync(magic.Spell, dir, monster.Location, monster.Id);
            else
                await Client.RangeAttackAsync(dir, monster.Location, monster.Id);
            RecordAttackTime();
            _nextRangeAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
        }
        else if (map == null)
        {
            await base.AttackMonsterAsync(monster, current);
        }
    }

    protected override async Task<bool> MoveToTargetAsync(MapData map, Point current, TrackedObject target, int radius = 1)
    {
        if (target.Type != ObjectType.Monster)
            return await base.MoveToTargetAsync(map, current, target, radius);
        if (target.Dead)
            return true;
        if (!HasBowEquipped())
            return await base.MoveToTargetAsync(map, current, target, radius);

        var magic = ElementCount > 0
            ? GetBestMagic(Spell.ElementalShot, Spell.StraightShot, Spell.DoubleShot)
            : GetBestMagic(Spell.StraightShot, Spell.DoubleShot);
        int attackRange = magic != null && magic.Range > 0 ? magic.Range : 7;
        const int retreatRange = 3;

        int dist = Functions.MaxDistance(current, target.Location);
        bool retreatFromTarget = dist <= retreatRange;
        bool canShoot = dist <= attackRange && CanShoot(map, current, target.Location);
        bool nearOtherMonsters = !IsSafeFromOtherMonsters(current, target, retreatRange);

        if (!canShoot)
        {
            var spot = GetNearestShootSpot(map, target, current, attackRange, retreatRange);
            if (spot == current)
            {
                if (dist > attackRange)
                {
                    var path = await FindBufferedPathAsync(map, current, target.Location, 3);
                    if (path.Count > 0)
                    {
                        bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, path[^1]);
                        if (!moved)
                        {
                            _cachedShootSpot = null;
                            _nextShootSpotCheck = DateTime.MinValue;
                        }
                        return moved;
                    }
                }
                else
                {
                    var safe = GetRetreatPoint(map, current, target, attackRange, retreatRange);
                    if (safe != current)
                    {
                        var path = await FindBufferedPathAsync(map, current, safe, 0);
                        if (path.Count > 0)
                        {
                            bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, safe);
                            if (!moved)
                            {
                                _cachedShootSpot = null;
                                _nextShootSpotCheck = DateTime.MinValue;
                            }
                            return moved;
                        }
                    }
                }
            }
            else
            {
                var path = await FindBufferedPathAsync(map, current, spot, 0);
                if (path.Count > 0)
                {
                    bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, spot);
                    if (!moved)
                    {
                        _cachedShootSpot = null;
                        _nextShootSpotCheck = DateTime.MinValue;
                    }
                    return moved;
                }
            }

            return true;
        }

        if (retreatFromTarget || nearOtherMonsters)
        {
            var safe = GetRetreatPoint(map, current, target, attackRange, retreatRange);
            if (safe != current)
            {
                var path = await FindBufferedPathAsync(map, current, safe, 0);
                if (path.Count > 0)
                {
                    bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, safe);
                    if (!moved)
                    {
                        _cachedShootSpot = null;
                        _nextShootSpotCheck = DateTime.MinValue;
                    }
                }
            }
            return false;
        }

        return false;
    }

    protected override async Task AttackTargetAsync(TrackedObject target, Point current)
    {
        await base.AttackTargetAsync(target, Client.CurrentLocation);
    }

    private bool IsSafeFromOtherMonsters(Point location, TrackedObject target, int minDistance)
    {
        if (minDistance <= 0)
            return true;

        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type != ObjectType.Monster || obj.Dead || obj.Hidden) continue;
            if (obj.Id == target.Id || obj.Tamed) continue;

            int distance = Functions.MaxDistance(location, obj.Location);
            if (distance <= minDistance)
                return false;
        }

        return true;
    }

    private Point GetSafestPoint(MapData map, Point origin, TrackedObject target, int range, int retreatRange)
    {
        var obstacles = BuildObstacles(map);
        Point best = origin;
        int bestScore = int.MinValue;

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                var p = new Point(origin.X + dx, origin.Y + dy);
                int dist = Functions.MaxDistance(p, origin);
                if (dist < range - 1 || dist > range) continue;
                if (!map.IsWalkable(p.X, p.Y)) continue;
                if (obstacles.Contains(p)) continue;
                int distToTarget = Functions.MaxDistance(p, target.Location);
                if (distToTarget < retreatRange) continue;
                if (distToTarget > range) continue;
                if (!IsSafeFromOtherMonsters(p, target, retreatRange)) continue;
                if (!CanShoot(map, p, target.Location)) continue;

                int min = 6;
                foreach (var obj in Client.TrackedObjects.Values)
                {
                    if (obj.Type != ObjectType.Monster || obj.Dead || obj.Hidden || obj.Tamed || obj.Id == target.Id) continue;
                    int d = Functions.MaxDistance(p, obj.Location);
                    if (d <= 6 && d < min) min = d;
                }
                int score = min - dist; // prefer farther from others, near range boundary
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
        }

        return best;
    }

    private Point GetRetreatPoint(MapData map, Point origin, TrackedObject target, int attackRange, int retreatRange)
    {
        var obstacles = BuildObstacles(map);

        var dir = Functions.DirectionFromPoint(target.Location, origin);
        var p = origin;
        for (int i = 1; i <= attackRange; i++)
        {
            p = Functions.PointMove(origin, dir, i);
            if (!map.IsWalkable(p.X, p.Y)) break;
            if (obstacles.Contains(p)) break;
            int distanceToTarget = Functions.MaxDistance(p, target.Location);
            if (distanceToTarget > attackRange) break;
            if (distanceToTarget < retreatRange) continue;
            if (!IsSafeFromOtherMonsters(p, target, retreatRange)) continue;
            if (CanShoot(map, p, target.Location))
                return p;
        }

        return GetSafestPoint(map, origin, target, attackRange, retreatRange);
    }

    private Point GetNearestShootSpot(MapData map, TrackedObject target, Point current, int attackRange, int safeDist)
    {
        if (DateTime.UtcNow < _nextShootSpotCheck && _cachedShootSpot.HasValue)
        {
            var spot = _cachedShootSpot.Value;
            int d = Functions.MaxDistance(spot, target.Location);
            var obs = BuildObstacles(map);
            if (d >= safeDist && d <= attackRange &&
                map.IsWalkable(spot.X, spot.Y) &&
                !obs.Contains(spot) &&
                CanShoot(map, spot, target.Location) &&
                IsSafeFromOtherMonsters(spot, target, safeDist))
                return spot;
        }

        var obstacles = BuildObstacles(map);
        var dirs = new[] { new Point(0,-1), new Point(1,0), new Point(0,1), new Point(-1,0), new Point(1,-1), new Point(1,1), new Point(-1,1), new Point(-1,-1) };
        var queue = new Queue<Point>();
        var visited = new HashSet<Point> { current };
        queue.Enqueue(current);

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            int dist = Functions.MaxDistance(p, target.Location);
            if (p != current && dist >= safeDist && dist <= attackRange &&
                CanShoot(map, p, target.Location) &&
                IsSafeFromOtherMonsters(p, target, safeDist))
            {
                _cachedShootSpot = p;
                _nextShootSpotCheck = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                return p;
            }

            foreach (var d in dirs)
            {
                var n = new Point(p.X + d.X, p.Y + d.Y);
                if (!map.IsWalkable(n.X, n.Y)) continue;
                if (obstacles.Contains(n)) continue;
                // allow exploring a wider area so we can path around walls
                if (Functions.MaxDistance(n, current) > attackRange * 3) continue;
                if (visited.Add(n))
                    queue.Enqueue(n);
            }
        }

        _cachedShootSpot = current;
        _nextShootSpotCheck = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        return current;
    }

    private static bool CanShoot(MapData map, Point from, Point to)
    {
        Point location = from;

        while (location != to)
        {
            MirDirection dir = Functions.DirectionFromPoint(location, to);
            location = Functions.PointMove(location, dir, 1);

            if (location.X < 0 || location.Y < 0 ||
                location.X >= map.Width || location.Y >= map.Height)
                return false;

            if (!map.IsWalkable(location.X, location.Y))
                return false;
        }

        return true;
    }

}
