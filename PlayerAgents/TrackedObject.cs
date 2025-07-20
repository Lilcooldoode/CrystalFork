using System.Drawing;
using Shared;

public class TrackedObject
{
    public uint Id { get; }
    public ObjectType Type { get; }
    public string Name { get; }
    public Point Location { get; set; }
    public MirDirection Direction { get; set; }

    public TrackedObject(uint id, ObjectType type, string name, Point location, MirDirection direction)
    {
        Id = id;
        Type = type;
        Name = name;
        Location = location;
        Direction = direction;
    }
}
