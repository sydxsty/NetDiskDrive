//! FFI ownership: every handle is owned by its caller. Concurrent operations are
//! supported; closing a handle concurrently with any operation is forbidden.
use crate::Volume;
use std::{
    cell::RefCell,
    ffi::{c_char, c_void, CStr, CString},
    panic::{catch_unwind, AssertUnwindSafe},
    ptr, slice,
};
use zeroize::Zeroizing;

thread_local! { static LAST_ERROR: RefCell<CString> = RefCell::new(CString::new("").unwrap()); }

fn set_error(message: impl ToString) {
    let message = message.to_string().replace('\0', " ");
    LAST_ERROR.with(|slot| *slot.borrow_mut() = CString::new(message).expect("NUL removed"));
}

fn boundary<T>(fallback: T, operation: impl FnOnce() -> std::result::Result<T, String>) -> T {
    set_error("");
    match catch_unwind(AssertUnwindSafe(operation)) {
        Ok(Ok(value)) => value,
        Ok(Err(error)) => {
            set_error(error);
            fallback
        }
        Err(_) => {
            set_error("internal panic; close and reopen the volume");
            fallback
        }
    }
}

unsafe fn text(pointer: *const c_char) -> std::result::Result<String, String> {
    if pointer.is_null() {
        return Err("required UTF-8 string is null".into());
    }
    CStr::from_ptr(pointer)
        .to_str()
        .map(str::to_owned)
        .map_err(|_| "string is not valid UTF-8".into())
}

unsafe fn password(
    pointer: *const c_char,
) -> std::result::Result<Option<Zeroizing<String>>, String> {
    if pointer.is_null() {
        Ok(None)
    } else {
        Ok(Some(Zeroizing::new(text(pointer)?)))
    }
}

unsafe fn volume<'a>(handle: *mut c_void) -> std::result::Result<&'a Volume, String> {
    (handle as *const Volume)
        .as_ref()
        .ok_or_else(|| "volume handle is null".into())
}

#[no_mangle]
pub unsafe extern "C" fn od_create(
    path_utf8: *const c_char,
    capacity_bytes: u64,
    password_utf8_or_null: *const c_char,
) -> i32 {
    boundary(-1, || {
        let path = text(path_utf8)?;
        let password = password(password_utf8_or_null)?;
        Volume::create(path, capacity_bytes, password.as_ref().map(|p| p.as_str()))
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_open(
    path_utf8: *const c_char,
    password_utf8_or_null: *const c_char,
) -> *mut c_void {
    boundary(ptr::null_mut(), || {
        let path = text(path_utf8)?;
        let password = password(password_utf8_or_null)?;
        let volume =
            Volume::open(path, password.as_ref().map(|p| p.as_str())).map_err(|e| e.to_string())?;
        Ok(Box::into_raw(Box::new(volume)).cast())
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_read(
    handle: *mut c_void,
    byte_offset: u64,
    buffer: *mut u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if buffer.is_null() && length != 0 {
            return Err("read buffer is null".into());
        }
        let output = if length == 0 {
            &mut []
        } else {
            slice::from_raw_parts_mut(buffer, length as usize)
        };
        match volume(handle)?.read(byte_offset, output) {
            Ok(()) => Ok(0),
            Err(error) => {
                output.fill(0);
                Err(error.to_string())
            }
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_write(
    handle: *mut c_void,
    byte_offset: u64,
    buffer: *const u8,
    length: u32,
) -> i32 {
    boundary(-1, || {
        if buffer.is_null() && length != 0 {
            return Err("write buffer is null".into());
        }
        let input = if length == 0 {
            &[]
        } else {
            slice::from_raw_parts(buffer, length as usize)
        };
        volume(handle)?
            .write(byte_offset, input)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_flush(handle: *mut c_void) -> i32 {
    boundary(-1, || {
        volume(handle)?.flush().map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_trim(handle: *mut c_void, byte_offset: u64, length: u64) -> i32 {
    boundary(-1, || {
        volume(handle)?
            .trim(byte_offset, length)
            .map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_compact(handle: *mut c_void) -> i32 {
    boundary(-1, || {
        volume(handle)?.compact().map_err(|e| e.to_string())?;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_capacity(handle: *mut c_void) -> u64 {
    boundary(0, || Ok(volume(handle)?.capacity()))
}

#[no_mangle]
pub unsafe extern "C" fn od_info(
    handle: *mut c_void,
    utf8_json: *mut c_char,
    buffer_len: u32,
) -> i32 {
    boundary(-1, || {
        if utf8_json.is_null() || buffer_len == 0 {
            return Err("info buffer is null or empty; reserve at least 1024 bytes".into());
        }
        *utf8_json = 0;
        let info = volume(handle)?.info().map_err(|e| e.to_string())?;
        let json = serde_json::to_vec(&info).map_err(|e| e.to_string())?;
        if json.len() + 1 > buffer_len as usize {
            return Err(format!(
                "info buffer too small; {} bytes required including NUL",
                json.len() + 1
            ));
        }
        ptr::copy_nonoverlapping(json.as_ptr(), utf8_json.cast(), json.len());
        *utf8_json.add(json.len()) = 0;
        Ok(0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn od_close(handle: *mut c_void) {
    boundary((), || {
        if !handle.is_null() {
            drop(Box::from_raw(handle as *mut Volume));
        }
        Ok(())
    });
}

#[no_mangle]
pub extern "C" fn od_last_error() -> *const c_char {
    LAST_ERROR.with(|slot| slot.borrow().as_ptr())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn null_inputs_and_short_info_are_errors() {
        unsafe {
            assert_eq!(od_create(ptr::null(), 64 << 20, ptr::null()), -1);
            assert!(!CStr::from_ptr(od_last_error()).to_bytes().is_empty());
            assert_eq!(od_read(ptr::null_mut(), 0, ptr::null_mut(), 0), -1);
            assert_eq!(od_capacity(ptr::null_mut()), 0);
            let dir = tempfile::tempdir().unwrap();
            let path = CString::new(dir.path().join("v").to_str().unwrap()).unwrap();
            assert_eq!(od_create(path.as_ptr(), 64 << 20, ptr::null()), 0);
            let handle = od_open(path.as_ptr(), ptr::null());
            assert!(!handle.is_null());
            let mut short = [7i8; 2];
            assert_eq!(od_info(handle, short.as_mut_ptr(), 2), -1);
            assert_eq!(short[0], 0);
            let mut info = [0i8; 1024];
            assert_eq!(od_info(handle, info.as_mut_ptr(), 1024), 0);
            let value: serde_json::Value =
                serde_json::from_slice(CStr::from_ptr(info.as_ptr()).to_bytes()).unwrap();
            assert_eq!(value["capacity_bytes"], 64 << 20);
            od_close(handle);
            od_close(ptr::null_mut());
        }
    }

    #[test]
    fn panic_is_contained() {
        assert_eq!(
            boundary(-1, || -> std::result::Result<i32, String> {
                panic!("test panic")
            }),
            -1
        );
        unsafe {
            assert!(CStr::from_ptr(od_last_error())
                .to_str()
                .unwrap()
                .contains("panic"));
        }
    }
}
