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

    public async Task<S.NPCResponse> WaitForNpcResponseAsync(CancellationToken cancellationToken = default)
    {
        var response = await WaitForNextNpcResponseAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var nextTask = WaitForNextNpcResponseAsync(cancellationToken);
            var delayTask = Task.Delay(NpcResponseDebounceMs, cancellationToken);
            var finished = await Task.WhenAny(nextTask, delayTask).ConfigureAwait(false);
            if (finished == nextTask)
            {
                response = await nextTask.ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }

    private Task<S.NPCResponse> WaitForNextNpcResponseAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<S.NPCResponse>();
        _npcResponseTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    private void DeliverNpcResponse(S.NPCResponse response)
    {
        if (response.Page.Count == 0)
            return;

        _npcResponseTcs?.TrySetResult(response);
        _npcResponseTcs = null;
    }

    public Task WaitForNpcGoodsAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        _npcGoodsTcs = tcs;
        _pendingGoodsNpcId = _dialogNpcId;
        if (cancellationToken != default)
            cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled();
                if (ReferenceEquals(_npcGoodsTcs, tcs))
                    _pendingGoodsNpcId = null;
            });
        return tcs.Task;
    }

    public Task WaitForNpcSellAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        _npcSellTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public Task WaitForNpcRepairAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        _npcRepairTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public Task<S.SellItem> WaitForSellItemAsync(ulong uniqueId, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<S.SellItem>();
        _sellItemTcs[uniqueId] = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled();
                _sellItemTcs.Remove(uniqueId);
            });
        return tcs.Task;
    }

    public Task<bool> WaitForRepairItemAsync(ulong uniqueId, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        _repairItemTcs[uniqueId] = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled();
                _repairItemTcs.Remove(uniqueId);
            });
        return tcs.Task;
    }

    public Task WaitForUserStorageAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        _userStorageTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public Task WaitForStorageLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_storage != null)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource<bool>();
        _storageLoadedTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public Task<bool> WaitForStoreItemAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        _storeItemTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public Task<bool> WaitForTakeBackItemAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        _takeBackItemTcs = tcs;
        if (cancellationToken != default)
            cancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }
}
