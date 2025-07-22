using System.Threading;
using System.Threading.Tasks;
using C = ClientPackets;
using S = ServerPackets;

public sealed partial class GameClient
{
    public async Task CallNPCAsync(uint objectId, string key)
    {
        if (_stream == null) return;
        await SendAsync(new C.CallNPC { ObjectID = objectId, Key = $"[{key}]" });
    }

    public Task<S.NPCResponse> WaitForNpcResponseAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<S.NPCResponse>();
        _npcResponseTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public async Task<S.NPCResponse> WaitForLatestNpcResponseAsync(CancellationToken cancellationToken = default)
    {
        var response = await WaitForNpcResponseAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var nextTask = WaitForNpcResponseAsync(cancellationToken);
            var delayTask = Task.Delay(NpcResponseDebounceMs, cancellationToken);
            var finished = await Task.WhenAny(nextTask, delayTask).ConfigureAwait(false);
            if (finished == nextTask)
            {
                response = await nextTask.ConfigureAwait(false);
                continue;
            }
            break;
        }
        return response;
    }

    private void DeliverNpcResponse(S.NPCResponse response)
    {
        _npcResponseTcs?.TrySetResult(response);
        _npcResponseTcs = null;
    }
}
