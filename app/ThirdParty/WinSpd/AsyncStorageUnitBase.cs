// OverlayDisk extension to the pinned WinSpd bindings; distributed under the same GPLv3 terms.
using System;
using Spd.Interop;

namespace Spd;

/// <summary>Return false only after accepting a request for later native completion.</summary>
public abstract class AsyncStorageUnitBase : StorageUnitBase
{
    public abstract bool ReadDeferred(DeferredStorageOperation operation, IntPtr data,
        ulong blockAddress, uint blockCount, bool forceUnitAccess, ref StorageUnitStatus status);
    public abstract bool WriteDeferred(DeferredStorageOperation operation, IntPtr data,
        ulong blockAddress, uint blockCount, bool forceUnitAccess, ref StorageUnitStatus status);
    public abstract bool FlushDeferred(DeferredStorageOperation operation,
        ulong blockAddress, uint blockCount, ref StorageUnitStatus status);
}
