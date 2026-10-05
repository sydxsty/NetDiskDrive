#ifndef OVERLAYDISK_V4_H
#define OVERLAYDISK_V4_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* All entrypoints use the C calling convention. Never close concurrently with
 * any handle call. Stop admissions, drain and flush while replies are still
 * being consumed, release completions, then close. Close itself does NOT flush. */
int32_t od_v4_create(const char *path_utf8, uint64_t capacity, const char *password_or_null); /* Legacy default: 4 MiB. */
/* Creation only: one immutable object per 4, 8 or 16 MiB; internal pages remain 4 KiB. */
int32_t od_v4_create_sized(const char *path_utf8, uint64_t capacity, const char *password_or_null, uint32_t object_size_bytes);
void *od_v4_open(const char *path_utf8, const char *password_or_null);
/* Provider runs outside native store/view/provider locks, while the original
 * ordered I/O remains outstanding. Fill exactly length bytes (this volume's 4, 8 or 16 MiB) and return 0;
 * on failure write a bounded UTF-8 error and return -1. Buffers are borrowed only
 * during the callback. Do not call ordered I/O from inside it. NULL unregisters
 * and waits for in-flight callbacks; cancel/join downloads before unregistering. */
typedef int32_t (*ODV4ObjectProvider)(void *context, const char *object_id,
    const char *sha256, uint8_t *output, uint32_t length, char *error, uint32_t error_length);
int32_t od_v4_set_object_provider(void *, ODV4ObjectProvider, void *context);
/* options: {mode:"copy"|"original",lazy,source_volume_id?,backing?,publication?}.
 * Original mode requires a confirmed commit+binding; identity is checked before creating a file. */
void *od_v4_restore_begin_options(const char *path_utf8, const uint8_t *root_object,
    uint32_t length, const char *password_or_null, const char *options_json);
void *od_v4_lazy_begin(const char *path_utf8, const uint8_t *root_object,
    uint32_t length, const char *password_or_null, const char *backing_json);
/* Authenticated immutable object import, outside the ordered I/O queue.
 * length must match Info.object_size; restore roots carry and authenticate the same geometry. */
int32_t od_v4_lazy_import(void *, const char *object_id, const uint8_t *data, uint32_t length);
int32_t od_v4_read(void *, uint64_t offset, uint8_t *data, uint32_t length);
int32_t od_v4_read_persistent(void *, uint64_t offset, uint8_t *data, uint32_t length);
int32_t od_v4_write(void *, uint64_t offset, const uint8_t *data, uint32_t length);
int32_t od_v4_trim(void *, uint64_t offset, uint64_t length);
/* Ordered barrier; flush prior accepted writes, then set guest sector write protection. */
int32_t od_v4_set_read_only(void *, uint32_t enabled);
int32_t od_v4_flush(void *);
int32_t od_v4_compact(void *);
uint64_t od_v4_capacity(void *);
int32_t od_v4_info(void *, char *json, uint32_t length);
int32_t od_v4_inspect(const char *path_utf8, char *json, uint32_t length);
const char *od_v4_last_error(void); /* Calling-thread error, overwritten by its next ABI call. */

enum { OD_V4_READ = 1, OD_V4_WRITE = 2, OD_V4_FLUSH = 3, OD_V4_TRIM = 4, OD_V4_FUA = 1 };
typedef struct ODV4Completion {
    uint64_t token;
    int32_t status;
    uint32_t length;
    const uint8_t *data;
    const char *error;
} ODV4Completion; /* 32 bytes on x64. status 0=success, -1=failed. */
/* At most 64 outstanding I/O requests and 64 MiB owned transfer buffers, plus
 * ONE reserved zero-buffer synchronous Flush control slot. All share the same
 * admission sequence; the reserved Flush is still an ordered global barrier.
 * Admissions block under backpressure; keep the completion consumer running.
 * READ/WRITE max 1 MiB. I/O is 512-byte aligned. Write is copied before returning.
 * Return 0 means accepted, NOT completed. Duplicate outstanding tokens fail.
 * Tokens may be reused only AFTER their completion is released. */
int32_t od_v4_submit(void *, uint32_t op, uint64_t offset, uint32_t length,
    const uint8_t *data, uint32_t flags, uint64_t token);
/* 0=one completion, 1=timeout, -1=stopped/error. Delivered exactly once.
 * READ data has >=1 MiB allocated capacity, including for short reads/errors;
 * length is actual successful bytes. Other operations have data=NULL.
 * data/error pointers remain valid until release(token) or close(handle). */
int32_t od_v4_next_completion(void *, ODV4Completion *out, uint32_t timeout_ms);
int32_t od_v4_release_completion(void *, uint64_t token);
int32_t od_v4_drain(void *); /* Prior execution only; does not await release or flush. */
/* Pump-failure escape hatch: wake/reject blocked and future ordinary sync and
 * async I/O admissions (including sync Trim). Accepted work/completions remain
 * intact. Sync Flush can use its reserved slot after cancellation even with all
 * 64 completions unreleased. Drain/next/release/close remain usable. No implicit
 * flush, dropped data, discarded completion or handle destruction occurs. */
int32_t od_v4_cancel_submissions(void *);
void od_v4_close(void *);

int32_t od_v4_snapshot_create(void *, char *id, uint32_t length);
/* Positive required UTF-8 byte count INCLUDING NUL; -1=error. Query with NULL,0.
 * An undersized nonempty buffer is set empty and the required count returned. */
int32_t od_v4_snapshot_manifest(void *, const char *id, char *json, uint32_t length);
int32_t od_v4_object_read(void *, const char *snapshot, const char *object,
    uint64_t offset, uint8_t *data, uint32_t length);
int32_t od_v4_snapshot_release(void *, const char *id);
/* Inspections and pinned export run outside the ordered disk I/O queue.
 * Snapshot creation/new cloud.prepare capture a short ordered cut; continuation
 * preparation, receipts and all inspection calls do not create disk barriers. */
int32_t od_v4_read_fua(void *, uint64_t offset, uint8_t *data, uint32_t length);
int32_t od_v4_write_fua(void *, uint64_t offset, const uint8_t *data, uint32_t length);
int32_t od_v4_control(void *, const char *request_json, char *response_json, uint32_t length);
int32_t od_v4_read_export(void *, const char *job_id, const char *object_id, uint64_t offset, uint8_t *data, uint32_t length);
void *od_v4_restore_begin(const char *path_utf8, const uint8_t *root_object, uint32_t length, const char *password_or_null);
int32_t od_v4_restore_accept(void *, const char *object_id, const uint8_t *data, uint32_t length);
void *od_v4_restore_snapshot_begin(void *source, const char *snapshot_id, const char *path_utf8, const char *password_or_null);
int32_t od_v4_restore_snapshot_resume(void *target, void *source);
#ifdef __cplusplus
}
#endif
#endif
