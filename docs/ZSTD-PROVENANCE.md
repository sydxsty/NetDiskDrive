# zstd transport dependency

OverlayDisk 0.7 uses `ZstdSharp.Port` **0.8.8**, pinned in
`cloud/OverlayDisk.Cloud.Contracts/OverlayDisk.Cloud.Contracts.csproj`.
This managed .NET implementation is distributed under the MIT license; the
upstream license is reproduced verbatim in `ZSTD-LICENSE.txt`.

Primary source evidence, checked for this implementation:

- [NuGet package](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8)
- [NuGet version index](https://api.nuget.org/v3-flatcontainer/zstdsharp.port/index.json)
- [Package archive / nuspec](https://api.nuget.org/v3-flatcontainer/zstdsharp.port/0.8.8/zstdsharp.port.0.8.8.nupkg)
- [Exact repository revision from the package nuspec](https://github.com/oleg-st/ZstdSharp/tree/2cd0c019693bc786a5fe5c3be94e107b24e7267e)
- [MIT license at that revision](https://github.com/oleg-st/ZstdSharp/blob/2cd0c019693bc786a5fe5c3be94e107b24e7267e/LICENSE)
- [Frame-header decoder API](https://github.com/oleg-st/ZstdSharp/blob/2cd0c019693bc786a5fe5c3be94e107b24e7267e/src/ZstdSharp/Unsafe/ZstdDecompress.cs)

The package nuspec identifies version 0.8.8, MIT, and repository commit
`2cd0c019693bc786a5fe5c3be94e107b24e7267e`; upstream describes this release
as based on zstd 1.5.7. No upstream application code or account credentials
are included.

## Immutable transport profile

`zstd-v1` means this pinned encoder implementation at compression level 3,
content-size enabled, frame checksum disabled, and zero internal zstd workers.
A 128-byte OverlayDisk envelope binds version, canonical length/SHA-256,
compressed length/SHA-256, reserved zero bytes, and the header checksum.
There is exactly one standard zstd frame, no dictionary, and a maximum 16 MiB
window. The decoder accepts only the expected 4/8/16 MiB canonical size and
checks its caller-supplied authenticated SHA-256 after bounded decompression.

Changing the encoder implementation or settings is a wire-format decision:
if bytes could change, introduce a new codec identifier rather than silently
producing different bytes at an existing immutable object path. The current
release rejects missing/unknown codecs and raw legacy objects; it performs no
migration and does not remove old remote files. JSON control records remain
uncompressed. Encrypted native content stays encrypted before compression.
