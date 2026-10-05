# WinSpd C# bindings

Source: https://github.com/winfsp/winspd
Pinned commit: `55c53bc454afbbba38bd1692f52beb77ed59142f` (not a claim of current Windows certification).
Vendored files: `src/dotnet/Interop.cs`, `StorageUnitBase.cs`, `StorageUnitBase+Const.cs`, `StorageUnitHost.cs`.
Copyright Bill Zissimopoulos. The complete upstream license is in `License.txt`.

Local changes: nullable context disabled for legacy binding source; load native DLL from an explicit app/install path; check native WinSpd major ABI rather than the application assembly version; use a strong GCHandle for callback lifetime; fix SuppressFinalize target; zero reserved callback slots; allow flags to be cleared; return a SCSI write error if UNMAP fails rather than silently acknowledging it. Unmap array construction is inside the exception boundary.

V2 asynchronous extension: `AsyncStorageUnitBase.cs` opts read/write/flush into native deferred completion. `StorageUnitHost` snapshots request and response values from `SpdStorageUnitGetOperationContext`, returns FALSE for accepted queued I/O, and replies through `SpdStorageUnitSendResponse` on a separate pump. The bound x64 request/response/context structures are 32/48/24 bytes, as defined in `inc/winspd/ioctl.h` and `inc/winspd/winspd.h`. Dispatcher thread count is configurable and OverlayDisk selects one ordered ingress thread. A native shutdown's synthetic zero-Hint flush remains synchronous. Native delivery errors are checked via `SpdStorageUnitGetDispatcherErrorF`. These three exports are present in upstream `src/dll/library.def`; deferred completion and buffer reuse are implemented in `src/shared/stgunit.c`. The product keeps every Rust read buffer alive until native send returns, then explicitly releases its completion.

Disk identity is the lowercase hyphenated GUID from `src/sys/stgunit.c` lines 185-192. WinSpd rejects a duplicate live GUID. The driver also exposes owner PID in VPD 0x83, but provisioning uses the process-local successful-host registration plus exact serial, product, bus, capacity, sector size and physical disk checks. All storage commands re-resolve the serial rather than trusting a user-supplied disk number.

Bus identity: `src/sys/driver.c` uses `VIRTUAL_HW_INITIALIZATION_DATA` with `AdapterInterfaceType = Internal`; `src/sys/adapter.c` sets `VirtualDevice = TRUE`. The official driver reports `Get-Disk.BusType` as the string `Virtual` on the verified Windows host. SCSI is the command protocol, not this bus classification. The guard requires `Virtual` together with all the other identity checks; it does not accept arbitrary bus types. `inc/winspd/ioctl.h` defines the exact Inquiry vendor string as `WinSpd  `.
