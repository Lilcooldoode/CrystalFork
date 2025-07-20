using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace PlayerAgents.Map;

public static class PathFinder
{
    private struct Node
    {
        public Point Point;
        public int G;
        public int F;
    }

    public static async Task<List<Point>> FindPathAsync(MapData map, Point start, Point end, ISet<Point>? obstacles = null)
    {
        return await Task.Run(() => FindPath(map, start, end, obstacles));
    }

    private static List<Point> FindPath(MapData map, Point start, Point end, ISet<Point>? obstacles)
    {
        int width = map.Width;
        int height = map.Height;

        if (start == end)
            return new List<Point> { start };

        if (width == 0 || height == 0)
            return new List<Point>();
        var open = new PriorityQueue<Node, int>();
        var cameFrom = new Dictionary<Point, Point>();
        var gScore = new Dictionary<Point, int>();
        gScore[start] = 0;
        open.Enqueue(new Node { Point = start, G = 0, F = Heuristic(start, end) }, Heuristic(start, end));
        var directions = new[]
        {
            new Point(0,-1), new Point(1,0), new Point(0,1), new Point(-1,0),
            new Point(1,-1), new Point(1,1), new Point(-1,1), new Point(-1,-1)
        };

        int steps = 0;
        const int maxSteps = 20000;
        while (open.Count > 0)
        {
            var current = open.Dequeue().Point;
            if (current == end)
                return ReconstructPath(cameFrom, current);

            if (++steps > maxSteps)
                break;

            foreach (var dir in directions)
            {
                var neighbor = new Point(current.X + dir.X, current.Y + dir.Y);
                if (!map.IsWalkable(neighbor.X, neighbor.Y)) continue;
                if (obstacles != null && obstacles.Contains(neighbor) && neighbor != end && neighbor != start) continue;
                int tentative = gScore[current] + ((dir.X == 0 || dir.Y == 0) ? 10 : 14);
                if (gScore.TryGetValue(neighbor, out var g) && tentative >= g) continue;
                cameFrom[neighbor] = current;
                gScore[neighbor] = tentative;
                int f = tentative + Heuristic(neighbor, end);
                open.Enqueue(new Node { Point = neighbor, G = tentative, F = f }, f);
            }
        }
        return new List<Point>();
    }

    private static List<Point> ReconstructPath(Dictionary<Point, Point> cameFrom, Point current)
    {
        var totalPath = new List<Point> { current };
        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            totalPath.Add(current);
        }
        totalPath.Reverse();
        return totalPath;
    }

    private static int Heuristic(Point a, Point b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return 10 * (dx + dy);
    }
}
