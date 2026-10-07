# OverlayDisk storage engine

The current engine uses one local container, authenticated copy-on-write storage,
and lazy immutable cloud objects. See [the current format and durability guide](README-v4.md)
for its geometry, APIs, persistence guarantees, and verification boundaries.

Only the current `v4::Volume` and `od_v4_*` interfaces are built. Obsolete storage
engines, old ABIs, and format migration paths are not included.

The standalone engine is distributed under the [MIT license](LICENSE).
