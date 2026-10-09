//! Bounded, process-wide CPU workers. Only immutable plaintext references enter
//! the queue; the storage transaction still assigns and publishes ordered slots.
use super::{
    codec::{hash, PageCodec, PageRef},
    Error, Result, PAGE,
};
use std::{
    ops::Deref,
    sync::{mpsc, Arc, Mutex, OnceLock},
    thread,
};
use zeroize::Zeroizing;

pub(super) struct PlainPage {
    pub bytes: Zeroizing<[u8; PAGE]>,
    pub digest: [u8; 32],
}
impl PlainPage {
    pub fn verified(bytes: Zeroizing<[u8; PAGE]>, digest: [u8; 32]) -> Self {
        Self { bytes, digest }
    }
}
impl Deref for PlainPage {
    type Target = [u8; PAGE];
    fn deref(&self) -> &Self::Target {
        &self.bytes
    }
}
impl AsRef<[u8; PAGE]> for PlainPage {
    fn as_ref(&self) -> &[u8; PAGE] {
        &self.bytes
    }
}
pub(super) struct EncodedPage {
    pub index: u64,
    pub reference: PageRef,
    pub digest: [u8; 32],
    pub cipher: Box<[u8; PAGE]>,
}
pub(super) struct Batch {
    pub pages: Vec<EncodedPage>,
    pub tasks: u64,
    pub workers: u64,
    pub parallel: bool,
}
const TASK_PAGES: usize = 64;
const PARALLEL_MIN: usize = 128;
struct Task {
    crypto: Arc<PageCodec>,
    version: u64,
    pages: Vec<(u64, Arc<PlainPage>)>,
    reply: mpsc::Sender<Result<Vec<EncodedPage>>>,
}
struct Pool {
    send: mpsc::SyncSender<Task>,
    workers: usize,
}
static POOL: OnceLock<std::result::Result<Pool, String>> = OnceLock::new();
impl Pool {
    fn create() -> std::result::Result<Self, String> {
        let workers = thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(1)
            .min(4);
        let (send, receive) = mpsc::sync_channel::<Task>(8);
        let receive = Arc::new(Mutex::new(receive));
        for n in 0..workers {
            let receive = receive.clone();
            thread::Builder::new()
                .name(format!("overlay-page-{n}"))
                .spawn(move || loop {
                    let task = match receive.lock() {
                        Ok(r) => r.recv(),
                        Err(_) => return,
                    };
                    let Ok(task) = task else {
                        return;
                    };
                    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                        encode_task(&task.crypto, task.version, &task.pages)
                    }))
                    .unwrap_or_else(|_| {
                        Err(Error::Invalid("page encoding worker panicked".into()))
                    });
                    let _ = task.reply.send(result);
                })
                .map_err(|e| e.to_string())?;
        }
        Ok(Self { send, workers })
    }
}
fn encode_task(
    crypto: &PageCodec,
    version: u64,
    pages: &[(u64, Arc<PlainPage>)],
) -> Result<Vec<EncodedPage>> {
    let mut out = Vec::with_capacity(pages.len());
    for (index, plain) in pages {
        let nonce = [0; 24];
        let (cipher, tag) = crypto.encode_page(*index, version, nonce, &plain.bytes)?;
        out.push(EncodedPage {
            index: *index,
            reference: PageRef {
                object: 0,
                object_generation: 0,
                slot: 0,
                version,
                nonce,
                tag,
            },
            digest: plain.digest,
            cipher: Box::new(cipher),
        });
    }
    Ok(out)
}
pub(super) fn encode_batch(
    crypto: Arc<PageCodec>,
    version: u64,
    pages: &[(u64, Arc<PlainPage>)],
) -> Result<Batch> {
    if pages.is_empty() {
        return Ok(Batch {
            pages: Vec::new(),
            tasks: 0,
            workers: 0,
            parallel: false,
        });
    }
    let parallel =
        pages.len() >= PARALLEL_MIN && thread::available_parallelism().is_ok_and(|n| n.get() > 1);
    if !parallel {
        let mut encoded = Vec::with_capacity(pages.len());
        for chunk in pages.chunks(TASK_PAGES) {
            encoded.extend(encode_task(&crypto, version, chunk)?);
        }
        return Ok(Batch {
            pages: encoded,
            tasks: pages.len().div_ceil(TASK_PAGES) as u64,
            workers: 1,
            parallel: false,
        });
    }
    let pool = POOL
        .get_or_init(Pool::create)
        .as_ref()
        .map_err(|e| Error::Invalid(format!("page encoding pool: {e}")))?;
    let (reply, results) = mpsc::channel();
    let mut accepted = 0;
    let mut failed = None;
    for chunk in pages.chunks(TASK_PAGES) {
        if pool
            .send
            .send(Task {
                crypto: crypto.clone(),
                version,
                pages: chunk.to_vec(),
                reply: reply.clone(),
            })
            .is_err()
        {
            failed = Some(Error::Invalid("page encoding workers stopped".into()));
            break;
        }
        accepted += 1;
    }
    drop(reply);
    let mut encoded = Vec::with_capacity(pages.len());
    for _ in 0..accepted {
        match results.recv() {
            Ok(Ok(part)) => encoded.extend(part),
            Ok(Err(error)) => {
                failed.get_or_insert(error);
            }
            Err(_) => {
                failed.get_or_insert(Error::Invalid("page encoding completion missing".into()));
                break;
            }
        }
    }
    if let Some(error) = failed {
        return Err(error);
    }
    encoded.sort_unstable_by_key(|p| p.index);
    Ok(Batch {
        pages: encoded,
        tasks: accepted as u64,
        workers: pool.workers as u64,
        parallel: true,
    })
}
pub(super) fn encode_one(
    crypto: &PageCodec,
    index: u64,
    version: u64,
    bytes: &[u8; PAGE],
) -> Result<EncodedPage> {
    let nonce = [0; 24];
    let (cipher, tag) = crypto.encode_page(index, version, nonce, bytes)?;
    Ok(EncodedPage {
        index,
        reference: PageRef {
            object: 0,
            object_generation: 0,
            slot: 0,
            version,
            nonce,
            tag,
        },
        digest: hash(bytes),
        cipher: Box::new(cipher),
    })
}
