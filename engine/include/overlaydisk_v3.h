#ifndef OVERLAYDISK_V3_H
#define OVERLAYDISK_V3_H
#include "overlaydisk_v2.h"
#ifdef __cplusplus
extern "C" {
#endif
/* V3 handles use the same ownership, queue and basic od_v2_* I/O/close ABI.
 * V3 creation is always a new format-3 file; old writers reject that format.
 * Neither close entrypoint promises an implicit flush. */
int32_t od_v3_create(const char *path, uint64_t capacity, const char *password_or_null);
void *od_v3_open(const char *path, const char *password_or_null);
int32_t od_v3_inspect(const char *path, char *json, uint32_t capacity);
const char *od_v3_last_error(void);
enum { OD_V3_CONTROL_CAPACITY = 131072, OD_V3_OBJECT_SIZE = 4194304 };
/* JSON commands return 0/-1. Supply >=OD_V3_CONTROL_CAPACITY bytes; insufficient
 * space is rejected BEFORE executing any mutation. No mutating size query.
 * Lists are capped at 128 entries. Inputs/outputs contain no account cookies. */
int32_t od_v3_control(void *handle, const char *request_json, char *json, uint32_t capacity);
/* Read the immutable prepared object, independently of the disk request queue.
 * Network I/O belongs to the caller. job/object IDs are returned by cloud.list. */
int32_t od_v3_read_export(void *handle, const char *job_id, const char *object_id,
    uint64_t offset, uint8_t *data, uint32_t length);
/* root is the exact 4 MiB object with kind=commit, ID=Job.root_object_id.
 * New destination identity/key; same encryption mode/password as source.
 * Restoring files reject ordinary block I/O until restore.finish succeeds.
 * Resume by od_v3_open(target,password), then restore.status/accept/finish.
 * If status is kind=uninitialized/phase=initializing, close that handle and
 * call restore_begin again with the verified original commit object. Only an
 * authenticated empty restore bootstrap is accepted; its local ID is retained.
 * Normal disks and initialized restore progress are never overwritten. */
void *od_v3_restore_begin(const char *target, const uint8_t *root, uint32_t length,
    const char *source_password_or_null);
int32_t od_v3_restore_accept(void *handle, const char *object_id,
    const uint8_t *data, uint32_t length);
/* Incremental local snapshot restore. The resulting target uses restore.status
 * and snapshot.restore_step controls; it never inherits a cloud writer binding.
 * An authenticated empty bootstrap can retry begin with the original source
 * snapshot. An existing source pin for that target identity is reused. Resume
 * can also recover an empty bootstrap when its source pin is already durable. */
void *od_v3_restore_snapshot_begin(void *source, const char *snapshot_id,
    const char *target, const char *target_password_or_null);
int32_t od_v3_restore_snapshot_resume(void *target, void *unlocked_source);
#ifdef __cplusplus
}
#endif
#endif
