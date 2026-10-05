//! All handle operations use one scheduler; synchronous calls have private replies.
//! The caller must quiesce native callbacks before closing a handle.
use crate::{
    async_io::{Backend, IoResult, Operation, Output, Queue, READ_ALLOCATION},
    v2::Volume,
};
use std::{
    cell::RefCell,
    ffi::{c_char, c_void, CStr, CString},
    panic::{catch_unwind, AssertUnwindSafe},
    ptr, slice,
    sync::Arc,
    time::Duration,
};
use zeroize::Zeroizing;

thread_local! { static LAST_ERROR: RefCell<CString> = RefCell::new(CString::new("").unwrap()); }

pub(super) fn boundary<T>(fallback: T, action: impl FnOnce() -> IoResult<T>) -> T {
    LAST_ERROR.with(|e| *e.borrow_mut() = CString::new("").unwrap());
    let result = catch_unwind(AssertUnwindSafe(action))
        .unwrap_or_else(|_| Err("V2 FFI panic; operation failed".into()));
    match result {
        Ok(value) => value,
        Err(error) => {
            LAST_ERROR.with(|e| {
                *e.borrow_mut() = CString::new(error.replace('\0', " ")).expect("NUL removed")
            });
            fallback
        }
    }
}

pub(super) unsafe fn text(pointer: *const c_char) -> IoResult<String> {
    if pointer.is_null() {
        return Err("required UTF-8 string is null".into());
    }
    CStr::from_ptr(pointer)
        .to_str()
        .map(str::to_owned)
        .map_err(|_| "string is not valid UTF-8".into())
}
pub(super) unsafe fn password(pointer: *const c_char) -> IoResult<Option<Zeroizing<String>>> {
    if pointer.is_null() {
        Ok(None)
    } else {
        Ok(Some(Zeroizing::new(text(pointer)?)))
    }
}
pub(super) struct Handle {
    pub queue: Queue,
    pub volume: Arc<Volume>,
    pub transfer: std::sync::Mutex<()>,
}
pub(super) unsafe fn handle<'a>(pointer: *mut c_void) -> IoResult<&'a Handle> {
    (pointer as *const Handle)
        .as_ref()
        .ok_or_else(|| "V2 handle is null".into())
}
pub(super) unsafe fn input<'a>(pointer: *const u8, length: u32) -> IoResult<&'a [u8]> {
    if length == 0 {
        Ok(&[])
    } else if pointer.is_null() {
        Err("write buffer is null".into())
    } else {
        Ok(slice::from_raw_parts(pointer, length as usize))
    }
}
pub(super) unsafe fn output<'a>(pointer: *mut u8, length: u32) -> IoResult<&'a mut [u8]> {
    if length == 0 {
        Ok(&mut [])
    } else if pointer.is_null() {
        Err("read buffer is null".into())
    } else {
        Ok(slice::from_raw_parts_mut(pointer, length as usize))
    }
}
pub(super) unsafe fn copy_text(pointer: *mut c_char, length: u32, data: &[u8]) -> IoResult<()> {
    if pointer.is_null() || length == 0 {
        return Err("text output buffer is null or empty".into());
    }
    *pointer = 0;
    if data.len() >= length as usize {
        return Err(format!(
            "output buffer too small; {} bytes required including NUL",
            data.len() + 1
        ));
    }
    ptr::copy_nonoverlapping(data.as_ptr(), pointer.cast(), data.len());
    *pointer.add(data.len()) = 0;
    Ok(())
}
fn bytes(result: Output) -> IoResult<Vec<u8>> {
    match result {
        Output::Bytes(bytes) => Ok(bytes),
        _ => Err("unexpected metadata result".into()),
    }
}
fn unit(result: Output) -> IoResult<()> {
    match result {
        Output::Unit => Ok(()),
        _ => Err("unexpected operation result".into()),
    }
}
fn aligned(offset: u64, length: u64) -> IoResult<()> {
    if !offset.is_multiple_of(512) || !length.is_multiple_of(512) {
        return Err("I/O range must be 512-byte aligned".into());
    }
    offset.checked_add(length).ok_or("I/O range overflow")?;
    Ok(())
}

struct CoreBackend {
    volume: Arc<Volume>,
}
impl Backend for CoreBackend {
    fn execute(&self, operation: &Operation) -> IoResult<Output> {
        let v = &self.volume;
        let error = |e: crate::v2::Error| e.to_string();
        match operation {
            Operation::Read {
                offset,
                length,
                fua,
            } => {
                let mut data = Zeroizing::new(vec![0; (*length).max(READ_ALLOCATION)]);
                if *fua {
                    v.flush().map_err(error)?;
                    v.read_persistent(*offset, &mut data[..*length])
                        .map_err(error)?;
                } else {
                    v.read(*offset, &mut data[..*length]).map_err(error)?;
                }
                Ok(Output::Data(data, *length))
            }
            Operation::Write { offset, data, fua } => {
                v.write(*offset, data).map_err(error)?;
                if *fua {
                    v.flush().map_err(error)?;
                }
                Ok(Output::Unit)
            }
            Operation::Trim {
                offset,
                length,
                fua,
            } => {
                v.trim(*offset, *length).map_err(error)?;
                if *fua {
                    v.flush().map_err(error)?;
                }
                Ok(Output::Unit)
            }
            Operation::Flush => {
                v.flush().map_err(error)?;
                Ok(Output::Unit)
            }
            Operation::Compact => {
                v.compact().map_err(error)?;
                Ok(Output::Unit)
            }
            Operation::Capacity => Ok(Output::Number(v.capacity())),
            Operation::Info => Ok(Output::Bytes(
                serde_json::to_vec(&v.info().map_err(error)?).map_err(|e| e.to_string())?,
            )),
            Operation::SnapshotCreate => Ok(Output::Bytes(
                v.snapshot_create().map_err(error)?.into_bytes(),
            )),
            Operation::SnapshotManifest(id) => Ok(Output::Bytes(
                serde_json::to_vec(&v.snapshot_manifest(id).map_err(error)?)
                    .map_err(|e| e.to_string())?,
            )),
            Operation::ObjectRead {
                snapshot,
                object,
                offset,
                length,
            } => {
                let mut data = Zeroizing::new(vec![0; (*length).max(READ_ALLOCATION)]);
                v.object_read(snapshot, object, *offset, &mut data[..*length])
                    .map_err(error)?;
                Ok(Output::Data(data, *length))
            }
            Operation::SnapshotRelease(id) => {
                v.snapshot_release(id).map_err(error)?;
                Ok(Output::Unit)
            }
            Operation::V3Control(request) => Ok(Output::Bytes(
                serde_json::to_vec(&v.control_v3(request).map_err(error)?)
                    .map_err(|e| e.to_string())?,
            )),
        }
    }
}

/// Pointers are borrowed until release_completion(token), never thread-local errors.
#[repr(C)]
pub struct ODCompletion {
    pub token: u64,
    pub status: i32,
    pub length: u32,
    pub data: *const u8,
    pub error: *const c_char,
}

#[no_mangle]
pub unsafe extern "C" fn od_v2_create(
    path: *const c_char,
    capacity: u64,
    secret: *const c_char,
) -> i32 {
    boundary(-1, || {
        let (path, password) = (text(path)?, password(secret)?);
        Volume::create(path, capacity, password.as_ref().map(|p| p.as_str()))
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_open(path: *const c_char, secret: *const c_char) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        let (path, password) = (text(path)?, password(secret)?);
        let volume =
            Volume::open(path, password.as_ref().map(|p| p.as_str())).map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}

pub(super) fn wrap_volume(volume: Volume) -> IoResult<*mut c_void> {
    let volume = Arc::new(volume);
    let queue = Queue::new(Arc::new(CoreBackend {
        volume: volume.clone(),
    }))?;
    Ok(Box::into_raw(Box::new(Handle {
        queue,
        volume,
        transfer: std::sync::Mutex::new(()),
    }))
    .cast())
}
unsafe fn read_call(
    handle_pointer: *mut c_void,
    offset: u64,
    buffer: *mut u8,
    length: u32,
    fua: bool,
) -> IoResult<()> {
    let target = output(buffer, length)?;
    aligned(offset, length as u64)?;
    match handle(handle_pointer)?.queue.call(Operation::Read {
        offset,
        length: length as usize,
        fua,
    }) {
        Ok(Output::Data(data, actual)) if actual == target.len() => {
            target.copy_from_slice(&data[..actual]);
            Ok(())
        }
        Ok(_) => {
            target.fill(0);
            Err("invalid read result".into())
        }
        Err(error) => {
            target.fill(0);
            Err(error)
        }
    }
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_read(
    h: *mut c_void,
    offset: u64,
    buffer: *mut u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        read_call(h, offset, buffer, length, false)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_read_persistent(
    h: *mut c_void,
    offset: u64,
    buffer: *mut u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        read_call(h, offset, buffer, length, true)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_write(
    h: *mut c_void,
    offset: u64,
    buffer: *const u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        aligned(offset, length as u64)?;
        unit(
            handle(h)?
                .queue
                .write(offset, input(buffer, length)?, false)?,
        )?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_trim(h: *mut c_void, offset: u64, length: u64) -> i32 {
    boundary(-1, || {
        aligned(offset, length)?;
        unit(handle(h)?.queue.call(Operation::Trim {
            offset,
            length,
            fua: false,
        })?)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_flush(h: *mut c_void) -> i32 {
    boundary(-1, || {
        unit(handle(h)?.queue.call(Operation::Flush)?)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_compact(h: *mut c_void) -> i32 {
    boundary(-1, || {
        unit(handle(h)?.queue.call(Operation::Compact)?)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_capacity(h: *mut c_void) -> u64 {
    boundary(0, || match handle(h)?.queue.call(Operation::Capacity)? {
        Output::Number(n) => Ok(n),
        _ => Err("invalid capacity result".into()),
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_info(h: *mut c_void, json: *mut c_char, length: u32) -> i32 {
    boundary(-1, || {
        copy_text(
            json,
            length,
            &bytes(handle(h)?.queue.call(Operation::Info)?)?,
        )?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_inspect(path: *const c_char, json: *mut c_char, length: u32) -> i32 {
    boundary(-1, || {
        let info = Volume::inspect(text(path)?).map_err(|e| e.to_string())?;
        copy_text(
            json,
            length,
            &serde_json::to_vec(&info).map_err(|e| e.to_string())?,
        )?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_v2_submit(
    h: *mut c_void,
    op: u32,
    offset: u64,
    length: u32,
    data: *const u8,
    flags: u32,
    token: u64,
) -> i32 {
    boundary(-1, || {
        if flags & !1 != 0 {
            return Err("unknown I/O flags".into());
        }
        if matches!(op, 1 | 2) && length as usize > READ_ALLOCATION {
            return Err("asynchronous transfer exceeds 1 MiB".into());
        }
        let q = &handle(h)?.queue;
        let fua = flags & 1 != 0;
        match op {
            1 => {
                aligned(offset, length as u64)?;
                q.submit(
                    token,
                    Operation::Read {
                        offset,
                        length: length as usize,
                        fua,
                    },
                )?;
            }
            2 => {
                aligned(offset, length as u64)?;
                q.submit_write(token, offset, input(data, length)?, fua)?;
            }
            3 if offset == 0 && length == 0 => q.submit(token, Operation::Flush)?,
            3 => return Err("Flush takes zero offset and length".into()),
            4 => {
                aligned(offset, length as u64)?;
                q.submit(
                    token,
                    Operation::Trim {
                        offset,
                        length: length as u64,
                        fua,
                    },
                )?;
            }
            _ => return Err("unknown asynchronous operation".into()),
        }
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_next_completion(
    h: *mut c_void,
    out: *mut ODCompletion,
    timeout_ms: u32,
) -> i32 {
    boundary(-1, || {
        if out.is_null() {
            return Err("completion output is null".into());
        }
        ptr::write(
            out,
            ODCompletion {
                token: 0,
                status: 0,
                length: 0,
                data: ptr::null(),
                error: ptr::null(),
            },
        );
        match handle(h)?
            .queue
            .next_completion(Duration::from_millis(timeout_ms as u64))?
        {
            None => Ok(1),
            Some(c) => {
                ptr::write(
                    out,
                    ODCompletion {
                        token: c.token,
                        status: c.status,
                        length: c.length,
                        data: c.data.as_ref().map_or(ptr::null(), |b| b.as_ptr()),
                        error: c.error.as_ref().map_or(ptr::null(), |e| e.as_ptr()),
                    },
                );
                Ok(0)
            }
        }
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_release_completion(h: *mut c_void, token: u64) -> i32 {
    boundary(-1, || {
        handle(h)?.queue.release_completion(token)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_drain(h: *mut c_void) -> i32 {
    boundary(-1, || {
        handle(h)?.queue.drain()?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_v2_cancel_submissions(h: *mut c_void) -> i32 {
    boundary(-1, || {
        handle(h)?.queue.cancel_submissions()?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_v2_snapshot_create(
    h: *mut c_void,
    id: *mut c_char,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if id.is_null() || length == 0 {
            return Err("snapshot ID output is null or empty".into());
        }
        *id = 0;
        let queue = &handle(h)?.queue;
        let value = bytes(queue.call(Operation::SnapshotCreate)?)?;
        if let Err(error) = copy_text(id, length, &value) {
            let snapshot = String::from_utf8(value).map_err(|e| e.to_string())?;
            unit(queue.call(Operation::SnapshotRelease(snapshot))?)?;
            return Err(error);
        }
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_snapshot_manifest(
    h: *mut c_void,
    id: *const c_char,
    json: *mut c_char,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if json.is_null() && length != 0 {
            return Err("manifest output is null".into());
        }
        let value = bytes(
            handle(h)?
                .queue
                .call(Operation::SnapshotManifest(text(id)?))?,
        )?;
        let required = i32::try_from(
            value
                .len()
                .checked_add(1)
                .ok_or("manifest length overflow")?,
        )
        .map_err(|_| "manifest exceeds ABI size limit")?;
        if !json.is_null() && length != 0 {
            *json = 0;
            if length as usize > value.len() {
                copy_text(json, length, &value)?;
            }
        }
        Ok(required)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_object_read(
    h: *mut c_void,
    snapshot: *const c_char,
    object: *const c_char,
    offset: u64,
    buffer: *mut u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        let target = output(buffer, length)?;
        let result = handle(h)?.queue.call(Operation::ObjectRead {
            snapshot: text(snapshot)?,
            object: text(object)?,
            offset,
            length: length as usize,
        });
        match result {
            Ok(Output::Data(data, actual)) if actual == target.len() => {
                target.copy_from_slice(&data[..actual]);
                Ok(0)
            }
            Ok(_) => {
                target.fill(0);
                Err("invalid object read result".into())
            }
            Err(error) => {
                target.fill(0);
                Err(error)
            }
        }
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_snapshot_release(h: *mut c_void, id: *const c_char) -> i32 {
    boundary(-1, || {
        unit(
            handle(h)?
                .queue
                .call(Operation::SnapshotRelease(text(id)?))?,
        )?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v2_close(h: *mut c_void) {
    boundary((), || {
        if !h.is_null() {
            drop(Box::from_raw(h as *mut Handle));
        }
        Ok(())
    });
}
#[no_mangle]
pub extern "C" fn od_v2_last_error() -> *const c_char {
    LAST_ERROR.with(|e| e.borrow().as_ptr())
}

#[cfg(test)]
mod tests {
    use super::*;
    struct OpenHandle(*mut c_void);
    impl Drop for OpenHandle {
        fn drop(&mut self) {
            unsafe {
                od_v2_close(self.0);
            }
        }
    }
    unsafe fn success(status: i32) {
        assert_eq!(
            status,
            0,
            "{}",
            CStr::from_ptr(od_v2_last_error()).to_string_lossy()
        );
    }
    unsafe fn next(h: *mut c_void) -> ODCompletion {
        let mut out = ODCompletion {
            token: 0,
            status: 0,
            length: 0,
            data: ptr::null(),
            error: ptr::null(),
        };
        success(od_v2_next_completion(h, &mut out, 5000));
        out
    }
    #[test]
    fn completion_layout_is_native_x64() {
        assert_eq!(std::mem::size_of::<ODCompletion>(), 32);
        assert_eq!(std::mem::align_of::<ODCompletion>(), 8);
    }
    #[test]
    fn null_handles_are_errors_without_unwinding() {
        unsafe {
            assert_eq!(od_v2_drain(ptr::null_mut()), -1);
            assert!(!CStr::from_ptr(od_v2_last_error()).to_bytes().is_empty());
            assert_eq!(
                od_v2_next_completion(ptr::null_mut(), ptr::null_mut(), 0),
                -1
            );
            od_v2_close(ptr::null_mut());
        }
    }

    #[test]
    fn encrypted_ffi_async_sync_error_and_reopen() {
        let directory = tempfile::tempdir().unwrap();
        let path = CString::new(directory.path().join("async.odv2").to_str().unwrap()).unwrap();
        let password = CString::new("V2-test-only-密码").unwrap();
        unsafe {
            success(od_v2_create(path.as_ptr(), 64 << 20, password.as_ptr()));
            let disk = OpenHandle(od_v2_open(path.as_ptr(), password.as_ptr()));
            assert!(
                !disk.0.is_null(),
                "{}",
                CStr::from_ptr(od_v2_last_error()).to_string_lossy()
            );
            assert_eq!(od_v2_capacity(disk.0), 64 << 20);
            let mut source = vec![19; 512];
            success(od_v2_submit(disk.0, 2, 0, 512, source.as_ptr(), 0, 1));
            source.fill(71);
            let write = next(disk.0);
            assert_eq!((write.token, write.status), (1, 0));
            assert!(write.data.is_null());
            success(od_v2_release_completion(disk.0, 1));
            // The synchronous read must follow the asynchronous cache publication.
            let mut got = [0; 512];
            success(od_v2_read(disk.0, 0, got.as_mut_ptr(), 512));
            assert_eq!(got, [19; 512]);
            success(od_v2_submit(disk.0, 1, 0, 512, ptr::null(), 1, 2));
            let read = next(disk.0);
            assert_eq!((read.token, read.status, read.length), (2, 0, 512));
            assert_eq!(
                &slice::from_raw_parts(read.data, READ_ALLOCATION)[..512],
                &[19; 512]
            );
            assert!(slice::from_raw_parts(read.data, READ_ALLOCATION)[512..]
                .iter()
                .all(|b| *b == 0));
            success(od_v2_release_completion(disk.0, 2));
            // This passes admission validation but is rejected by the storage backend.
            success(od_v2_submit(disk.0, 1, 64 << 20, 512, ptr::null(), 0, 3));
            let error = next(disk.0);
            assert_eq!((error.status, error.length), (-1, 0));
            let message = CStr::from_ptr(error.error).to_owned();
            assert_eq!(od_v2_flush(ptr::null_mut()), -1);
            assert_eq!(CStr::from_ptr(error.error), message.as_c_str());
            assert!(slice::from_raw_parts(error.data, READ_ALLOCATION)
                .iter()
                .all(|b| *b == 0));
            success(od_v2_release_completion(disk.0, 3));
            success(od_v2_drain(disk.0));
            success(od_v2_flush(disk.0));
            drop(disk);
            let reopened = OpenHandle(od_v2_open(path.as_ptr(), password.as_ptr()));
            assert!(!reopened.0.is_null());
            success(od_v2_read(reopened.0, 0, got.as_mut_ptr(), 512));
            assert_eq!(got, [19; 512]);
        }
    }

    #[test]
    fn snapshot_ffi_size_query_export_and_short_id_cleanup() {
        let directory = tempfile::tempdir().unwrap();
        let path = CString::new(directory.path().join("export.odv2").to_str().unwrap()).unwrap();
        unsafe {
            success(od_v2_create(path.as_ptr(), 64 << 20, ptr::null()));
            let disk = OpenHandle(od_v2_open(path.as_ptr(), ptr::null()));
            assert!(
                !disk.0.is_null(),
                "{}",
                CStr::from_ptr(od_v2_last_error()).to_string_lossy()
            );
            success(od_v2_write(disk.0, 0, [27; 4096].as_ptr(), 4096));
            let mut short_id = [9i8; 1];
            assert_eq!(od_v2_snapshot_create(disk.0, short_id.as_mut_ptr(), 1), -1);
            let mut info = vec![0i8; 16384];
            success(od_v2_info(disk.0, info.as_mut_ptr(), info.len() as u32));
            let info: serde_json::Value =
                serde_json::from_slice(CStr::from_ptr(info.as_ptr()).to_bytes()).unwrap();
            assert_eq!(
                info["snapshot_count"], 0,
                "undersized output must not leak a pinned snapshot"
            );
            let mut id = [0i8; 128];
            success(od_v2_snapshot_create(
                disk.0,
                id.as_mut_ptr(),
                id.len() as u32,
            ));
            let required = od_v2_snapshot_manifest(disk.0, id.as_ptr(), ptr::null_mut(), 0);
            assert!(required > 1);
            let mut short = [1i8; 4];
            assert_eq!(
                od_v2_snapshot_manifest(disk.0, id.as_ptr(), short.as_mut_ptr(), 4),
                required
            );
            assert_eq!(short[0], 0);
            let mut json = vec![0i8; required as usize];
            assert_eq!(
                od_v2_snapshot_manifest(disk.0, id.as_ptr(), json.as_mut_ptr(), required as u32),
                required
            );
            let manifest: serde_json::Value =
                serde_json::from_slice(CStr::from_ptr(json.as_ptr()).to_bytes()).unwrap();
            let object = CString::new(manifest["objects"][0]["id"].as_str().unwrap()).unwrap();
            let mut payload = vec![0; 4 << 20];
            success(od_v2_object_read(
                disk.0,
                id.as_ptr(),
                object.as_ptr(),
                0,
                payload.as_mut_ptr(),
                payload.len() as u32,
            ));
            success(od_v2_snapshot_release(disk.0, id.as_ptr()));
            assert_eq!(
                od_v2_snapshot_manifest(disk.0, id.as_ptr(), ptr::null_mut(), 0),
                -1
            );
            success(od_v2_flush(disk.0));
            drop(disk);
            // Windows enforces the volume's exclusive byte-range lock. Inspect
            // uses a separate file handle and belongs after closing the owner.
            let mut inspect = vec![0i8; 16384];
            success(od_v2_inspect(
                path.as_ptr(),
                inspect.as_mut_ptr(),
                inspect.len() as u32,
            ));
            let inspect: serde_json::Value =
                serde_json::from_slice(CStr::from_ptr(inspect.as_ptr()).to_bytes()).unwrap();
            assert_eq!(inspect["authenticated"], false);
        }
    }
}
