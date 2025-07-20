using System.Drawing;
using Shared;

public class TrackedObject
{
    public uint Id { get; }
    public ObjectType Type { get; }
    public string Name { get; }
    public Point Location { get; set; }
    public MirDirection Direction { get; set; }

    public byte AI { get; }
    public bool Dead { get; set; }

    public TrackedObject(uint id, ObjectType type, string name, Point location, MirDirection direction, byte ai = 0, bool dead = false)
    {
        Id = id;
        Type = type;
        Name = name;
        Location = location;
        Direction = direction;
        AI = ai;
        Dead = dead;
    }
}
