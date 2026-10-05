use crate::{
    async_io::{Operation, Output},
    ffi_v2::*,
    v2::Volume,
};
use std::{
    ffi::{c_char, c_void},
    ptr,
};
const CONTROL: u32 = 131072;
#[no_mangle]
pub unsafe extern "C" fn od_v3_create(
    path: *const c_char,
    capacity: u64,
    secret: *const c_char,
) -> i32 {
    boundary(-1, || {
        let p = password(secret)?;
        Volume::create_v3(text(path)?, capacity, p.as_ref().map(|s| s.as_str()))
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_open(path: *const c_char, secret: *const c_char) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        let p = password(secret)?;
        let volume =
            Volume::open(text(path)?, p.as_ref().map(|s| s.as_str())).map_err(|e| e.to_string())?;
        if volume.info().map_err(|e| e.to_string())?.format_version != 3 {
            return Err("use the older release for a non-V3 volume".into());
        }
        wrap_volume(volume)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_inspect(path: *const c_char, json: *mut c_char, len: u32) -> i32 {
    boundary(-1, || {
        let info = Volume::inspect(text(path)?).map_err(|e| e.to_string())?;
        if info.format_version != 3 {
            return Err("not a format-3 container".into());
        }
        copy_text(
            json,
            len,
            &serde_json::to_vec(&info).map_err(|e| e.to_string())?,
        )?;
        Ok(0)
    })
}
#[no_mangle]
pub extern "C" fn od_v3_last_error() -> *const c_char {
    od_v2_last_error()
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_control(
    h: *mut c_void,
    request: *const c_char,
    json: *mut c_char,
    len: u32,
) -> i32 {
    boundary(-1, || {
        if json.is_null() || len < CONTROL {
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
        let bytes = if matches!(
            cmd,
            "cloud.status"
                | "cloud.list"
                | "cloud.objects"
                | "cloud.published_objects"
                | "debug.blocks"
                | "compact.status"
                | "restore.status"
        ) {
            serde_json::to_vec(&h.volume.control_v3(&request).map_err(|e| e.to_string())?)
                .map_err(|e| e.to_string())?
        } else {
            match h.queue.call(Operation::V3Control(request))? {
                Output::Bytes(bytes) => bytes,
                _ => return Err("invalid V3 response".into()),
            }
        };
        copy_text(json, len, &bytes)?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_read_export(
    h: *mut c_void,
    job: *const c_char,
    object: *const c_char,
    offset: u64,
    data: *mut u8,
    len: u32,
) -> i32 {
    boundary(-1, || {
        if len > 4 * 1024 * 1024 {
            return Err("export read exceeds 4 MiB".into());
        }
        let h = handle(h)?;
        let _transfer = h.transfer.lock().map_err(|_| "transfer lock poisoned")?;
        let out = output(data, len)?;
        match h
            .volume
            .read_export_v3(&text(job)?, &text(object)?, offset, out)
        {
            Ok(()) => Ok(0),
            Err(e) => {
                out.fill(0);
                Err(e.to_string())
            }
        }
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_restore_begin(
    path: *const c_char,
    root: *const u8,
    len: u32,
    secret: *const c_char,
) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        if len != 4 * 1024 * 1024 {
            return Err("root must be exactly 4 MiB".into());
        }
        let p = password(secret)?;
        let volume = Volume::restore_begin_v3(
            text(path)?,
            input(root, len)?,
            p.as_ref().map(|p| p.as_str()),
        )
        .map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_restore_accept(
    h: *mut c_void,
    object: *const c_char,
    data: *const u8,
    len: u32,
) -> i32 {
    boundary(-1, || {
        if len != 4 * 1024 * 1024 {
            return Err("object must be exactly 4 MiB".into());
        }
        let h = handle(h)?;
        let _transfer = h.transfer.lock().map_err(|_| "transfer lock poisoned")?;
        h.volume
            .restore_accept_v3(&text(object)?, input(data, len)?)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_restore_snapshot_begin(
    h: *mut c_void,
    snapshot: *const c_char,
    path: *const c_char,
    secret: *const c_char,
) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        let p = password(secret)?;
        let volume = Volume::restore_snapshot_begin_v3(
            handle(h)?.volume.clone(),
            &text(snapshot)?,
            text(path)?,
            p.as_ref().map(|p| p.as_str()),
        )
        .map_err(|e| e.to_string())?;
        wrap_volume(volume)
    })
}
#[no_mangle]
pub unsafe extern "C" fn od_v3_restore_snapshot_resume(
    target: *mut c_void,
    source: *mut c_void,
) -> i32 {
    boundary(-1, || {
        handle(target)?
            .volume
            .restore_snapshot_resume_v3(handle(source)?.volume.clone())
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::{CStr, CString};
    fn c(s: &str) -> CString {
        CString::new(s).unwrap()
    }
    #[test]
    fn v3_abi_uses_ordered_io_and_checks_output_before_mutation() {
        unsafe {
            let dir = tempfile::tempdir().unwrap();
            let path = c(dir.path().join("api.odv3").to_str().unwrap());
            assert_eq!(od_v3_create(path.as_ptr(), 64 << 20, ptr::null()), 0);
            let h = od_v3_open(path.as_ptr(), ptr::null());
            assert!(
                !h.is_null(),
                "{}",
                CStr::from_ptr(od_v3_last_error()).to_string_lossy()
            );
            let bytes = [38u8; 4096];
            assert_eq!(od_v2_write(h, 0, bytes.as_ptr(), bytes.len() as u32), 0);
            let mut out = vec![0i8; CONTROL as usize];
            let create = c(r#"{"cmd":"snapshot.create","name":"ABI snapshot"}"#);
            assert_eq!(od_v3_control(h, create.as_ptr(), out.as_mut_ptr(), 1), -1);
            let list = c(r#"{"cmd":"snapshot.list"}"#);
            assert_eq!(
                od_v3_control(h, list.as_ptr(), out.as_mut_ptr(), CONTROL),
                0
            );
            let parsed: serde_json::Value =
                serde_json::from_str(CStr::from_ptr(out.as_ptr()).to_str().unwrap()).unwrap();
            assert!(parsed["items"].as_array().unwrap().is_empty());
            assert_eq!(
                od_v3_control(h, create.as_ptr(), out.as_mut_ptr(), CONTROL),
                0
            );
            let mut read = [0u8; 4096];
            assert_eq!(od_v2_read(h, 0, read.as_mut_ptr(), read.len() as u32), 0);
            assert_eq!(read, bytes);
            assert_eq!(od_v2_compact(h), -1);
            assert_eq!(od_v2_flush(h), 0);
            od_v2_close(h);
            assert_eq!(od_v3_inspect(path.as_ptr(), out.as_mut_ptr(), CONTROL), 0);
            let info: serde_json::Value =
                serde_json::from_str(CStr::from_ptr(out.as_ptr()).to_str().unwrap()).unwrap();
            assert_eq!(info["format_version"], 3);
        }
    }
}
