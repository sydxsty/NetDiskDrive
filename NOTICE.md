# Copyright and licensing

Copyright (c) 2026 sydxsty and contributors.

NetDiskDrive (current executable name: OverlayDisk) is distributed under
**GPL-3.0-only**, except where a component explicitly provides its own license.
The license is in `LICENSE`. This software is provided without warranty.

The original Rust core under `engine/` remains available under the MIT license
in `engine/LICENSE`. Combining it into the application does not remove that
standalone permission. Third-party copyrights and licenses remain unchanged.

## WinSpd

**WinSpd - Windows Storage Proxy Driver, Copyright (C) Bill Zissimopoulos**

Repository: https://github.com/winfsp/winspd

The modified C# binding source is included in `app/ThirdParty/WinSpd/` together
with the complete upstream GPLv3 license and its stated exceptions, plus a
record of local modifications. The application does not claim ownership of
this source or extend the upstream exceptions to unrelated code.

The unmodified installer and native runtime are from `v1.0B1`:
https://github.com/winfsp/winspd/releases/tag/v1.0B1

Corresponding upstream source:
https://github.com/winfsp/winspd/tree/v1.0B1
https://github.com/winfsp/winspd/archive/refs/tags/v1.0B1.zip

Pinned binding source revision:
https://github.com/winfsp/winspd/tree/55c53bc454afbbba38bd1692f52beb77ed59142f

## Other components and corresponding source

See `docs/THIRD-PARTY.md`, `docs/RUST-DEPENDENCY-LICENSES.txt`,
`docs/WEBVIEW2-LICENSE.txt` and `docs/ZSTD-LICENSE.txt`. WebView2 Runtime,
.NET Desktop Runtime and Visual C++ Runtime are installed separately.

Source matching the public release, including the modified bindings and build
scripts: https://github.com/sydxsty/NetDiskDrive/tree/v0.8.0
GitHub provides source ZIP/TAR archives alongside the binary release. Exact
Rust dependency versions and source checksums are in `engine/Cargo.lock`;
NuGet package versions are pinned in the project files. Upstream source links
and notices accompany the release; no cloud account, browser profile or test
disk is part of the distribution.
