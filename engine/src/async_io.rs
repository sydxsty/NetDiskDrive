//! One admission order, bounded ownership, and dependency-aware execution.
//! Completion delivery is deliberately independent from execution/drain progress.
use std::{
    collections::{BTreeMap, HashMap, VecDeque},
    ffi::CString,
    panic::{catch_unwind, AssertUnwindSafe},
    sync::{mpsc, Arc, Condvar, Mutex},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use zeroize::Zeroizing;

pub(crate) const MAX_REQUESTS: usize = 64;
pub(crate) const MAX_BYTES: usize = 64 * 1024 * 1024;
pub(crate) const READ_ALLOCATION: usize = 1024 * 1024;
pub(crate) type IoResult<T> = Result<T, String>;

pub(crate) enum Operation {
    Read {
        offset: u64,
        length: usize,
        fua: bool,
    },
    Write {
        offset: u64,
        data: Zeroizing<Vec<u8>>,
        fua: bool,
    },
    Trim {
        offset: u64,
        length: u64,
        fua: bool,
    },
    Flush,
    SnapshotCreate,
    SnapshotRelease(String),
    Control(serde_json::Value),
}

pub(crate) enum Output {
    Unit,
    Data(Zeroizing<Vec<u8>>, usize),
    Bytes(Vec<u8>),
}

pub(crate) trait Backend: Send + Sync + 'static {
    fn execute(&self, operation: &Operation) -> IoResult<Output>;
}

#[derive(Clone, Copy)]
struct Spec {
    range: Option<(u64, u64)>,
    barrier: bool,
    charge: usize,
    read_length: Option<usize>,
    control: bool,
}

impl Spec {
    fn io(
        offset: u64,
        length: u64,
        barrier: bool,
        charge: usize,
        read_length: Option<usize>,
    ) -> IoResult<Self> {
        if charge > MAX_BYTES {
            return Err("request exceeds the 64 MiB queue buffer budget".into());
        }
        let end = offset.checked_add(length).ok_or("I/O range overflow")?;
        // Different 512-byte sectors can still share one core read-modify-write page.
        let range = if length == 0 {
            None
        } else {
            Some((offset / 4096, (end - 1) / 4096 + 1))
        };
        Ok(Self {
            range,
            barrier,
            charge,
            read_length,
            control: false,
        })
    }
    fn conflicts(self, other: Self) -> bool {
        self.barrier
            || other.barrier
            || match (self.range, other.range) {
                (Some((a, b)), Some((c, d))) => a < d && c < b,
                _ => false,
            }
    }
}

impl Operation {
    fn spec(&self) -> IoResult<Spec> {
        match self {
            Self::Read {
                offset,
                length,
                fua,
            } => Spec::io(
                *offset,
                *length as u64,
                *fua,
                (*length).max(READ_ALLOCATION),
                Some(*length),
            ),
            Self::Write { offset, data, fua } => {
                Spec::io(*offset, data.len() as u64, *fua, data.len(), None)
            }
            Self::Trim {
                offset,
                length,
                fua,
            } => Spec::io(*offset, *length, *fua, 0, None),
            Self::Flush => {
                let mut spec = Spec::io(0, 0, true, 0, None)?;
                spec.control = true;
                Ok(spec)
            }
            _ => Spec::io(0, 0, true, 0, None),
        }
    }
}

enum Reply {
    Async(u64),
    Sync(mpsc::SyncSender<IoResult<Output>>),
}
struct Job {
    sequence: u64,
    spec: Spec,
    operation: Operation,
    reply: Reply,
    reserved_control: bool,
}
struct Token {
    charge: usize,
    delivered: bool,
}

pub(crate) struct Completion {
    pub token: u64,
    pub status: i32,
    pub length: u32,
    pub data: Option<Zeroizing<Vec<u8>>>,
    pub error: Option<CString>,
}

impl Completion {
    fn new(token: u64, spec: Spec, result: IoResult<Output>) -> Self {
        let result = match result {
            Ok(Output::Data(bytes, length))
                if spec.read_length == Some(length) && length <= bytes.len() =>
            {
                Ok(Some(bytes))
            }
            Ok(Output::Unit) if spec.read_length.is_none() => Ok(None),
            Ok(_) => Err("backend returned an invalid asynchronous result".into()),
            Err(error) => Err(error),
        };
        let (status, length, mut data, error) = match result {
            Ok(bytes) => (0, spec.read_length.unwrap_or(0) as u32, bytes, None),
            Err(message) => (
                -1,
                0,
                spec.read_length
                    .map(|n| Zeroizing::new(vec![0; n.max(READ_ALLOCATION)])),
                Some(CString::new(message.replace('\0', " ")).expect("NUL removed")),
            ),
        };
        if let Some(bytes) = data.as_mut() {
            let allocated = bytes.len().max(READ_ALLOCATION);
            bytes.resize(allocated, 0);
        }
        Self {
            token,
            status,
            length,
            data,
            error,
        }
    }
}

struct State {
    accepting: bool,
    io_accepting: bool,
    control_busy: bool,
    stopping: bool,
    last_sequence: u64,
    completed_through: u64,
    completed_ranges: BTreeMap<u64, u64>,
    pending: VecDeque<Job>,
    running: BTreeMap<u64, Spec>,
    count: usize,
    bytes: usize,
    tokens: HashMap<u64, Token>,
    ready: VecDeque<u64>,
    completions: HashMap<u64, Arc<Completion>>,
}

struct Shared {
    state: Mutex<State>,
    changed: Condvar,
    backend: Arc<dyn Backend>,
}

pub(crate) struct Queue {
    shared: Arc<Shared>,
    workers: Mutex<Vec<JoinHandle<()>>>,
}

impl Queue {
    pub fn new(backend: Arc<dyn Backend>) -> IoResult<Self> {
        let shared = Arc::new(Shared {
            backend,
            changed: Condvar::new(),
            state: Mutex::new(State {
                accepting: true,
                io_accepting: true,
                control_busy: false,
                stopping: false,
                last_sequence: 0,
                completed_through: 0,
                completed_ranges: BTreeMap::new(),
                pending: VecDeque::new(),
                running: BTreeMap::new(),
                count: 0,
                bytes: 0,
                tokens: HashMap::new(),
                ready: VecDeque::new(),
                completions: HashMap::new(),
            }),
        });
        let queue = Self {
            shared,
            workers: Mutex::new(Vec::new()),
        };
        for index in 0..4 {
            let shared = queue.shared.clone();
            let worker = thread::Builder::new()
                .name(format!("od-v2-io-{index}"))
                .spawn(move || worker_loop(shared))
                .map_err(|e| format!("cannot start I/O worker: {e}"))?;
            queue.workers.lock().unwrap().push(worker);
        }
        Ok(queue)
    }

    fn enqueue(
        &self,
        spec: Spec,
        token: Option<u64>,
        build: impl FnOnce() -> Operation,
    ) -> IoResult<Option<mpsc::Receiver<IoResult<Output>>>> {
        let reserved_control = token.is_none() && spec.control;
        let mut state = self
            .shared
            .state
            .lock()
            .map_err(|_| "queue lock poisoned")?;
        loop {
            if !state.accepting {
                return Err("I/O queue is stopping".into());
            }
            if !state.io_accepting && !reserved_control {
                return Err("I/O submissions have been cancelled".into());
            }
            if token.is_some_and(|t| state.tokens.contains_key(&t)) {
                return Err("completion token is already outstanding".into());
            }
            if reserved_control && !state.control_busy
                || !reserved_control
                    && state.count < MAX_REQUESTS
                    && spec.charge <= MAX_BYTES - state.bytes
            {
                break;
            }
            state = self
                .shared
                .changed
                .wait(state)
                .map_err(|_| "queue lock poisoned")?;
        }
        let sequence = state
            .last_sequence
            .checked_add(1)
            .ok_or("I/O sequence exhausted")?;
        // For borrowed native Write buffers, ownership is copied only after admission.
        let operation = build();
        let (reply, receiver) = if let Some(token) = token {
            state.tokens.insert(
                token,
                Token {
                    charge: spec.charge,
                    delivered: false,
                },
            );
            (Reply::Async(token), None)
        } else {
            let (sender, receiver) = mpsc::sync_channel(1);
            (Reply::Sync(sender), Some(receiver))
        };
        state.last_sequence = sequence;
        if reserved_control {
            state.control_busy = true;
        } else {
            state.count += 1;
            state.bytes += spec.charge;
        }
        state.pending.push_back(Job {
            sequence,
            spec,
            operation,
            reply,
            reserved_control,
        });
        self.shared.changed.notify_all();
        Ok(receiver)
    }

    pub fn submit(&self, token: u64, operation: Operation) -> IoResult<()> {
        let spec = operation.spec()?;
        self.enqueue(spec, Some(token), || operation)?;
        Ok(())
    }

    pub fn submit_write(&self, token: u64, offset: u64, data: &[u8], fua: bool) -> IoResult<()> {
        let spec = Spec::io(offset, data.len() as u64, fua, data.len(), None)?;
        self.enqueue(spec, Some(token), || Operation::Write {
            offset,
            data: Zeroizing::new(data.to_vec()),
            fua,
        })?;
        Ok(())
    }

    pub fn call(&self, operation: Operation) -> IoResult<Output> {
        let spec = operation.spec()?;
        self.enqueue(spec, None, || operation)?
            .expect("synchronous receiver")
            .recv()
            .map_err(|_| "I/O worker stopped without a reply".to_string())?
    }

    pub fn write(&self, offset: u64, data: &[u8], fua: bool) -> IoResult<Output> {
        let spec = Spec::io(offset, data.len() as u64, fua, data.len(), None)?;
        self.enqueue(spec, None, || Operation::Write {
            offset,
            data: Zeroizing::new(data.to_vec()),
            fua,
        })?
        .expect("synchronous receiver")
        .recv()
        .map_err(|_| "I/O worker stopped without a reply".to_string())?
    }

    pub fn next_completion(&self, timeout: Duration) -> IoResult<Option<Arc<Completion>>> {
        let deadline = Instant::now()
            .checked_add(timeout)
            .ok_or("completion timeout overflow")?;
        let mut state = self
            .shared
            .state
            .lock()
            .map_err(|_| "queue lock poisoned")?;
        loop {
            if let Some(token) = state.ready.pop_front() {
                state
                    .tokens
                    .get_mut(&token)
                    .expect("accepted token")
                    .delivered = true;
                return Ok(Some(
                    state
                        .completions
                        .get(&token)
                        .expect("completed token")
                        .clone(),
                ));
            }
            if state.stopping && state.pending.is_empty() && state.running.is_empty() {
                return Err("I/O queue stopped".into());
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Ok(None);
            }
            let (next, _) = self
                .shared
                .changed
                .wait_timeout(state, remaining)
                .map_err(|_| "queue lock poisoned")?;
            state = next;
        }
    }

    pub fn release_completion(&self, token: u64) -> IoResult<()> {
        let mut state = self
            .shared
            .state
            .lock()
            .map_err(|_| "queue lock poisoned")?;
        let lease = state
            .tokens
            .get(&token)
            .ok_or("unknown or already released completion token")?;
        if !lease.delivered {
            return Err("completion must be delivered before it can be released".into());
        }
        let charge = lease.charge;
        state.completions.remove(&token);
        state.tokens.remove(&token);
        state.count -= 1;
        state.bytes -= charge;
        self.shared.changed.notify_all();
        Ok(())
    }

    pub fn drain(&self) -> IoResult<()> {
        let mut state = self
            .shared
            .state
            .lock()
            .map_err(|_| "queue lock poisoned")?;
        let target = state.last_sequence;
        while state.completed_through < target {
            state = self
                .shared
                .changed
                .wait(state)
                .map_err(|_| "queue lock poisoned")?;
        }
        Ok(())
    }

    /// Interrupt blocked async and ordinary sync admissions without dropping
    /// accepted work. Sync Flush has one separate zero-buffer control slot.
    pub fn cancel_submissions(&self) -> IoResult<()> {
        let mut state = self
            .shared
            .state
            .lock()
            .map_err(|_| "queue lock poisoned")?;
        state.io_accepting = false;
        self.shared.changed.notify_all();
        Ok(())
    }

    pub fn shutdown(&self) {
        {
            let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
            state.accepting = false;
            state.stopping = true;
            self.shared.changed.notify_all();
        }
        let mut workers = self.workers.lock().unwrap_or_else(|e| e.into_inner());
        for worker in workers.drain(..) {
            let _ = worker.join();
        }
    }

    #[cfg(test)]
    pub fn usage(&self) -> (usize, usize) {
        let state = self.shared.state.lock().unwrap();
        (state.count, state.bytes)
    }
}

impl Drop for Queue {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn runnable(state: &State) -> Option<usize> {
    for (index, job) in state.pending.iter().enumerate() {
        if job.spec.barrier {
            return (index == 0 && state.running.is_empty()).then_some(index);
        }
        if state
            .running
            .values()
            .any(|running| job.spec.conflicts(*running))
        {
            continue;
        }
        if state
            .pending
            .iter()
            .take(index)
            .any(|earlier| job.spec.conflicts(earlier.spec))
        {
            continue;
        }
        return Some(index);
    }
    None
}

fn completed(state: &mut State, sequence: u64) {
    let (mut start, mut end) = (sequence, sequence);
    if let Some((&a, &b)) = state.completed_ranges.range(..sequence).next_back() {
        if b.checked_add(1) == Some(sequence) {
            start = a;
            state.completed_ranges.remove(&a);
        }
    }
    if let Some((&a, &b)) = state.completed_ranges.range(sequence..).next() {
        if end.checked_add(1) == Some(a) {
            end = b;
            state.completed_ranges.remove(&a);
        }
    }
    state.completed_ranges.insert(start, end);
    while let Some((&a, &b)) = state.completed_ranges.first_key_value() {
        if state.completed_through.checked_add(1) != Some(a) {
            break;
        }
        state.completed_through = b;
        state.completed_ranges.remove(&a);
    }
}

fn worker_loop(shared: Arc<Shared>) {
    loop {
        let job = {
            let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
            loop {
                if let Some(index) = runnable(&state) {
                    let job = state.pending.remove(index).expect("runnable request");
                    state.running.insert(job.sequence, job.spec);
                    break job;
                }
                if state.stopping && state.pending.is_empty() && state.running.is_empty() {
                    return;
                }
                state = shared
                    .changed
                    .wait(state)
                    .unwrap_or_else(|e| e.into_inner());
            }
        };
        let result = catch_unwind(AssertUnwindSafe(|| shared.backend.execute(&job.operation)))
            .unwrap_or_else(|_| Err("I/O backend panicked; request failed".into()));
        drop(job.operation);
        let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
        state.running.remove(&job.sequence);
        match job.reply {
            Reply::Async(token) => {
                state
                    .completions
                    .insert(token, Arc::new(Completion::new(token, job.spec, result)));
                state.ready.push_back(token);
            }
            Reply::Sync(sender) => {
                if job.reserved_control {
                    state.control_busy = false;
                } else {
                    state.count -= 1;
                    state.bytes -= job.spec.charge;
                }
                let _ = sender.send(result);
            }
        }
        completed(&mut state, job.sequence);
        shared.changed.notify_all();
    }
}
