namespace OverlayDisk.Cloud.Contracts;

/// <summary>Immutable per-volume transport object geometry. New-volume UI defaults to 4 MiB; persisted geometry is always explicit.</summary>
public static class CloudObjectGeometry
{
    public const int DefaultSize = 4 * 1024 * 1024;
    public const int MaximumSize = 16 * 1024 * 1024;
    public static bool IsSupported(long bytes) => bytes == DefaultSize || bytes == 8 * 1024 * 1024 || bytes == MaximumSize;
    public static int Validate(int bytes) => IsSupported(bytes) ? bytes : throw new ArgumentOutOfRangeException(nameof(bytes), "Object size must be 4, 8, or 16 MiB.");
}
