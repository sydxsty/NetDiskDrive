//! One resident handle. Every physical I/O uses explicit, aligned offsets.
use super::{Error, Result};
use fs2::FileExt;
use std::{cell::RefCell, rc::Rc};
use std::{
    alloc::{alloc_zeroed, dealloc, Layout},
    fs::{File, OpenOptions},
    path::Path,
    ptr::NonNull,
    sync::atomic::{AtomicU64, Ordering},
};

#[derive(Clone, Copy, Default, Debug, serde::Serialize)]
pub(super) struct ScopedIoCounts {
    pub local_read_bytes: u64,
    pub local_write_bytes: u64,
    pub read_calls: u64,
    pub write_calls: u64,
    pub flush_count: u64,
}
impl ScopedIoCounts {
    pub fn add(&mut self, other: Self) {
        self.local_read_bytes += other.local_read_bytes;
        self.local_write_bytes += other.local_write_bytes;
        self.read_calls += other.read_calls;
        self.write_calls += other.write_calls;
        self.flush_count += other.flush_count;
    }
}
type ScopedCounter = (usize, Rc<RefCell<ScopedIoCounts>>);
thread_local! {
    static SCOPED_IO: RefCell<Vec<ScopedCounter>> = const { RefCell::new(Vec::new()) };
}
/// Synchronous native preparation stays on the calling thread. Attribution is
/// scoped to that thread AND device, so parallel guest I/O is never included.
pub(super) struct IoScope {
    counts: Rc<RefCell<ScopedIoCounts>>,
}
impl IoScope {
    pub fn snapshot(&self) -> ScopedIoCounts { *self.counts.borrow() }
}
impl Drop for IoScope {
    fn drop(&mut self) {
        SCOPED_IO.with(|scopes| scopes.borrow_mut().retain(|(_, value)| !Rc::ptr_eq(value, &self.counts)));
    }
}

pub struct Device {
    file: File,
    read_bytes: AtomicU64,
    write_bytes: AtomicU64,
    read_calls: AtomicU64,
    write_calls: AtomicU64,
    sync_calls: AtomicU64,
    deallocated_bytes: AtomicU64,
    protected_prefix: AtomicU64,
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
    pub(super) fn io_scope(&self) -> IoScope {
        let counts = Rc::new(RefCell::new(ScopedIoCounts::default()));
        SCOPED_IO.with(|scopes| scopes.borrow_mut().push((self as *const Self as usize, counts.clone())));
        IoScope { counts }
    }
    fn record_scoped(&self, delta: ScopedIoCounts) {
        let id = self as *const Self as usize;
        SCOPED_IO.with(|scopes| {
            for (device, counts) in scopes.borrow().iter() {
                if *device == id { counts.borrow_mut().add(delta); }
            }
        });
    }
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
            read_bytes: AtomicU64::new(0),
            write_bytes: AtomicU64::new(0),
            read_calls: AtomicU64::new(0),
            write_calls: AtomicU64::new(0),
            sync_calls: AtomicU64::new(0),
            deallocated_bytes: AtomicU64::new(0),
            protected_prefix: AtomicU64::new(super::OBJECT),
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
    pub fn diagnostics(&self) -> std::collections::BTreeMap<String, u64> {
        std::collections::BTreeMap::from([
            (
                "physical_read_bytes".into(),
                self.read_bytes.load(Ordering::Relaxed),
            ),
            (
                "physical_write_bytes".into(),
                self.write_bytes.load(Ordering::Relaxed),
            ),
            (
                "physical_read_calls".into(),
                self.read_calls.load(Ordering::Relaxed),
            ),
            (
                "physical_write_calls".into(),
                self.write_calls.load(Ordering::Relaxed),
            ),
            (
                "physical_sync_calls".into(),
                self.sync_calls.load(Ordering::Relaxed),
            ),
            (
                "deallocated_bytes".into(),
                self.deallocated_bytes.load(Ordering::Relaxed),
            ),
        ])
    }
    pub fn allocated_bytes(&self) -> Result<u64> {
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            Ok(self.file.metadata()?.blocks().saturating_mul(512))
        }
        #[cfg(windows)]
        {
            windows::allocated_bytes(&self.file)
        }
    }
    pub(super) fn set_control_region(&self, object_size: u64) -> Result<()> {
        super::Geometry::new(object_size)?;
        self.protected_prefix.store(object_size, Ordering::Release);
        Ok(())
    }
    /// The caller must durably retire the range and exclude every older reader before calling.
    /// This method never changes the logical file length and never touches the control extent.
    pub fn deallocate(&self, offset: u64, length: u64) -> Result<u64> {
        let end = offset
            .checked_add(length)
            .filter(|end| *end <= i64::MAX as u64)
            .ok_or_else(|| Error::Invalid("deallocation range overflow".into()))?;
        if offset < self.protected_prefix.load(Ordering::Acquire)
            || !offset.is_multiple_of(4096)
            || !length.is_multiple_of(4096)
            || end > self.len()?
        {
            return Err(Error::Invalid(
                "deallocation must cover retired aligned container data".into(),
            ));
        }
        if length == 0 {
            return Ok(0);
        }
        let before = self.allocated_bytes()?;
        #[cfg(target_os = "linux")]
        {
            use std::os::fd::AsRawFd;
            unsafe extern "C" {
                fn fallocate(fd: i32, mode: i32, offset: i64, len: i64) -> i32;
            }
            // KEEP_SIZE | PUNCH_HOLE; unsupported filesystems fail without pretending to reclaim space.
            if unsafe { fallocate(self.file.as_raw_fd(), 0x03, offset as i64, length as i64) } != 0
            {
                return Err(std::io::Error::last_os_error().into());
            }
        }
        #[cfg(windows)]
        windows::zero_range(&self.file, offset, end)?;
        #[cfg(not(any(target_os = "linux", windows)))]
        return Err(std::io::Error::new(
            std::io::ErrorKind::Unsupported,
            "sparse deallocation is unavailable",
        )
        .into());
        self.sync()?;
        let freed = before.saturating_sub(self.allocated_bytes()?);
        self.deallocated_bytes.fetch_add(freed, Ordering::Relaxed);
        Ok(freed)
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
        self.sync_calls.fetch_add(1, Ordering::Relaxed);
        self.record_scoped(ScopedIoCounts { flush_count: 1, ..Default::default() });
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
        self.read_bytes.fetch_add(end - start, Ordering::Relaxed);
        self.read_calls.fetch_add(1, Ordering::Relaxed);
        self.record_scoped(ScopedIoCounts { local_read_bytes: end - start, read_calls: 1, ..Default::default() });
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
        self.raw_write(offset, &buffer.bytes()[..input.len()])?;
        self.write_bytes
            .fetch_add(input.len() as u64, Ordering::Relaxed);
        self.write_calls.fetch_add(1, Ordering::Relaxed);
        self.record_scoped(ScopedIoCounts { local_write_bytes: input.len() as u64, write_calls: 1, ..Default::default() });
        Ok(())
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
        fn GetFileInformationByHandleEx(
            handle: *mut c_void,
            class: i32,
            info: *mut c_void,
            length: u32,
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
    pub fn allocated_bytes(file: &File) -> Result<u64> {
        #[repr(C)]
        struct StandardInfo {
            allocated: i64,
            end: i64,
            links: u32,
            deleting: u8,
            directory: u8,
            padding: u16,
        }
        let mut info = StandardInfo {
            allocated: 0,
            end: 0,
            links: 0,
            deleting: 0,
            directory: 0,
            padding: 0,
        };
        if unsafe {
            GetFileInformationByHandleEx(
                file.as_raw_handle(),
                1,
                (&mut info as *mut StandardInfo).cast(),
                std::mem::size_of::<StandardInfo>() as u32,
            )
        } == 0
        {
            return Err(std::io::Error::last_os_error().into());
        }
        u64::try_from(info.allocated)
            .map_err(|_| Error::Invalid("negative file allocation size".into()))
    }
    pub fn zero_range(file: &File, start: u64, end: u64) -> Result<()> {
        #[repr(C)]
        struct ZeroData {
            start: i64,
            end: i64,
        }
        let input = ZeroData {
            start: start as i64,
            end: end as i64,
        };
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
                0x0009_80c8,
                (&input as *const ZeroData).cast(),
                16,
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

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn sparse_reclaim_preserves_neighbors_and_counts_aligned_io() {
        let folder = tempfile::tempdir().unwrap();
        let device = Device::open(&folder.path().join("disk.odv4"), true).unwrap();
        device.resize(3 * super::super::OBJECT).unwrap();
        device.write(0, &[0x11; 4096]).unwrap();
        device
            .write(
                super::super::OBJECT,
                &vec![0x22; super::super::OBJECT as usize],
            )
            .unwrap();
        device
            .write(2 * super::super::OBJECT, &[0x33; 4096])
            .unwrap();
        device.sync().unwrap();
        let length = device.len().unwrap();
        let before = device.allocated_bytes().unwrap();
        let freed = device
            .deallocate(super::super::OBJECT, super::super::OBJECT)
            .unwrap();
        assert_eq!(device.len().unwrap(), length);
        assert!(freed > 0);
        assert!(device.allocated_bytes().unwrap() < before);
        let mut data = [0; 4096];
        device.read(0, &mut data).unwrap();
        assert_eq!(data, [0x11; 4096]);
        device.read(super::super::OBJECT, &mut data).unwrap();
        assert_eq!(data, [0; 4096]);
        device.read(2 * super::super::OBJECT, &mut data).unwrap();
        assert_eq!(data, [0x33; 4096]);
        let before = device.diagnostics();
        let mut short = [0; 512];
        device
            .read(2 * super::super::OBJECT + 512, &mut short)
            .unwrap();
        let after = device.diagnostics();
        assert_eq!(
            after["physical_read_bytes"] - before["physical_read_bytes"],
            4096
        );
        assert_eq!(
            after["physical_read_calls"] - before["physical_read_calls"],
            1
        );
        assert!(device.deallocate(0, 4096).is_err());
        assert!(device.deallocate(length, 4096).is_err());
        assert!(device.deallocate(super::super::OBJECT + 1, 4096).is_err());
    }
}
