//! One resident handle. Every physical I/O uses explicit, aligned offsets.
use super::{Error, Result};
use fs2::FileExt;
use std::{
    alloc::{alloc_zeroed, dealloc, Layout},
    fs::{File, OpenOptions},
    path::Path,
    ptr::NonNull,
};

pub struct Device {
    file: File,
    #[cfg(test)]
    pub events: std::sync::Mutex<Vec<(bool, u64, usize)>>,
    #[cfg(test)]
    pub fail_after: std::sync::atomic::AtomicI64,
    #[cfg(test)]
    pub fail_resize: std::sync::atomic::AtomicBool,
    #[cfg(test)]
    pub fail_sync: std::sync::atomic::AtomicBool,
}
impl Drop for Device {
    fn drop(&mut self) {
        // Release our lock explicitly. On Unix a concurrent process launch can
        // briefly inherit this open-file description until exec closes it;
        // relying only on close would make immediate reopen spuriously fail.
        let _ = FileExt::unlock(&self.file);
    }
}
struct Aligned {
    ptr: NonNull<u8>,
    size: usize,
}
impl Aligned {
    fn new(size: usize) -> Result<Self> {
        let layout = Layout::from_size_align(size.max(4096), 4096)
            .map_err(|_| Error::Invalid("invalid aligned buffer".into()))?;
        let ptr = NonNull::new(unsafe { alloc_zeroed(layout) })
            .ok_or_else(|| Error::Invalid("I/O allocation failed".into()))?;
        Ok(Self {
            ptr,
            size: size.max(4096),
        })
    }
    fn bytes(&self) -> &[u8] {
        unsafe { std::slice::from_raw_parts(self.ptr.as_ptr(), self.size) }
    }
    fn bytes_mut(&mut self) -> &mut [u8] {
        unsafe { std::slice::from_raw_parts_mut(self.ptr.as_ptr(), self.size) }
    }
}
impl Drop for Aligned {
    fn drop(&mut self) {
        unsafe {
            dealloc(
                self.ptr.as_ptr(),
                Layout::from_size_align(self.size, 4096).unwrap(),
            )
        }
    }
}
impl Device {
    pub fn open(path: &Path, create: bool) -> Result<Self> {
        let mut options = OpenOptions::new();
        options.read(true).write(true).create_new(create);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.custom_flags(0x2000_0000 | 0x4000_0000);
        }
        let file = options.open(path)?;
        FileExt::try_lock_exclusive(&file).map_err(|e| {
            if e.raw_os_error() == fs2::lock_contended_error().raw_os_error() {
                Error::Locked
            } else {
                Error::Io(e)
            }
        })?;
        #[cfg(windows)]
        if create {
            windows::set_sparse(&file)?;
        }
        Ok(Self {
            file,
            #[cfg(test)]
            events: std::sync::Mutex::new(Vec::new()),
            #[cfg(test)]
            fail_after: std::sync::atomic::AtomicI64::new(-1),
            #[cfg(test)]
            fail_resize: std::sync::atomic::AtomicBool::new(false),
            #[cfg(test)]
            fail_sync: std::sync::atomic::AtomicBool::new(false),
        })
    }
    pub fn len(&self) -> Result<u64> {
        Ok(self.file.metadata()?.len())
    }
    pub fn resize(&self, bytes: u64) -> Result<()> {
        #[cfg(test)]
        if self.fail_resize.load(std::sync::atomic::Ordering::Relaxed) {
            return Err(std::io::Error::other("injected resize failure").into());
        }
        self.file.set_len(bytes)?;
        Ok(())
    }
    pub fn grow(&self, minimum: u64) -> Result<()> {
        if self.len()? < minimum {
            self.resize(minimum.div_ceil(64 * 1024 * 1024) * (64 * 1024 * 1024))?;
        }
        Ok(())
    }
    pub fn sync(&self) -> Result<()> {
        #[cfg(test)]
        if self.fail_sync.load(std::sync::atomic::Ordering::Relaxed) {
            return Err(std::io::Error::other("injected sync failure").into());
        }
        self.file.sync_all()?;
        Ok(())
    }
    pub fn read(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        if output.is_empty() {
            return Ok(());
        }
        #[cfg(test)]
        self.events
            .lock()
            .unwrap()
            .push((false, offset, output.len()));
        let start = offset / 4096 * 4096;
        let end = offset
            .checked_add(output.len() as u64)
            .ok_or_else(|| Error::Invalid("I/O overflow".into()))?
            .div_ceil(4096)
            * 4096;
        let mut buffer = Aligned::new((end - start) as usize)?;
        self.raw_read(start, &mut buffer.bytes_mut()[..(end - start) as usize])?;
        let within = (offset - start) as usize;
        output.copy_from_slice(&buffer.bytes()[within..within + output.len()]);
        Ok(())
    }
    pub fn write(&self, offset: u64, input: &[u8]) -> Result<()> {
        if !offset.is_multiple_of(4096) || !input.len().is_multiple_of(4096) {
            return Err(Error::Invalid(
                "physical writes must be 4096-byte aligned".into(),
            ));
        }
        if input.is_empty() {
            return Ok(());
        }
        #[cfg(test)]
        if self.fail_after.load(std::sync::atomic::Ordering::Relaxed) >= 0
            && self
                .fail_after
                .fetch_sub(1, std::sync::atomic::Ordering::Relaxed)
                == 0
        {
            return Err(
                std::io::Error::from_raw_os_error(if cfg!(windows) { 112 } else { 28 }).into(),
            );
        }
        #[cfg(test)]
        self.events
            .lock()
            .unwrap()
            .push((true, offset, input.len()));
        let mut buffer = Aligned::new(input.len())?;
        buffer.bytes_mut()[..input.len()].copy_from_slice(input);
        self.raw_write(offset, &buffer.bytes()[..input.len()])
    }
    #[cfg(unix)]
    fn raw_read(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        use std::os::unix::fs::FileExt;
        self.file.read_exact_at(output, offset)?;
        Ok(())
    }
    #[cfg(unix)]
    fn raw_write(&self, offset: u64, input: &[u8]) -> Result<()> {
        use std::os::unix::fs::FileExt;
        self.file.write_all_at(input, offset)?;
        Ok(())
    }
    #[cfg(windows)]
    fn raw_read(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        windows::transfer(&self.file, offset, output.as_mut_ptr(), output.len(), false)
    }
    #[cfg(windows)]
    fn raw_write(&self, offset: u64, input: &[u8]) -> Result<()> {
        windows::transfer(
            &self.file,
            offset,
            input.as_ptr() as *mut u8,
            input.len(),
            true,
        )
    }
}

#[cfg(windows)]
mod windows {
    use super::*;
    use std::{ffi::c_void, os::windows::io::AsRawHandle};
    #[repr(C)]
    struct Overlapped {
        internal: usize,
        internal_high: usize,
        offset: u32,
        offset_high: u32,
        event: *mut c_void,
    }
    #[link(name = "kernel32")]
    extern "system" {
        fn CreateEventW(
            attributes: *const c_void,
            manual: i32,
            initial: i32,
            name: *const u16,
        ) -> *mut c_void;
        fn CloseHandle(handle: *mut c_void) -> i32;
        fn ReadFile(
            handle: *mut c_void,
            buffer: *mut c_void,
            count: u32,
            done: *mut u32,
            overlapped: *mut Overlapped,
        ) -> i32;
        fn WriteFile(
            handle: *mut c_void,
            buffer: *const c_void,
            count: u32,
            done: *mut u32,
            overlapped: *mut Overlapped,
        ) -> i32;
        fn GetOverlappedResult(
            handle: *mut c_void,
            overlapped: *mut Overlapped,
            done: *mut u32,
            wait: i32,
        ) -> i32;
        fn DeviceIoControl(
            handle: *mut c_void,
            code: u32,
            input: *const c_void,
            input_size: u32,
            output: *mut c_void,
            output_size: u32,
            done: *mut u32,
            overlapped: *mut Overlapped,
        ) -> i32;
    }
    struct Event(*mut c_void);
    impl Drop for Event {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
    pub fn set_sparse(file: &File) -> Result<()> {
        let event = Event(unsafe { CreateEventW(std::ptr::null(), 1, 0, std::ptr::null()) });
        if event.0.is_null() {
            return Err(std::io::Error::last_os_error().into());
        }
        let mut ov = Overlapped {
            internal: 0,
            internal_high: 0,
            offset: 0,
            offset_high: 0,
            event: event.0,
        };
        let mut done = 0;
        let result = unsafe {
            DeviceIoControl(
                file.as_raw_handle(),
                0x0009_00c4,
                std::ptr::null(),
                0,
                std::ptr::null_mut(),
                0,
                &mut done,
                &mut ov,
            )
        };
        if result == 0 && std::io::Error::last_os_error().raw_os_error() != Some(997) {
            return Err(std::io::Error::last_os_error().into());
        }
        if unsafe { GetOverlappedResult(file.as_raw_handle(), &mut ov, &mut done, 1) } == 0 {
            return Err(std::io::Error::last_os_error().into());
        }
        Ok(())
    }
    pub fn transfer(
        file: &File,
        offset: u64,
        pointer: *mut u8,
        length: usize,
        write: bool,
    ) -> Result<()> {
        if length > u32::MAX as usize {
            return Err(Error::Invalid("I/O transfer too large".into()));
        }
        let event = Event(unsafe { CreateEventW(std::ptr::null(), 1, 0, std::ptr::null()) });
        if event.0.is_null() {
            return Err(std::io::Error::last_os_error().into());
        }
        let mut ov = Overlapped {
            internal: 0,
            internal_high: 0,
            offset: offset as u32,
            offset_high: (offset >> 32) as u32,
            event: event.0,
        };
        let mut done = 0;
        let result = unsafe {
            if write {
                WriteFile(
                    file.as_raw_handle(),
                    pointer.cast(),
                    length as u32,
                    &mut done,
                    &mut ov,
                )
            } else {
                ReadFile(
                    file.as_raw_handle(),
                    pointer.cast(),
                    length as u32,
                    &mut done,
                    &mut ov,
                )
            }
        };
        if result == 0 {
            let error = std::io::Error::last_os_error();
            if error.raw_os_error() != Some(997) {
                return Err(error.into());
            }
        }
        if unsafe { GetOverlappedResult(file.as_raw_handle(), &mut ov, &mut done, 1) } == 0 {
            return Err(std::io::Error::last_os_error().into());
        }
        if done as usize != length {
            return Err(std::io::Error::new(
                std::io::ErrorKind::UnexpectedEof,
                "short aligned file I/O",
            )
            .into());
        }
        Ok(())
    }
}
