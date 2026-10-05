use crate::async_io::*;
use std::{
    collections::HashMap,
    sync::{
        atomic::{AtomicUsize, Ordering},
        mpsc, Arc, Condvar, Mutex,
    },
    thread,
    time::Duration,
};
use zeroize::Zeroizing;

#[derive(Default)]
struct Gate {
    open: Mutex<bool>,
    wake: Condvar,
}
impl Gate {
    fn wait(&self) {
        let mut open = self.open.lock().unwrap();
        while !*open {
            open = self.wake.wait(open).unwrap();
        }
    }
    fn release(&self) {
        *self.open.lock().unwrap() = true;
        self.wake.notify_all();
    }
}
struct Fake {
    cache: Mutex<Vec<u8>>,
    durable: Mutex<Vec<u8>>,
    started: mpsc::Sender<(&'static str, u64)>,
    gates: HashMap<(&'static str, u64), Arc<Gate>>,
    active: AtomicUsize,
    max_active: AtomicUsize,
}
impl Fake {
    fn new(
        gates: HashMap<(&'static str, u64), Arc<Gate>>,
    ) -> (Arc<Self>, mpsc::Receiver<(&'static str, u64)>) {
        let (started, receiver) = mpsc::channel();
        (
            Arc::new(Self {
                cache: Mutex::new(vec![0; 65536]),
                durable: Mutex::new(vec![0; 65536]),
                started,
                gates,
                active: AtomicUsize::new(0),
                max_active: AtomicUsize::new(0),
            }),
            receiver,
        )
    }
    fn persist(&self) {
        *self.durable.lock().unwrap() = self.cache.lock().unwrap().clone();
    }
}
impl Backend for Fake {
    fn execute(&self, operation: &Operation) -> IoResult<Output> {
        let key = match operation {
            Operation::Read { offset, .. } => ("read", *offset),
            Operation::Write { offset, .. } => ("write", *offset),
            Operation::Trim { offset, .. } => ("trim", *offset),
            Operation::Flush => ("flush", 0),
            _ => ("other", 0),
        };
        let active = self.active.fetch_add(1, Ordering::SeqCst) + 1;
        self.max_active.fetch_max(active, Ordering::SeqCst);
        struct Count<'a>(&'a AtomicUsize);
        impl Drop for Count<'_> {
            fn drop(&mut self) {
                self.0.fetch_sub(1, Ordering::SeqCst);
            }
        }
        let _count = Count(&self.active);
        let _ = self.started.send(key);
        if let Some(gate) = self.gates.get(&key) {
            gate.wait();
        }
        if key.1 == 32768 {
            return Err("injected I/O failure\0details".into());
        }
        if key.1 == 36864 {
            panic!("injected worker panic");
        }
        match operation {
            Operation::Read {
                offset,
                length,
                fua,
            } => {
                if *fua {
                    self.persist();
                }
                let source = if *fua { &self.durable } else { &self.cache };
                let source = source.lock().unwrap();
                let end = *offset as usize + *length;
                if end > source.len() {
                    return Err("out of bounds".into());
                }
                let mut data = Zeroizing::new(vec![0; (*length).max(READ_ALLOCATION)]);
                data[..*length].copy_from_slice(&source[*offset as usize..end]);
                Ok(Output::Data(data, *length))
            }
            Operation::Write { offset, data, fua } => {
                {
                    let mut cache = self.cache.lock().unwrap();
                    cache[*offset as usize..*offset as usize + data.len()].copy_from_slice(data);
                }
                if *fua {
                    self.persist();
                }
                Ok(Output::Unit)
            }
            Operation::Trim {
                offset,
                length,
                fua,
            } => {
                {
                    let mut cache = self.cache.lock().unwrap();
                    cache[*offset as usize..(*offset + *length) as usize].fill(0);
                }
                if *fua {
                    self.persist();
                }
                Ok(Output::Unit)
            }
            Operation::Flush => {
                self.persist();
                Ok(Output::Unit)
            }
            _ => Ok(Output::Unit),
        }
    }
}
fn event(events: &mpsc::Receiver<(&'static str, u64)>) -> (&'static str, u64) {
    events
        .recv_timeout(Duration::from_secs(3))
        .expect("worker did not start")
}
fn completion(queue: &Queue) -> Arc<Completion> {
    queue
        .next_completion(Duration::from_secs(3))
        .unwrap()
        .expect("missing completion")
}
fn release(queue: &Queue, c: Arc<Completion>) -> (u64, i32) {
    let result = (c.token, c.status);
    drop(c);
    queue.release_completion(result.0).unwrap();
    result
}

#[test]
fn independent_ranges_execute_while_overlapping_read_waits() {
    let gate = Arc::new(Gate::default());
    let (backend, events) = Fake::new(HashMap::from([(("write", 0), gate.clone())]));
    let queue = Queue::new(backend.clone()).unwrap();
    queue.submit_write(1, 0, &[7; 512], false).unwrap();
    assert_eq!(event(&events), ("write", 0));
    queue.submit_write(2, 4096, &[8; 512], false).unwrap();
    assert_eq!(event(&events), ("write", 4096));
    assert_eq!(release(&queue, completion(&queue)), (2, 0));
    queue
        .submit(
            3,
            Operation::Read {
                offset: 0,
                length: 512,
                fua: false,
            },
        )
        .unwrap();
    queue
        .submit(
            4,
            Operation::Read {
                offset: 8192,
                length: 512,
                fua: false,
            },
        )
        .unwrap();
    assert_eq!(event(&events), ("read", 8192));
    assert_eq!(release(&queue, completion(&queue)), (4, 0));
    assert!(events.recv_timeout(Duration::from_millis(75)).is_err());
    gate.release();
    assert_eq!(release(&queue, completion(&queue)), (1, 0));
    assert_eq!(event(&events), ("read", 0));
    let read = completion(&queue);
    assert_eq!(read.token, 3);
    assert_eq!(&read.data.as_ref().unwrap()[..512], &[7; 512]);
    release(&queue, read);
    queue.drain().unwrap();
    assert!(backend.max_active.load(Ordering::SeqCst) >= 2);
}

#[test]
fn earlier_pending_same_page_write_cannot_be_overtaken() {
    let gate = Arc::new(Gate::default());
    let (backend, events) = Fake::new(HashMap::from([(("write", 0), gate.clone())]));
    let queue = Queue::new(backend.clone()).unwrap();
    queue.submit_write(1, 0, &[1; 1024], false).unwrap();
    assert_eq!(event(&events), ("write", 0));
    queue.submit_write(2, 0, &[2; 1024], false).unwrap();
    queue.submit_write(3, 512, &[3; 512], false).unwrap();
    assert!(events.recv_timeout(Duration::from_millis(75)).is_err());
    gate.release();
    queue.drain().unwrap();
    assert_eq!(event(&events), ("write", 0));
    assert_eq!(event(&events), ("write", 512));
    let data = backend.cache.lock().unwrap();
    assert_eq!(&data[..512], &[2; 512]);
    assert_eq!(&data[512..1024], &[3; 512]);
    for token in 1..=3 {
        assert_eq!(release(&queue, completion(&queue)), (token, 0));
    }
}

#[test]
fn flush_is_global_prefix_barrier_and_later_writes_cannot_pass() {
    let write_gate = Arc::new(Gate::default());
    let flush_gate = Arc::new(Gate::default());
    let (backend, events) = Fake::new(HashMap::from([
        (("write", 0), write_gate.clone()),
        (("flush", 0), flush_gate.clone()),
    ]));
    let queue = Queue::new(backend.clone()).unwrap();
    queue.submit_write(1, 0, &[1; 512], false).unwrap();
    assert_eq!(event(&events), ("write", 0));
    queue.submit_write(2, 4096, &[2; 512], false).unwrap();
    assert_eq!(event(&events), ("write", 4096));
    queue.submit(3, Operation::Flush).unwrap();
    queue.submit_write(4, 8192, &[4; 512], false).unwrap();
    assert!(events.recv_timeout(Duration::from_millis(75)).is_err());
    write_gate.release();
    assert_eq!(event(&events), ("flush", 0));
    assert!(events.recv_timeout(Duration::from_millis(75)).is_err());
    flush_gate.release();
    assert_eq!(event(&events), ("write", 8192));
    queue.drain().unwrap();
    let durable = backend.durable.lock().unwrap();
    assert_eq!(&durable[..512], &[1; 512]);
    assert_eq!(&durable[4096..4608], &[2; 512]);
    assert_eq!(&durable[8192..8704], &[0; 512]);
    drop(durable);
    for _ in 0..4 {
        release(&queue, completion(&queue));
    }
}

#[test]
fn fua_write_persists_preceding_trim_and_fua_read_flushes_latest_cache() {
    let (backend, _) = Fake::new(HashMap::new());
    let queue = Queue::new(backend.clone()).unwrap();
    queue.write(0, &[9; 4096], false).unwrap();
    assert!(backend.durable.lock().unwrap()[..4096]
        .iter()
        .all(|b| *b == 0));
    queue
        .submit(
            1,
            Operation::Trim {
                offset: 0,
                length: 512,
                fua: false,
            },
        )
        .unwrap();
    queue.submit_write(2, 4096, &[5; 512], true).unwrap();
    queue.drain().unwrap();
    for token in 1..=2 {
        assert_eq!(release(&queue, completion(&queue)), (token, 0));
    }
    {
        let durable = backend.durable.lock().unwrap();
        assert_eq!(&durable[..512], &[0; 512]);
        assert_eq!(&durable[512..4096], &[9; 3584]);
        assert_eq!(&durable[4096..4608], &[5; 512]);
    }
    queue.write(8192, &[6; 512], false).unwrap();
    queue
        .submit(
            3,
            Operation::Read {
                offset: 8192,
                length: 512,
                fua: true,
            },
        )
        .unwrap();
    let c = completion(&queue);
    assert_eq!(&c.data.as_ref().unwrap()[..512], &[6; 512]);
    assert_eq!(&backend.durable.lock().unwrap()[8192..8704], &[6; 512]);
    release(&queue, c);
}

#[test]
fn synchronous_operations_use_the_same_admission_order() {
    let gate = Arc::new(Gate::default());
    let (backend, events) = Fake::new(HashMap::from([(("write", 0), gate.clone())]));
    let queue = Arc::new(Queue::new(backend).unwrap());
    queue.submit_write(1, 0, &[4; 512], false).unwrap();
    assert_eq!(event(&events), ("write", 0));
    let (sender, receiver) = mpsc::channel();
    let q = queue.clone();
    let caller = thread::spawn(move || {
        sender
            .send(q.call(Operation::Read {
                offset: 0,
                length: 512,
                fua: false,
            }))
            .unwrap()
    });
    assert!(receiver.recv_timeout(Duration::from_millis(75)).is_err());
    gate.release();
    let Output::Data(data, length) = receiver
        .recv_timeout(Duration::from_secs(3))
        .unwrap()
        .unwrap()
    else {
        panic!("read expected")
    };
    assert_eq!(length, 512);
    assert_eq!(&data[..512], &[4; 512]);
    caller.join().unwrap();
    release(&queue, completion(&queue));
}

#[test]
fn write_buffer_is_owned_and_short_read_allocation_lives_until_release() {
    let gate = Arc::new(Gate::default());
    let (backend, events) = Fake::new(HashMap::from([(("write", 0), gate.clone())]));
    let queue = Queue::new(backend).unwrap();
    let mut source = vec![7; 512];
    queue.submit_write(1, 0, &source, false).unwrap();
    source.fill(99);
    assert_eq!(event(&events), ("write", 0));
    gate.release();
    release(&queue, completion(&queue));
    queue
        .submit(
            2,
            Operation::Read {
                offset: 0,
                length: 512,
                fua: false,
            },
        )
        .unwrap();
    let c = completion(&queue);
    let data = c.data.as_ref().unwrap();
    assert!(data.len() >= READ_ALLOCATION);
    assert_eq!(&data[..512], &[7; 512]);
    assert!(data[512..].iter().all(|b| *b == 0));
    let address = data.as_ptr();
    for token in 3..20 {
        queue.submit_write(token, 4096, &[8; 512], false).unwrap();
    }
    queue.drain().unwrap();
    for _ in 3..20 {
        release(&queue, completion(&queue));
    }
    assert_eq!(address, c.data.as_ref().unwrap().as_ptr());
    assert_eq!(&c.data.as_ref().unwrap()[..512], &[7; 512]);
    release(&queue, c);
}

#[test]
fn completion_tokens_deliver_once_and_reject_premature_or_duplicate_release() {
    let gate = Arc::new(Gate::default());
    let (backend, events) = Fake::new(HashMap::from([(("write", 0), gate.clone())]));
    let queue = Queue::new(backend).unwrap();
    queue.submit_write(42, 0, &[1; 512], false).unwrap();
    assert_eq!(event(&events), ("write", 0));
    assert!(queue.submit_write(42, 4096, &[2; 512], false).is_err());
    assert!(queue.release_completion(42).is_err());
    gate.release();
    let c = completion(&queue);
    assert!(queue.next_completion(Duration::ZERO).unwrap().is_none());
    assert!(queue.submit_write(42, 4096, &[2; 512], false).is_err());
    release(&queue, c);
    assert!(queue.release_completion(42).is_err());
    queue.submit_write(42, 4096, &[2; 512], false).unwrap();
    assert_eq!(release(&queue, completion(&queue)), (42, 0));
}

#[test]
fn count_backpressure_does_not_block_drain_on_unreleased_completions() {
    let (backend, _) = Fake::new(HashMap::new());
    let queue = Arc::new(Queue::new(backend).unwrap());
    for token in 0..64 {
        queue.submit_write(token, 0, &[1; 512], false).unwrap();
    }
    queue.drain().unwrap();
    assert_eq!(queue.usage().0, MAX_REQUESTS);
    let (sender, receiver) = mpsc::channel();
    let q = queue.clone();
    let caller = thread::spawn(move || {
        sender
            .send(q.submit_write(64, 0, &[2; 512], false))
            .unwrap()
    });
    assert!(receiver.recv_timeout(Duration::from_millis(75)).is_err());
    queue.drain().unwrap();
    release(&queue, completion(&queue));
    receiver
        .recv_timeout(Duration::from_secs(3))
        .unwrap()
        .unwrap();
    caller.join().unwrap();
    for _ in 0..64 {
        release(&queue, completion(&queue));
    }
    assert_eq!(queue.usage(), (0, 0));
}

#[test]
fn transfer_byte_budget_is_enforced_independently_of_request_count() {
    let (backend, _) = Fake::new(HashMap::new());
    let queue = Arc::new(Queue::new(backend).unwrap());
    queue
        .submit(
            1,
            Operation::Read {
                offset: 0,
                length: MAX_BYTES,
                fua: false,
            },
        )
        .unwrap();
    queue.drain().unwrap();
    assert_eq!(queue.usage(), (1, MAX_BYTES));
    let (sender, receiver) = mpsc::channel();
    let q = queue.clone();
    let caller =
        thread::spawn(move || sender.send(q.submit_write(2, 0, &[2; 512], false)).unwrap());
    assert!(receiver.recv_timeout(Duration::from_millis(75)).is_err());
    assert_eq!(release(&queue, completion(&queue)), (1, -1));
    receiver
        .recv_timeout(Duration::from_secs(3))
        .unwrap()
        .unwrap();
    caller.join().unwrap();
    release(&queue, completion(&queue));
    assert_eq!(queue.usage(), (0, 0));
}

#[test]
fn cancellation_wakes_async_and_sync_io_but_reserved_flush_still_persists() {
    let (backend, _) = Fake::new(HashMap::new());
    let queue = Arc::new(Queue::new(backend.clone()).unwrap());
    for token in 0..64 {
        queue.submit_write(token, 0, &[1; 512], false).unwrap();
    }
    queue.drain().unwrap();
    let (sender, receiver) = mpsc::channel();
    let q = queue.clone();
    let caller = thread::spawn(move || {
        sender
            .send(q.submit_write(64, 0, &[2; 512], false))
            .unwrap()
    });
    let (trim_sender, trim_receiver) = mpsc::channel();
    let q = queue.clone();
    let trim_caller = thread::spawn(move || {
        trim_sender
            .send(q.call(Operation::Trim {
                offset: 0,
                length: 512,
                fua: false,
            }))
            .unwrap();
    });
    assert!(receiver.recv_timeout(Duration::from_millis(75)).is_err());
    assert!(trim_receiver
        .recv_timeout(Duration::from_millis(75))
        .is_err());
    queue.cancel_submissions().unwrap();
    assert!(receiver
        .recv_timeout(Duration::from_secs(3))
        .unwrap()
        .is_err());
    caller.join().unwrap();
    assert!(trim_receiver
        .recv_timeout(Duration::from_secs(3))
        .unwrap()
        .is_err());
    trim_caller.join().unwrap();
    queue.drain().unwrap();
    // No completion has been released: the control slot must still be usable.
    assert_eq!(queue.usage().0, MAX_REQUESTS);
    queue.call(Operation::Flush).unwrap();
    assert_eq!(&backend.durable.lock().unwrap()[..512], &[1; 512]);
    assert_eq!(queue.usage().0, MAX_REQUESTS);
    for _ in 0..64 {
        release(&queue, completion(&queue));
    }
    assert_eq!(queue.usage(), (0, 0));
    assert!(queue.submit_write(65, 0, &[1; 512], false).is_err());
    queue.call(Operation::Flush).unwrap();
}

#[test]
fn errors_and_panics_complete_once_with_owned_error_and_keep_workers_alive() {
    let (backend, _) = Fake::new(HashMap::new());
    let queue = Queue::new(backend).unwrap();
    queue
        .submit(
            1,
            Operation::Read {
                offset: 32768,
                length: 512,
                fua: false,
            },
        )
        .unwrap();
    queue
        .submit(
            2,
            Operation::Read {
                offset: 36864,
                length: 512,
                fua: false,
            },
        )
        .unwrap();
    for _ in 0..2 {
        let c = completion(&queue);
        assert_eq!(c.status, -1);
        assert_eq!(c.length, 0);
        assert!(c.error.as_ref().unwrap().to_bytes().len() > 5);
        assert!(c.data.as_ref().unwrap().len() >= READ_ALLOCATION);
        assert!(c.data.as_ref().unwrap().iter().all(|b| *b == 0));
        release(&queue, c);
    }
    queue.submit_write(3, 4096, &[3; 512], false).unwrap();
    assert_eq!(release(&queue, completion(&queue)), (3, 0));
    assert!(queue.next_completion(Duration::ZERO).unwrap().is_none());
}

#[test]
fn worker_count_is_bounded_and_shutdown_joins_without_requiring_release() {
    let gate = Arc::new(Gate::default());
    let gates = (0..8)
        .map(|i| (("write", i * 4096), gate.clone()))
        .collect();
    let (backend, events) = Fake::new(gates);
    let queue = Arc::new(Queue::new(backend.clone()).unwrap());
    for token in 0..8 {
        queue
            .submit_write(token, token * 4096, &[1; 512], false)
            .unwrap();
    }
    for _ in 0..4 {
        event(&events);
    }
    assert!(events.recv_timeout(Duration::from_millis(75)).is_err());
    let (sender, receiver) = mpsc::channel();
    let q = queue.clone();
    let closer = thread::spawn(move || {
        q.shutdown();
        sender.send(()).unwrap();
    });
    assert!(receiver.recv_timeout(Duration::from_millis(75)).is_err());
    assert!(queue.submit_write(99, 49152, &[1; 512], false).is_err());
    gate.release();
    receiver.recv_timeout(Duration::from_secs(3)).unwrap();
    closer.join().unwrap();
    assert_eq!(backend.max_active.load(Ordering::SeqCst), 4);
    queue.drain().unwrap();
    for _ in 0..8 {
        release(&queue, completion(&queue));
    }
    assert!(queue.next_completion(Duration::ZERO).is_err());
}
