#ifndef OVERLAYDISK_V2_H
#define OVERLAYDISK_V2_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* All entrypoints use the C calling convention. Never close concurrently with
 * any handle call. Stop admissions, drain and flush while replies are still
 * being consumed, release completions, then close. Close itself does NOT flush. */
int32_t od_v2_create(const char *path_utf8, uint64_t capacity, const char *password_or_null);
void *od_v2_open(const char *path_utf8, const char *password_or_null);
int32_t od_v2_read(void *, uint64_t offset, uint8_t *data, uint32_t length);
int32_t od_v2_read_persistent(void *, uint64_t offset, uint8_t *data, uint32_t length);
int32_t od_v2_write(void *, uint64_t offset, const uint8_t *data, uint32_t length);
int32_t od_v2_trim(void *, uint64_t offset, uint64_t length);
int32_t od_v2_flush(void *);
int32_t od_v2_compact(void *);
uint64_t od_v2_capacity(void *);
int32_t od_v2_info(void *, char *json, uint32_t length);
int32_t od_v2_inspect(const char *path_utf8, char *json, uint32_t length);
const char *od_v2_last_error(void); /* Calling-thread error, overwritten by its next ABI call. */

enum { OD_V2_READ = 1, OD_V2_WRITE = 2, OD_V2_FLUSH = 3, OD_V2_TRIM = 4, OD_V2_FUA = 1 };
typedef struct ODCompletion {
    uint64_t token;
    int32_t status;
    uint32_t length;
    const uint8_t *data;
    const char *error;
} ODCompletion; /* 32 bytes on x64. status 0=success, -1=failed. */
/* At most 64 outstanding I/O requests and 64 MiB owned transfer buffers, plus
 * ONE reserved zero-buffer synchronous Flush control slot. All share the same
 * admission sequence; the reserved Flush is still an ordered global barrier.
 * Admissions block under backpressure; keep the completion consumer running.
 * READ/WRITE max 1 MiB. I/O is 512-byte aligned. Write is copied before returning.
 * Return 0 means accepted, NOT completed. Duplicate outstanding tokens fail.
 * Tokens may be reused only AFTER their completion is released. */
int32_t od_v2_submit(void *, uint32_t op, uint64_t offset, uint32_t length,
    const uint8_t *data, uint32_t flags, uint64_t token);
/* 0=one completion, 1=timeout, -1=stopped/error. Delivered exactly once.
 * READ data has >=1 MiB allocated capacity, including for short reads/errors;
 * length is actual successful bytes. Other operations have data=NULL.
 * data/error pointers remain valid until release(token) or close(handle). */
int32_t od_v2_next_completion(void *, ODCompletion *out, uint32_t timeout_ms);
int32_t od_v2_release_completion(void *, uint64_t token);
int32_t od_v2_drain(void *); /* Prior execution only; does not await release or flush. */
/* Pump-failure escape hatch: wake/reject blocked and future ordinary sync and
 * async I/O admissions (including sync Trim). Accepted work/completions remain
 * intact. Sync Flush can use its reserved slot after cancellation even with all
 * 64 completions unreleased. Drain/next/release/close remain usable. No implicit
 * flush, dropped data, discarded completion or handle destruction occurs. */
int32_t od_v2_cancel_submissions(void *);
void od_v2_close(void *);

int32_t od_v2_snapshot_create(void *, char *id, uint32_t length);
/* Positive required UTF-8 byte count INCLUDING NUL; -1=error. Query with NULL,0.
 * An undersized nonempty buffer is set empty and the required count returned. */
int32_t od_v2_snapshot_manifest(void *, const char *id, char *json, uint32_t length);
int32_t od_v2_object_read(void *, const char *snapshot, const char *object,
    uint64_t offset, uint8_t *data, uint32_t length);
int32_t od_v2_snapshot_release(void *, const char *id);
#ifdef __cplusplus
}
#endif
#endif
