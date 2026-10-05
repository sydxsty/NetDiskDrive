namespace OverlayDisk;

public sealed record PrefetchSettings(string Strategy = "sequential", int ObjectCount = 2)
{
    public const int MaximumObjectCount = 16;
    public void Validate()
    {
        if (Strategy is not ("disabled" or "sequential" or "adaptive") || ObjectCount is < 1 or > MaximumObjectCount)
            throw new IOException("请选择有效的预取策略，预取数量应为 1–16 个块。");
    }
}
