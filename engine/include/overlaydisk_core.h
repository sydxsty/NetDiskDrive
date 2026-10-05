#ifndef OVERLAYDISK_CORE_H
#define OVERLAYDISK_CORE_H
#include <stdint.h>
#if defined(_WIN32)
#define OD_API __declspec(dllimport)
#else
#define OD_API
#endif
#ifdef __cplusplus
extern "C" {
#endif

/* All paths/passwords are NUL-terminated UTF-8. A NULL password creates/opens
 * an unencrypted volume; an empty password is invalid for creation.
 * Return codes: 0 success, -1 error (see od_last_error on the same thread).
 * Reads/writes/trim require 512-byte alignment and cannot exceed capacity.
 * Handles support concurrent calls. Never close concurrently with a call.
 * Every successful write/trim is durable locally; there is no cloud backend.
 * Catching Rust panics cannot protect against invalid C pointers/handles.
 */
OD_API int32_t od_create(const char *path_utf8, uint64_t capacity_bytes, const char *password_utf8_or_null);
OD_API void *od_open(const char *path_utf8, const char *password_utf8_or_null);
OD_API int32_t od_read(void *handle, uint64_t byte_offset, uint8_t *buffer, uint32_t length);
OD_API int32_t od_write(void *handle, uint64_t byte_offset, const uint8_t *buffer, uint32_t length);
OD_API int32_t od_flush(void *handle);
OD_API int32_t od_trim(void *handle, uint64_t byte_offset, uint64_t length);
OD_API int32_t od_compact(void *handle); /* device must be unmounted */
OD_API uint64_t od_capacity(void *handle); /* 0 on failure */
OD_API int32_t od_info(void *handle, char *utf8_json, uint32_t buffer_len); /* NUL-terminated JSON; 1024 bytes sufficient for v1 */
OD_API void od_close(void *handle);
OD_API const char *od_last_error(void); /* valid until next call on this thread; do not free */

#ifdef __cplusplus
}
#endif
#endif
