//! Format-4 execution adapter. Only ordered disk I/O and short snapshot cuts use this queue.
use crate::{
    async_io::{Backend, IoResult, Operation, Output, READ_ALLOCATION},
    v4::Volume,
};
use std::sync::Arc;
use zeroize::Zeroizing;

pub(crate) struct CoreBackend {
    pub(crate) volume: Arc<Volume>,
}
impl Backend for CoreBackend {
    fn execute(&self, operation: &Operation) -> IoResult<Output> {
        let v = &self.volume;
        let error = |e: crate::v4::Error| e.to_string();
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
            Operation::SnapshotCreate => Ok(Output::Bytes(
                v.snapshot_create().map_err(error)?.into_bytes(),
            )),
            Operation::SnapshotRelease(id) => {
                v.snapshot_release(id).map_err(error)?;
                Ok(Output::Unit)
            }
            Operation::Control(request) => Ok(Output::Bytes(
                serde_json::to_vec(&v.control(request).map_err(error)?)
                    .map_err(|e| e.to_string())?,
            )),
        }
    }
}
