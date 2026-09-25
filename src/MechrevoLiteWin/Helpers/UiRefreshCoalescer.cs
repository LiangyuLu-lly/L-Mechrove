namespace MechrevoLite.Helpers;

/// <summary>
/// 把爆发式 UI 刷新收成一次已投递的回调。
/// CapabilitiesChanged 会在十二个状态主题上连发：投递尚未执行前的多次 Request 只跑一次；
/// 清掉 queued 标志之后（refresh 进行中）再来的 Request 可以再投递一次——永远不会是十二次。
/// queued 必须在调用 refresh 之前清掉，否则 refresh 期间的新事件会被吞掉。
/// </summary>
internal sealed class UiRefreshCoalescer
{
    int _queued;

    internal void Request(Action<Action> post, Action refresh)
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        try
        {
            post(() =>
            {
                Interlocked.Exchange(ref _queued, 0);
                refresh();
            });
        }
        catch
        {
            Interlocked.Exchange(ref _queued, 0);
            throw;
        }
    }
}
