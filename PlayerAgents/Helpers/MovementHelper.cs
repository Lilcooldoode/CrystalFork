using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PlayerAgents.Map;

public static class MovementHelper
{
    public static HashSet<Point> BuildObstacles(GameClient client, uint ignoreId = 0, int radius = 1)
    {
        var obstacles = new HashSet<Point>(client.BlockingCells);
        if (ignoreId != 0 && client.TrackedObjects.TryGetValue(ignoreId, out var obj))
            obstacles.Remove(obj.Location);

        if (radius > 0 && !string.IsNullOrEmpty(client.CurrentMapFile))
        {
            var current = Path.GetFileNameWithoutExtension(client.CurrentMapFile);
            foreach (var entry in client.MovementMemory.GetAll())
            {
                if (entry.SourceMap == current)
                    obstacles.Add(new Point(entry.SourceX, entry.SourceY));
            }
        }
        return obstacles;
    }

    public static Point GetRandomPoint(GameClient client, MapData map, Random random, Point origin, int radius)
    {
        var obstacles = BuildObstacles(client);
        var nav = client.NavData;
        const int attempts = 20;
        if (nav != null)
        {
            int r = radius;
            if (radius > 0 && random.Next(5) == 0)
                r = 0; // occasionally roam anywhere using full nav data

            for (int i = 0; i < attempts; i++)
            {
                if (!nav.TryGetRandomCell(random, origin, r, out var navPoint))
                    break;
                if (!obstacles.Contains(navPoint))
                    return navPoint;
            }
        }

        var cells = map.WalkableCells;
        if (cells.Count > 0)
        {
            if (radius > 0)
            {
                var subset = cells.Where(c => Functions.MaxDistance(c, origin) <= radius && !obstacles.Contains(c)).ToList();
                if (subset.Count > 0)
                    return subset[random.Next(subset.Count)];
            }
            var free = cells.Where(c => !obstacles.Contains(c)).ToList();
            if (free.Count > 0)
                return free[random.Next(free.Count)];
        }

        int width = Math.Max(map.Width, 1);
        int height = Math.Max(map.Height, 1);
        for (int i = 0; i < attempts; i++)
        {
            int x = Math.Clamp(origin.X + random.Next(-10, 11), 0, width - 1);
            int y = Math.Clamp(origin.Y + random.Next(-10, 11), 0, height - 1);
            if (map.IsWalkable(x, y))
            {
                var p = new Point(x, y);
                if (!obstacles.Contains(p))
                    return p;
            }
        }
        int fx = Math.Clamp(origin.X + random.Next(-10, 11), 0, width - 1);
        int fy = Math.Clamp(origin.Y + random.Next(-10, 11), 0, height - 1);
        return new Point(fx, fy);
    }

    public static async Task<List<Point>> FindPathAsync(GameClient client, MapData map, Point start, Point dest, uint ignoreId = 0, int radius = 1)
    {
        try
        {
            var obstacles = BuildObstacles(client, ignoreId, radius);
            return await PathFinder.FindPathAsync(map, start, dest, obstacles, radius);
        }
        catch
        {
            return new List<Point>();
        }
    }

    public static async Task<bool> MoveAlongPathAsync(GameClient client, List<Point> path, Point destination)
    {
        if (path.Count <= 1) return true;
        if (client.MovementSavePending) return false;

        var current = client.CurrentLocation;

        if (path.Count > 2)
        {
            var next = path[1];
            var dir = Functions.DirectionFromPoint(current, next);
            if (Functions.PointMove(current, dir, 2) == path[2] && client.CanRun(dir))
            {
                await client.RunAsync(dir);
                path.RemoveRange(0, 2);
                return true;
            }
        }

        if (path.Count > 1)
        {
            var dir = Functions.DirectionFromPoint(current, path[1]);
            if (client.CanWalk(dir))
            {
                await client.WalkAsync(dir);
                path.RemoveAt(0);
                return true;
            }
        }
        else
        {
            var dir = Functions.DirectionFromPoint(current, destination);
            if (client.CanWalk(dir))
            {
                await client.WalkAsync(dir);
                return true;
            }
        }

        return false;
    }

    public static List<MapMovementEntry>? FindTravelPath(GameClient client, string destMapFile)
    {
        var startMap = Path.GetFileNameWithoutExtension(client.CurrentMapFile);
        var destMap = Path.GetFileNameWithoutExtension(destMapFile);
        if (startMap == destMap)
            return new List<MapMovementEntry>();

        var entries = client.MovementMemory.GetAll()
            .Where(e => e.SourceMap != e.DestinationMap)
            .ToList();
        var queue = new Queue<(string Map, List<MapMovementEntry> Path)>();
        var visited = new HashSet<string> { startMap };
        queue.Enqueue((startMap, new List<MapMovementEntry>()));

        while (queue.Count > 0)
        {
            var (map, soFar) = queue.Dequeue();
            if (map == destMap)
                return soFar;

            foreach (var e in entries)
            {
                if (e.SourceMap != map) continue;
                if (visited.Contains(e.DestinationMap)) continue;
                visited.Add(e.DestinationMap);
                var newList = new List<MapMovementEntry>(soFar) { e };
                queue.Enqueue((e.DestinationMap, newList));
            }
        }
        return null;
    }
}
