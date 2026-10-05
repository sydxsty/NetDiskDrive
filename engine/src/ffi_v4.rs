//! Format-4 ABI. Ordered disk I/O shares a bounded queue; inspection and pinned export do not.
//! The caller must quiesce native callbacks before closing a handle.
use crate::{
    async_io::{IoResult, Operation, Output, Queue, READ_ALLOCATION},
    async_v4::CoreBackend,
    v4::Volume,
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
        .unwrap_or_else(|_| Err("V4 FFI panic; operation failed".into()));
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
const HANDLE_MAGIC: u64 = 0x344b534944444f;
#[repr(C)]
pub(super) struct Handle {
    magic: u64,
    pub queue: Queue,
    pub volume: Arc<Volume>,
    pub cloud_control: std::sync::Mutex<()>,
}
pub(super) unsafe fn handle<'a>(pointer: *mut c_void) -> IoResult<&'a Handle> {
    if pointer.is_null() {
        return Err("V4 handle is null".into());
    }
    // Inspect the tag before constructing a typed reference: V2/V3 handles have
    // a different layout and must never be interpreted as a format-4 Handle.
    if ptr::read_unaligned(pointer.cast::<u64>()) != HANDLE_MAGIC {
        return Err("handle does not belong to format 4".into());
    }
    Ok(&*pointer.cast::<Handle>())
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
pub unsafe extern "C" fn od_v4_create(
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
pub unsafe extern "C" fn od_v4_create_sized(
    path: *const c_char,
    capacity: u64,
    secret: *const c_char,
    object_size_bytes: u32,
) -> i32 {
    boundary(-1, || {
        let (path, password) = (text(path)?, password(secret)?);
        Volume::create_sized(
            path,
            capacity,
            password.as_ref().map(|p| p.as_str()),
            object_size_bytes as u64,
        )
        .map_err(|e| e.to_string())?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_open(path: *const c_char, secret: *const c_char) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        let (path, password) = (text(path)?, password(secret)?);
        let volume =
            Volume::open(path, password.as_ref().map(|p| p.as_str())).map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}

pub type ODObjectProvider = unsafe extern "C" fn(
    context: *mut c_void,
    object_id: *const c_char,
    sha256: *const c_char,
    output: *mut u8,
    length: u32,
    error: *mut c_char,
    error_length: u32,
) -> i32;

#[no_mangle]
pub unsafe extern "C" fn od_v4_set_object_provider(
    h: *mut c_void,
    callback: Option<ODObjectProvider>,
    context: *mut c_void,
) -> i32 {
    boundary(-1, || {
        let object_size = handle(h)?.volume.object_size();
        let provider: Option<Arc<crate::v4::ObjectProvider>> = callback.map(|callback| {
            let address = context as usize;
            Arc::new(move |object: &crate::v4::RemoteObject, output: &mut [u8]| {
                if output.len() != object_size as usize || object.length != object_size {
                    return Err(crate::v4::Error::Invalid("provider object length".into()));
                }
                let id = CString::new(object.id.as_str())
                    .map_err(|_| crate::v4::Error::Invalid("provider object id".into()))?;
                let sha = CString::new(object.sha256.as_str())
                    .map_err(|_| crate::v4::Error::Invalid("provider object hash".into()))?;
                let mut error = [0u8; 1024];
                let status = unsafe {
                    callback(
                        address as *mut c_void,
                        id.as_ptr(),
                        sha.as_ptr(),
                        output.as_mut_ptr(),
                        output.len() as u32,
                        error.as_mut_ptr().cast(),
                        error.len() as u32,
                    )
                };
                if status != 0 {
                    output.fill(0);
                    let end = error.iter().position(|b| *b == 0).unwrap_or(error.len());
                    let message = if end == 0 {
                        "cloud object provider failed".into()
                    } else {
                        String::from_utf8_lossy(&error[..end]).into_owned()
                    };
                    return Err(crate::v4::Error::Invalid(message));
                }
                Ok(())
            }) as Arc<crate::v4::ObjectProvider>
        });
        handle(h)?
            .volume
            .set_object_provider(provider)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_v4_lazy_begin(
    path: *const c_char,
    root: *const u8,
    length: u32,
    secret: *const c_char,
    backing_json: *const c_char,
) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        if !matches!(length, 4_194_304 | 8_388_608 | 16_777_216) {
            return Err("root must be a 4, 8 or 16 MiB object".into());
        }
        let (path, secret, backing) = (text(path)?, password(secret)?, text(backing_json)?);
        if backing.len() > 16384 {
            return Err("lazy backing descriptor is too large".into());
        }
        let backing = serde_json::from_str(&backing).map_err(|e| format!("lazy backing: {e}"))?;
        let volume = Volume::lazy_begin(
            path,
            input(root, length)?,
            secret.as_ref().map(|p| p.as_str()),
            backing,
        )
        .map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_v4_lazy_import(
    h: *mut c_void,
    object_id: *const c_char,
    data: *const u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if length as u64 != handle(h)?.volume.object_size() {
            return Err("lazy object length differs from volume geometry".into());
        }
        handle(h)?
            .volume
            .lazy_import(&text(object_id)?, input(data, length)?)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}

pub(super) fn wrap_volume(volume: Volume) -> IoResult<*mut c_void> {
    let volume = Arc::new(volume);
    let queue = Queue::new(Arc::new(CoreBackend {
        volume: volume.clone(),
    }))?;
    Ok(Box::into_raw(Box::new(Handle {
        magic: HANDLE_MAGIC,
        queue,
        volume,
        cloud_control: std::sync::Mutex::new(()),
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
pub unsafe extern "C" fn od_v4_read(
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
pub unsafe extern "C" fn od_v4_read_persistent(
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
pub unsafe extern "C" fn od_v4_write(
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
pub unsafe extern "C" fn od_v4_trim(h: *mut c_void, offset: u64, length: u64) -> i32 {
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
pub unsafe extern "C" fn od_v4_set_read_only(h: *mut c_void, enabled: u32) -> i32 {
    boundary(-1, || {
        if enabled > 1 {
            return Err("read-only flag must be 0 or 1".into());
        }
        handle(h)?.queue.call(Operation::V3Control(
            serde_json::json!({"cmd":"volume.read_only","enabled":enabled==1}),
        ))?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_flush(h: *mut c_void) -> i32 {
    boundary(-1, || {
        unit(handle(h)?.queue.call(Operation::Flush)?)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_compact(h: *mut c_void) -> i32 {
    boundary(-1, || {
        handle(h)?.volume.compact().map_err(|e| e.to_string())?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_capacity(h: *mut c_void) -> u64 {
    boundary(0, || Ok(handle(h)?.volume.capacity()))
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_info(h: *mut c_void, json: *mut c_char, length: u32) -> i32 {
    boundary(-1, || {
        let info = handle(h)?.volume.info().map_err(|e| e.to_string())?;
        copy_text(
            json,
            length,
            &serde_json::to_vec(&info).map_err(|e| e.to_string())?,
        )?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_inspect(path: *const c_char, json: *mut c_char, length: u32) -> i32 {
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
pub unsafe extern "C" fn od_v4_submit(
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
pub unsafe extern "C" fn od_v4_next_completion(
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
pub unsafe extern "C" fn od_v4_release_completion(h: *mut c_void, token: u64) -> i32 {
    boundary(-1, || {
        handle(h)?.queue.release_completion(token)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_drain(h: *mut c_void) -> i32 {
    boundary(-1, || {
        handle(h)?.queue.drain()?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_v4_cancel_submissions(h: *mut c_void) -> i32 {
    boundary(-1, || {
        handle(h)?.queue.cancel_submissions()?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_v4_snapshot_create(
    h: *mut c_void,
    id: *mut c_char,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if id.is_null() || length < 37 {
            if !id.is_null() && length != 0 {
                *id = 0;
            }
            return Err("snapshot ID output requires 37 bytes; no snapshot was created".into());
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
pub unsafe extern "C" fn od_v4_snapshot_manifest(
    h: *mut c_void,
    id: *const c_char,
    json: *mut c_char,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if json.is_null() && length != 0 {
            return Err("manifest output is null".into());
        }
        let value = serde_json::to_vec(
            &handle(h)?
                .volume
                .snapshot_manifest(&text(id)?)
                .map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
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
pub unsafe extern "C" fn od_v4_object_read(
    h: *mut c_void,
    snapshot: *const c_char,
    object: *const c_char,
    offset: u64,
    buffer: *mut u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if length as u64 > handle(h)?.volume.object_size() {
            return Err("object read exceeds volume object size".into());
        }
        let out = output(buffer, length)?;
        match handle(h)?
            .volume
            .object_read(&text(snapshot)?, &text(object)?, offset, out)
        {
            Ok(()) => Ok(0),
            Err(error) => {
                out.fill(0);
                Err(error.to_string())
            }
        }
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_snapshot_release(h: *mut c_void, id: *const c_char) -> i32 {
    boundary(-1, || {
        handle(h)?
            .volume
            .snapshot_release(&text(id)?)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_close(h: *mut c_void) {
    boundary((), || {
        if !h.is_null() {
            let checked = handle(h)?;
            checked.queue.shutdown();
            checked
                .volume
                .set_object_provider(None)
                .map_err(|e| e.to_string())?;
            let mut owned = Box::from_raw(h as *mut Handle);
            owned.magic = 0;
            drop(owned);
        }
        Ok(())
    });
}
#[no_mangle]
pub extern "C" fn od_v4_last_error() -> *const c_char {
    LAST_ERROR.with(|e| e.borrow().as_ptr())
}

const CONTROL: u32 = 131072;
#[no_mangle]
pub unsafe extern "C" fn od_v4_read_fua(
    h: *mut c_void,
    offset: u64,
    buffer: *mut u8,
    length: u32,
) -> i32 {
    od_v4_read_persistent(h, offset, buffer, length)
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_write_fua(
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
                .write(offset, input(buffer, length)?, true)?,
        )?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_control(
    h: *mut c_void,
    request: *const c_char,
    json: *mut c_char,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if json.is_null() || length < CONTROL {
            return Err(
                "control output needs at least 131072 bytes; command was not executed".into(),
            );
        }
        *json = 0;
        let input = text(request)?;
        if input.len() > 65536 {
            return Err("control request is too large".into());
        }
        let request: serde_json::Value = serde_json::from_str(&input).map_err(|e| e.to_string())?;
        let cmd = request["cmd"].as_str().ok_or("command missing")?;
        let h = handle(h)?;
        // Serialize only cloud mutations. The job-existence check and cut admission
        // must be atomic with cloud.commit, so a resumed job cannot become a new
        // snapshot without joining the disk I/O admission order.
        let cloud_mutation = matches!(
            cmd,
            "cloud.prepare" | "cloud.bind" | "cloud.pause" | "cloud.receipt" | "cloud.commit"
        );
        let _cloud = if cloud_mutation {
            Some(
                h.cloud_control
                    .lock()
                    .map_err(|_| "cloud operation lock poisoned")?,
            )
        } else {
            None
        };
        let needs_cut = cmd == "snapshot.create"
            || (cmd == "cloud.prepare" && {
                let status = h
                    .volume
                    .control(&serde_json::json!({"cmd":"cloud.status"}))
                    .map_err(|e| e.to_string())?;
                let status = status.get("status").unwrap_or(&status);
                status.get("job").is_none_or(serde_json::Value::is_null)
            });
        let value = if needs_cut {
            bytes(h.queue.call(Operation::V3Control(request))?)?
        } else {
            serde_json::to_vec(&h.volume.control(&request).map_err(|e| e.to_string())?)
                .map_err(|e| e.to_string())?
        };
        copy_text(json, length, &value)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_read_export(
    h: *mut c_void,
    job: *const c_char,
    object: *const c_char,
    offset: u64,
    data: *mut u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if length as u64 > handle(h)?.volume.object_size() {
            return Err("export read exceeds volume object size".into());
        }
        let out = output(data, length)?;
        match handle(h)?
            .volume
            .read_export(&text(job)?, &text(object)?, offset, out)
        {
            Ok(()) => Ok(0),
            Err(error) => {
                out.fill(0);
                Err(error.to_string())
            }
        }
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_restore_begin_options(
    path: *const c_char,
    root: *const u8,
    length: u32,
    secret: *const c_char,
    options: *const c_char,
) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        if !matches!(length, 4_194_304 | 8_388_608 | 16_777_216) {
            return Err("root must be a 4, 8 or 16 MiB object".into());
        }
        let options = text(options)?;
        if options.len() > 32768 {
            return Err("restore options too large".into());
        }
        let options =
            serde_json::from_str(&options).map_err(|e| format!("restore options: {e}"))?;
        let p = password(secret)?;
        let volume = Volume::restore_begin_options(
            text(path)?,
            input(root, length)?,
            p.as_ref().map(|p| p.as_str()),
            options,
        )
        .map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_restore_begin(
    path: *const c_char,
    root: *const u8,
    length: u32,
    secret: *const c_char,
) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        if !matches!(length, 4_194_304 | 8_388_608 | 16_777_216) {
            return Err("root must be a 4, 8 or 16 MiB object".into());
        }
        let p = password(secret)?;
        let volume = Volume::restore_begin_v4(
            text(path)?,
            input(root, length)?,
            p.as_ref().map(|p| p.as_str()),
        )
        .map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_restore_accept(
    h: *mut c_void,
    object: *const c_char,
    data: *const u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if length as u64 != handle(h)?.volume.object_size() {
            return Err("object length differs from volume geometry".into());
        }
        handle(h)?
            .volume
            .restore_accept(&text(object)?, input(data, length)?)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_restore_snapshot_begin(
    source: *mut c_void,
    snapshot: *const c_char,
    path: *const c_char,
    secret: *const c_char,
) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        let p = password(secret)?;
        let source = handle(source)?;
        // The named source snapshot is already durable; do not flush unrelated live writes.
        let volume = Volume::restore_snapshot_begin_v4(
            &source.volume,
            &text(snapshot)?,
            text(path)?,
            p.as_ref().map(|p| p.as_str()),
        )
        .map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v4_restore_snapshot_resume(
    target: *mut c_void,
    source: *mut c_void,
) -> i32 {
    boundary(-1, || {
        handle(target)?
            .volume
            .restore_snapshot_resume_v4(&handle(source)?.volume)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Disk(*mut c_void);
    impl Drop for Disk {
        fn drop(&mut self) {
            unsafe {
                od_v4_close(self.0);
            }
        }
    }
    fn c(value: &str) -> CString {
        CString::new(value).unwrap()
    }
    unsafe fn success(result: i32) {
        assert_eq!(
            result,
            0,
            "{}",
            CStr::from_ptr(od_v4_last_error()).to_string_lossy()
        );
    }
    #[test]
    fn v4_provider_abi_registration_and_close_do_not_require_a_remote_source() {
        unsafe extern "C" fn unexpected_provider(
            context: *mut c_void,
            _: *const c_char,
            _: *const c_char,
            _: *mut u8,
            _: u32,
            _: *mut c_char,
            _: u32,
        ) -> i32 {
            (*(context as *const std::sync::atomic::AtomicUsize))
                .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            -1
        }
        unsafe {
            let directory = tempfile::tempdir().unwrap();
            let path = c(directory.path().join("provider-ffi.odv4").to_str().unwrap());
            success(od_v4_create(path.as_ptr(), 64 << 20, ptr::null()));
            let disk = Disk(od_v4_open(path.as_ptr(), ptr::null()));
            let calls = std::sync::atomic::AtomicUsize::new(0);
            success(od_v4_set_object_provider(
                disk.0,
                Some(unexpected_provider),
                &calls as *const _ as *mut c_void,
            ));
            let mut bytes = [99u8; 512];
            success(od_v4_read(
                disk.0,
                0,
                bytes.as_mut_ptr(),
                bytes.len() as u32,
            ));
            assert_eq!(bytes, [0; 512]);
            assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 0);
            success(od_v4_set_object_provider(disk.0, None, ptr::null_mut()));
            assert!(od_v4_set_object_provider(ptr::null_mut(), None, ptr::null_mut()) < 0);
            // Close also unregisters a provider, even without an explicit final NULL call.
            success(od_v4_set_object_provider(
                disk.0,
                Some(unexpected_provider),
                &calls as *const _ as *mut c_void,
            ));
            drop(disk);
            assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 0);
        }
    }
    #[test]
    fn v4_abi_orders_copied_writes_fua_and_reopen() {
        unsafe {
            assert_eq!(std::mem::size_of::<ODCompletion>(), 32);
            let directory = tempfile::tempdir().unwrap();
            let path = c(directory.path().join("ffi.odv4").to_str().unwrap());
            success(od_v4_create(path.as_ptr(), 64 << 20, ptr::null()));
            let disk = Disk(od_v4_open(path.as_ptr(), ptr::null()));
            assert!(
                !disk.0.is_null(),
                "{}",
                CStr::from_ptr(od_v4_last_error()).to_string_lossy()
            );
            let expected = vec![0x71; 4096];
            let mut write = expected.clone();
            success(od_v4_submit(
                disk.0,
                2,
                512,
                write.len() as u32,
                write.as_ptr(),
                0,
                1,
            ));
            write.fill(0x99);
            let mut completion: ODCompletion = std::mem::zeroed();
            success(od_v4_next_completion(disk.0, &mut completion, 5000));
            assert_eq!((completion.token, completion.status), (1, 0));
            success(od_v4_release_completion(disk.0, 1));
            let mut read = vec![0; expected.len()];
            success(od_v4_read_fua(
                disk.0,
                512,
                read.as_mut_ptr(),
                read.len() as u32,
            ));
            assert_eq!(read, expected);
            assert_eq!(
                od_v4_submit(
                    disk.0,
                    1,
                    0,
                    READ_ALLOCATION as u32 + 512,
                    ptr::null(),
                    0,
                    2
                ),
                -1
            );
            assert_eq!(od_v4_submit(disk.0, 1, 0, 512, ptr::null(), 2, 2), -1);
            success(od_v4_submit(disk.0, 1, 64 << 20, 512, ptr::null(), 0, 3));
            success(od_v4_next_completion(disk.0, &mut completion, 5000));
            assert_eq!(
                (completion.token, completion.status, completion.length),
                (3, -1, 0)
            );
            assert!(!completion.data.is_null());
            assert!(!completion.error.is_null());
            assert_eq!(*completion.data.add(READ_ALLOCATION - 1), 0);
            success(od_v4_release_completion(disk.0, 3));
            success(od_v4_drain(disk.0));
            drop(disk);
            let reopened = Disk(od_v4_open(path.as_ptr(), ptr::null()));
            assert!(!reopened.0.is_null());
            success(od_v4_read(
                reopened.0,
                512,
                read.as_mut_ptr(),
                read.len() as u32,
            ));
            assert_eq!(read, expected);
            let mut info = vec![0i8; 16384];
            success(od_v4_info(reopened.0, info.as_mut_ptr(), info.len() as u32));
            let info: serde_json::Value =
                serde_json::from_str(CStr::from_ptr(info.as_ptr()).to_str().unwrap()).unwrap();
            assert_eq!(info["format_version"], 4);
        }
    }
    #[test]
    fn v4_rejects_legacy_containers_handles_and_small_mutation_outputs() {
        unsafe {
            let directory = tempfile::tempdir().unwrap();
            let old_path = c(directory.path().join("legacy.odv3").to_str().unwrap());
            crate::v2::Volume::create_v3(directory.path().join("legacy.odv3"), 64 << 20, None)
                .unwrap();
            assert!(od_v4_open(old_path.as_ptr(), ptr::null()).is_null());
            let old = crate::ffi_v3::od_v3_open(old_path.as_ptr(), ptr::null());
            assert!(!old.is_null());
            assert_eq!(od_v4_flush(old), -1);
            crate::ffi_v2::od_v2_close(old);
            let path = c(directory.path().join("new.odv4").to_str().unwrap());
            success(od_v4_create(path.as_ptr(), 64 << 20, ptr::null()));
            let disk = Disk(od_v4_open(path.as_ptr(), ptr::null()));
            assert!(!disk.0.is_null());
            let mut tiny = [0i8; 1];
            assert_eq!(
                od_v4_control(
                    disk.0,
                    c(r#"{"cmd":"snapshot.create","name":"must not exist"}"#).as_ptr(),
                    tiny.as_mut_ptr(),
                    1
                ),
                -1
            );
            assert_eq!(od_v4_snapshot_create(disk.0, tiny.as_mut_ptr(), 1), -1);
            let mut out = vec![0i8; CONTROL as usize];
            success(od_v4_control(
                disk.0,
                c(r#"{"cmd":"snapshot.list"}"#).as_ptr(),
                out.as_mut_ptr(),
                CONTROL,
            ));
            let list: serde_json::Value =
                serde_json::from_str(CStr::from_ptr(out.as_ptr()).to_str().unwrap()).unwrap();
            assert!(list["items"].as_array().unwrap().is_empty());
        }
    }
}
