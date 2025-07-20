using ClientPackets;
using Shared;

public class BaseAI
{
    protected readonly GameClient Client;
    protected readonly Random Random = new();

    public BaseAI(GameClient client)
    {
        Client = client;
    }

    protected virtual int WalkDelay => 1000;

    public virtual async Task RunAsync()
    {
        while (true)
        {
            var dir = (MirDirection)Random.Next(0, 8);
            await Client.WalkAsync(dir);
            await Task.Delay(WalkDelay);
        }
    }
}
