using System.Text.Json.Serialization;

namespace OverlayDisk;

public sealed class DiskEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ContainerPath { get; set; } = "";
    public ulong CapacityBytes { get; set; }
    [JsonRequired] public uint ObjectSizeBytes { get; set; }
    public char DriveLetter { get; set; } = 'O';
    public bool Encrypted { get; set; }
    public bool ReadOnly { get; set; }
    public bool Initialized { get; set; }
    public int FormatVersion { get; set; } = 4;
    [JsonIgnore] public bool Unlocked { get; set; }
    [JsonIgnore] public bool Mounted { get; set; }
    [JsonIgnore] public string Status { get; set; } = "未挂载";
}

public sealed record CreateDiskRequest(string Name, string ContainerPath, ulong CapacityBytes, char DriveLetter, bool Encrypted, bool ReadOnly = false, uint ObjectSizeBytes = 4 * 1024 * 1024);
