//! Advisory classification only. No NTFS byte is discarded or rewritten here.
//! Layout references: Microsoft NTFS boot/MFT documentation and Linux ntfs3/ntfs.h.
//! Accept a bounded, CRC-checked GPT basic-data layout (optional MSR), or legacy
//! single-partition MBR. Unknown/corrupt layouts remain ordinary data.
use super::{Error, Result};
use serde::{Deserialize, Serialize};

const BUDGET: usize = 512 * 1024;
const MAX_RANGES: usize = 4096;
const PARTITION: u64 = 1024 * 1024;
const CLUSTER: u64 = 4096;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct Range {
    pub start: u64,
    pub end: u64,
    pub kind: String,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Classifier {
    pub ranges: Vec<Range>,
    pub watch: Vec<(u64, u64)>,
    pub recognized: bool,
    pub probe_bytes: u64,
}
#[derive(Clone, Debug)]
struct Run {
    vcn: u64,
    length: u64,
    lcn: Option<u64>,
}
struct Budget<F> {
    reader: F,
    capacity: u64,
    spent: usize,
    partition: u64,
}
impl<F: FnMut(u64, &mut [u8]) -> Result<()>> Budget<F> {
    fn read(&mut self, offset: u64, out: &mut [u8]) -> Result<()> {
        if offset
            .checked_add(out.len() as u64)
            .is_none_or(|end| end > self.capacity)
            || self.spent.checked_add(out.len()).is_none_or(|n| n > BUDGET)
        {
            return Err(invalid("NTFS classification read budget/bounds"));
        }
        self.spent += out.len();
        (self.reader)(offset, out)
    }
    fn stream(
        &mut self,
        runs: &[Run],
        offset: u64,
        out: &mut [u8],
        watch: &mut Vec<(u64, u64)>,
    ) -> Result<()> {
        let mut at = offset;
        let mut done = 0;
        while done < out.len() {
            let vcn = at / CLUSTER;
            let within = at % CLUSTER;
            let run = runs
                .iter()
                .find(|r| vcn >= r.vcn && vcn < r.vcn + r.length)
                .ok_or_else(|| invalid("NTFS stream extent missing"))?;
            let lcn = run
                .lcn
                .ok_or_else(|| invalid("sparse NTFS metadata record"))?;
            let available = (run.vcn + run.length - vcn) * CLUSTER - within;
            let take = available.min((out.len() - done) as u64) as usize;
            let physical = self
                .partition
                .checked_add((lcn + vcn - run.vcn) * CLUSTER)
                .and_then(|n| n.checked_add(within))
                .ok_or_else(|| invalid("NTFS extent overflow"))?;
            self.read(physical, &mut out[done..done + take])?;
            watch.push((physical, physical + take as u64));
            at += take as u64;
            done += take;
        }
        Ok(())
    }
}
fn invalid(message: &str) -> Error {
    Error::Invalid(message.into())
}
fn word(bytes: &[u8], at: usize) -> Result<u16> {
    Ok(u16::from_le_bytes(
        bytes
            .get(at..at + 2)
            .ok_or_else(|| invalid("NTFS u16 bounds"))?
            .try_into()
            .unwrap(),
    ))
}
fn dword(bytes: &[u8], at: usize) -> Result<u32> {
    Ok(u32::from_le_bytes(
        bytes
            .get(at..at + 4)
            .ok_or_else(|| invalid("NTFS u32 bounds"))?
            .try_into()
            .unwrap(),
    ))
}
fn qword(bytes: &[u8], at: usize) -> Result<u64> {
    Ok(u64::from_le_bytes(
        bytes
            .get(at..at + 8)
            .ok_or_else(|| invalid("NTFS u64 bounds"))?
            .try_into()
            .unwrap(),
    ))
}

struct Layout {
    start: u64,
    length: u64,
    watch: Vec<(u64, u64)>,
}
const BASIC_DATA: [u8; 16] = [
    0xa2, 0xa0, 0xd0, 0xeb, 0xe5, 0xb9, 0x33, 0x44, 0x87, 0xc0, 0x68, 0xb6, 0xb7, 0x26, 0x99, 0xc7,
];
const MSR: [u8; 16] = [
    0x16, 0xe3, 0xc9, 0xe3, 0x5c, 0x0b, 0xb8, 0x4d, 0x81, 0x7d, 0xf9, 0x2d, 0xf0, 0x02, 0x15, 0xae,
];
fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = !0u32;
    for byte in bytes {
        crc ^= *byte as u32;
        for _ in 0..8 {
            crc = (crc >> 1) ^ (0xedb8_8320 & 0u32.wrapping_sub(crc & 1));
        }
    }
    !crc
}
#[derive(PartialEq, Eq)]
struct Gpt {
    first: u64,
    last: u64,
    guid: [u8; 16],
    count: u32,
    entries: u64,
    crc: u32,
}
fn gpt_header(bytes: &[u8], own: u64, other: u64) -> Result<Gpt> {
    let size = dword(bytes, 12)? as usize;
    if &bytes[..8] != b"EFI PART"
        || dword(bytes, 8)? != 0x0001_0000
        || !(92..=512).contains(&size)
        || dword(bytes, 20)? != 0
        || qword(bytes, 24)? != own
        || qword(bytes, 32)? != other
    {
        return Err(invalid("GPT header geometry"));
    }
    let mut checked = bytes[..size].to_vec();
    checked[16..20].fill(0);
    if crc32(&checked) != dword(bytes, 16)? {
        return Err(invalid("GPT header CRC"));
    }
    let h = Gpt {
        first: qword(bytes, 40)?,
        last: qword(bytes, 48)?,
        guid: bytes[56..72].try_into().unwrap(),
        count: dword(bytes, 80)?,
        entries: qword(bytes, 72)?,
        crc: dword(bytes, 88)?,
    };
    if !(1..=1024).contains(&h.count)
        || dword(bytes, 84)? != 128
        || h.guid == [0; 16]
        || h.first < 34
        || h.last < h.first
        || h.last >= own.max(other)
    {
        return Err(invalid("GPT usable range/table size"));
    }
    let sectors = (h.count as u64 * 128).div_ceil(512);
    let end = h
        .entries
        .checked_add(sectors)
        .ok_or_else(|| invalid("GPT table overflow"))?;
    if own == 1 && (h.entries < 2 || end > h.first)
        || own != 1 && (h.entries <= h.last || end > own)
    {
        return Err(invalid("GPT table overlaps usable range"));
    }
    Ok(h)
}
fn discover<F: FnMut(u64, &mut [u8]) -> Result<()>>(
    input: &mut Budget<F>,
) -> Result<Option<Layout>> {
    let mut mbr = [0u8; 512];
    input.read(0, &mut mbr)?;
    if mbr[510..] != [0x55, 0xaa] || !input.capacity.is_multiple_of(512) {
        return Ok(None);
    }
    let entries: Vec<_> = mbr[446..510]
        .chunks_exact(16)
        .filter(|e| e[4] != 0)
        .collect();
    if entries.len() != 1 {
        return Ok(None);
    }
    let e = entries[0];
    let start = dword(e, 8)? as u64;
    let count = dword(e, 12)? as u64;
    if e[4] == 7 {
        let start = start * 512;
        let length = count * 512;
        if start < PARTITION
            || !start.is_multiple_of(PARTITION)
            || length < CLUSTER
            || start
                .checked_add(length)
                .is_none_or(|end| end > input.capacity)
        {
            return Ok(None);
        }
        return Ok(Some(Layout {
            start,
            length,
            watch: vec![(0, 512)],
        }));
    }
    let sectors = input.capacity / 512;
    if e[4] != 0xee || sectors < 68 || start != 1 || count != (sectors - 1).min(u32::MAX as u64) {
        return Ok(None);
    }
    let mut primary = [0u8; 512];
    let mut backup = [0u8; 512];
    input.read(512, &mut primary)?;
    input.read(input.capacity - 512, &mut backup)?;
    let p = gpt_header(&primary, 1, sectors - 1)?;
    let b = gpt_header(&backup, sectors - 1, 1)?;
    if p.first != b.first
        || p.last != b.last
        || p.guid != b.guid
        || p.count != b.count
        || p.crc != b.crc
    {
        return Err(invalid("GPT copies disagree"));
    }
    let len = p.count as usize * 128;
    let mut table = vec![0u8; len];
    let mut mirror = vec![0u8; len];
    input.read(
        p.entries
            .checked_mul(512)
            .ok_or_else(|| invalid("GPT LBA overflow"))?,
        &mut table,
    )?;
    input.read(
        b.entries
            .checked_mul(512)
            .ok_or_else(|| invalid("GPT LBA overflow"))?,
        &mut mirror,
    )?;
    if crc32(&table) != p.crc || crc32(&mirror) != b.crc || table != mirror {
        return Err(invalid("GPT table CRC/copies"));
    }
    let mut partitions = Vec::new();
    let mut data = None;
    let mut msr = false;
    for row in table.chunks_exact(128) {
        if row[..16] == [0; 16] {
            if row.iter().any(|x| *x != 0) {
                return Err(invalid("GPT nonempty unused entry"));
            }
            continue;
        }
        let first = qword(row, 32)?;
        let last = qword(row, 40)?;
        if row[16..32] == [0; 16]
            || first < p.first
            || last > p.last
            || first > last
            || partitions.iter().any(|(a, z, g): &(u64, u64, [u8; 16])| {
                first <= *z && *a <= last || row[16..32] == *g
            })
        {
            return Err(invalid("GPT partition bounds/identity/overlap"));
        }
        partitions.push((first, last, row[16..32].try_into().unwrap()));
        if row[..16] == BASIC_DATA {
            if data.is_some() {
                return Err(invalid("multiple GPT data partitions"));
            }
            data = Some((first, last));
        } else if row[..16] == MSR {
            if msr {
                return Err(invalid("multiple GPT reserved partitions"));
            }
            msr = true;
        } else {
            return Err(invalid("unsupported GPT partition type"));
        }
    }
    let Some((first, last)) = data else {
        return Ok(None);
    };
    let start = first
        .checked_mul(512)
        .ok_or_else(|| invalid("GPT partition offset overflow"))?;
    let length = (last - first + 1)
        .checked_mul(512)
        .ok_or_else(|| invalid("GPT partition size overflow"))?;
    if start < PARTITION || !start.is_multiple_of(PARTITION) {
        return Ok(None);
    }
    Ok(Some(Layout {
        start,
        length,
        watch: vec![
            (0, 1024),
            (p.entries * 512, p.entries * 512 + len as u64),
            (b.entries * 512, b.entries * 512 + len as u64),
            (input.capacity - 512, input.capacity),
        ],
    }))
}

impl Classifier {
    pub fn classify(&self, offset: u64, length: u64) -> &'static str {
        let end = offset.saturating_add(length);
        for kind in ["ntfs_log", "ntfs_usn", "ntfs_mft", "ntfs_metadata"] {
            if self
                .ranges
                .iter()
                .any(|r| r.kind == kind && r.start < end && offset < r.end)
            {
                return kind;
            }
        }
        "data"
    }
    pub fn needs_refresh(&self, offset: u64, length: u64) -> bool {
        let end = offset.saturating_add(length);
        // An unformatted volume only needs discovery when its partition/boot area changes.
        if !self.recognized {
            return self.watch.iter().any(|(a, b)| *a < end && offset < *b)
                || offset < 34 * 512
                || offset < PARTITION + 4096 && end > PARTITION;
        }
        self.watch.iter().any(|(a, b)| *a < end && offset < *b)
    }
    pub fn probe<F: FnMut(u64, &mut [u8]) -> Result<()>>(capacity: u64, read: F) -> Result<Self> {
        let mut input = Budget {
            reader: read,
            capacity,
            spent: 0,
            partition: 0,
        };
        let mut fallback = Self {
            watch: vec![(0, 34 * 512)],
            ..Self::default()
        };
        if capacity >= 34 * 512 {
            fallback.watch.push((capacity - 33 * 512, capacity));
        }
        let layout = match discover(&mut input) {
            Ok(Some(layout)) => layout,
            _ => {
                fallback.probe_bytes = input.spent as u64;
                return Ok(fallback);
            }
        };
        input.partition = layout.start;
        fallback.watch = layout.watch.clone();
        fallback.watch.push((layout.start, layout.start + 4096));
        let result = Self::probe_ntfs(&mut input, &layout);
        Ok(match result {
            Ok(classifier) => classifier,
            Err(_) => {
                fallback.probe_bytes = input.spent as u64;
                fallback
            }
        })
    }
    fn probe_ntfs<F: FnMut(u64, &mut [u8]) -> Result<()>>(
        input: &mut Budget<F>,
        layout: &Layout,
    ) -> Result<Self> {
        let partition = layout.start;
        let mut boot = [0u8; 512];
        input.read(partition, &mut boot)?;
        if &boot[3..11] != b"NTFS    " || boot[510..] != [0x55, 0xaa] {
            return Err(invalid("unsupported NTFS boot geometry"));
        }
        let sector = word(&boot, 11)? as usize;
        if !matches!(sector, 512 | 4096) || sector as u64 * boot[13] as u64 != CLUSTER {
            return Err(invalid("unsupported NTFS boot geometry"));
        }
        let volume_bytes = qword(&boot, 40)?
            .checked_mul(sector as u64)
            .ok_or_else(|| invalid("NTFS volume overflow"))?;
        if volume_bytes == 0 || volume_bytes > layout.length {
            return Err(invalid("NTFS volume outside disk"));
        }
        let clusters = volume_bytes / CLUSTER;
        let size = record_size(boot[64] as i8)?;
        let index_size = record_size(boot[68] as i8)?;
        let mft_lcn = qword(&boot, 48)?;
        if mft_lcn >= clusters {
            return Err(invalid("NTFS MFT outside disk"));
        }
        let mut result = Self {
            watch: layout
                .watch
                .iter()
                .copied()
                .chain([(partition, partition + 4096)])
                .collect(),
            recognized: true,
            ..Self::default()
        };
        result.ranges.push(Range {
            start: partition,
            end: partition + 4096,
            kind: "ntfs_metadata".into(),
        });
        let mut first = vec![0u8; size];
        let first_at = partition + mft_lcn * CLUSTER;
        if first_at + size as u64 > partition + volume_bytes {
            return Err(invalid("NTFS MFT record outside partition"));
        }
        input.read(first_at, &mut first)?;
        result.watch.push((first_at, first_at + size as u64));
        fixup(&mut first, sector, b"FILE")?;
        let first_attrs = attributes(&first)?;
        let mut mft = Vec::new();
        for attr in &first_attrs {
            if dword(attr, 0)? == 0x80 && attr[9] == 0 && attr[8] == 1 {
                mft.extend(runs(attr, clusters)?);
            }
        }
        if mft.is_empty() {
            return Err(invalid("NTFS MFT mapping absent"));
        }
        result.add_runs(&mft, "ntfs_mft", partition)?;
        let mut extended_ids = Vec::new();
        for number in 1..16u64 {
            let mut record = vec![0; size];
            input.stream(&mft, number * size as u64, &mut record, &mut result.watch)?;
            if record.iter().all(|b| *b == 0) {
                continue;
            }
            if fixup(&mut record, sector, b"FILE").is_err() {
                continue;
            }
            if word(&record, 22)? & 1 == 0 {
                continue;
            }
            let attrs = attributes(&record)?;
            let kind = match number {
                1 => "ntfs_mft",
                2 => "ntfs_log",
                _ => "ntfs_metadata",
            };
            for attr in &attrs {
                if attr[8] == 1 {
                    result.add_runs(&runs(attr, clusters)?, kind, partition)?;
                }
            }
            if number == 11 {
                for attr in &attrs {
                    if dword(attr, 0)? == 0x90 && attr[8] == 0 {
                        let data = resident(attr)?;
                        if data.len() >= 32 {
                            extended_ids.extend(index_entries(data, 16)?);
                        }
                    }
                    if dword(attr, 0)? == 0xa0 && attr[8] == 1 {
                        let stream = runs(attr, clusters)?;
                        let bytes = qword(attr, 48)?.min(128 * 1024);
                        for i in 0..(bytes / index_size as u64).min(16) {
                            let mut node = vec![0; index_size];
                            if input
                                .stream(
                                    &stream,
                                    i * index_size as u64,
                                    &mut node,
                                    &mut result.watch,
                                )
                                .is_err()
                            {
                                break;
                            }
                            if fixup(&mut node, sector, b"INDX").is_ok() {
                                extended_ids.extend(index_entries(&node, 24)?);
                            }
                        }
                    }
                }
            }
        }
        extended_ids.sort();
        extended_ids.dedup();
        for (number, name) in extended_ids.into_iter().take(32) {
            if !matches!(
                name.as_str(),
                "$UsnJrnl" | "$Quota" | "$ObjId" | "$Reparse" | "$RmMetadata"
            ) {
                continue;
            }
            let Some(at) = number.checked_mul(size as u64) else {
                continue;
            };
            let mut record = vec![0; size];
            if input
                .stream(&mft, at, &mut record, &mut result.watch)
                .is_err()
            {
                continue;
            }
            if fixup(&mut record, sector, b"FILE").is_err() || word(&record, 22)? & 1 == 0 {
                continue;
            }
            let attrs = attributes(&record)?;
            // An index entry alone is insufficient: validate the child's resident name/parent too.
            let verified = attrs.iter().any(|a| {
                dword(a, 0).ok() == Some(0x30)
                    && a[8] == 0
                    && resident(a)
                        .ok()
                        .and_then(|v| file_name(v).ok())
                        .is_some_and(|(parent, n)| parent == 11 && n == name)
            });
            if !verified {
                continue;
            }
            for attr in attrs {
                if attr[8] == 1 {
                    result.add_runs(
                        &runs(attr, clusters)?,
                        if name == "$UsnJrnl" {
                            "ntfs_usn"
                        } else {
                            "ntfs_metadata"
                        },
                        partition,
                    )?;
                }
            }
        }
        result.ranges.sort_by_key(|r| (r.start, r.end));
        result.ranges.dedup();
        result.watch.sort_unstable();
        result.watch.dedup();
        result.probe_bytes = input.spent as u64;
        Ok(result)
    }
    fn add_runs(&mut self, runs: &[Run], kind: &str, partition: u64) -> Result<()> {
        for run in runs {
            if let Some(lcn) = run.lcn {
                if self.ranges.len() >= MAX_RANGES {
                    return Err(invalid("NTFS classification extent budget"));
                }
                self.ranges.push(Range {
                    start: partition + lcn * CLUSTER,
                    end: partition + (lcn + run.length) * CLUSTER,
                    kind: kind.into(),
                });
            }
        }
        Ok(())
    }
}
fn record_size(value: i8) -> Result<usize> {
    let size = if value < 0 {
        1usize.checked_shl(-(value as i32) as u32)
    } else {
        (value as usize).checked_mul(CLUSTER as usize)
    }
    .ok_or_else(|| invalid("NTFS record size overflow"))?;
    if !(512..=65536).contains(&size) || !size.is_power_of_two() {
        return Err(invalid("NTFS record size unsupported"));
    }
    Ok(size)
}
fn fixup(record: &mut [u8], sector: usize, signature: &[u8; 4]) -> Result<()> {
    if record.get(..4) != Some(signature) || !record.len().is_multiple_of(sector) {
        return Err(invalid("NTFS record signature/size"));
    }
    let at = word(record, 4)? as usize;
    let count = word(record, 6)? as usize;
    if count != record.len() / sector + 1
        || at < 8
        || at.checked_add(count * 2).is_none_or(|n| n > record.len())
    {
        return Err(invalid("NTFS fixup bounds"));
    }
    let update = word(record, at)?;
    for i in 1..count {
        let end = i * sector - 2;
        if word(record, end)? != update {
            return Err(invalid("torn NTFS record"));
        }
        let value = word(record, at + i * 2)?.to_le_bytes();
        record[end..end + 2].copy_from_slice(&value);
    }
    Ok(())
}
fn attributes(record: &[u8]) -> Result<Vec<&[u8]>> {
    let mut at = word(record, 20)? as usize;
    let used = dword(record, 24)? as usize;
    if at < 48 || used > record.len() || at >= used {
        return Err(invalid("NTFS attributes bounds"));
    }
    let mut result = Vec::new();
    while at + 4 <= used {
        if dword(record, at)? == u32::MAX {
            return Ok(result);
        }
        let length = dword(record, at + 4)? as usize;
        if length < 24
            || !length.is_multiple_of(8)
            || at.checked_add(length).is_none_or(|n| n > used)
        {
            return Err(invalid("NTFS attribute length"));
        }
        let attr = &record[at..at + length];
        if attr[8] > 1 || attr[8] == 1 && length < 64 {
            return Err(invalid("NTFS attribute representation"));
        }
        result.push(attr);
        at += length;
        if result.len() > 128 {
            return Err(invalid("NTFS attribute count budget"));
        }
    }
    Err(invalid("NTFS attributes have no terminator"))
}
fn resident(attr: &[u8]) -> Result<&[u8]> {
    let start = word(attr, 20)? as usize;
    let len = dword(attr, 16)? as usize;
    attr.get(
        start
            ..start
                .checked_add(len)
                .ok_or_else(|| invalid("NTFS resident overflow"))?,
    )
    .ok_or_else(|| invalid("NTFS resident bounds"))
}
fn runs(attr: &[u8], clusters: u64) -> Result<Vec<Run>> {
    let mut at = word(attr, 32)? as usize;
    if at < 64 || at >= attr.len() {
        return Err(invalid("NTFS mapping pairs offset"));
    }
    let mut vcn = qword(attr, 16)?;
    let last = qword(attr, 24)?;
    let mut lcn = 0i128;
    let mut result = Vec::new();
    while at < attr.len() {
        let header = attr[at];
        at += 1;
        if header == 0 {
            return if vcn
                == last
                    .checked_add(1)
                    .ok_or_else(|| invalid("NTFS VCN overflow"))?
            {
                Ok(result)
            } else {
                Err(invalid("NTFS mapping length mismatch"))
            };
        }
        let len = (header & 15) as usize;
        let off = (header >> 4) as usize;
        if len == 0 || len > 8 || off > 8 || at + len + off > attr.len() {
            return Err(invalid("NTFS mapping pair bounds"));
        }
        let mut n = [0u8; 8];
        n[..len].copy_from_slice(&attr[at..at + len]);
        at += len;
        let length = u64::from_le_bytes(n);
        if length == 0 {
            return Err(invalid("zero NTFS run"));
        }
        let physical = if off == 0 {
            None
        } else {
            let mut value = if attr[at + off - 1] & 0x80 != 0 {
                [0xff; 8]
            } else {
                [0; 8]
            };
            value[..off].copy_from_slice(&attr[at..at + off]);
            lcn = lcn
                .checked_add(i64::from_le_bytes(value) as i128)
                .ok_or_else(|| invalid("NTFS LCN overflow"))?;
            if lcn < 0
                || lcn > u64::MAX as i128
                || lcn as u64 >= clusters
                || (lcn as u64)
                    .checked_add(length)
                    .is_none_or(|end| end > clusters)
            {
                return Err(invalid("NTFS run outside volume"));
            }
            Some(lcn as u64)
        };
        at += off;
        result.push(Run {
            vcn,
            length,
            lcn: physical,
        });
        vcn = vcn
            .checked_add(length)
            .ok_or_else(|| invalid("NTFS VCN overflow"))?;
        if result.len() > MAX_RANGES {
            return Err(invalid("NTFS run budget"));
        }
    }
    Err(invalid("unterminated NTFS runlist"))
}
fn file_name(data: &[u8]) -> Result<(u64, String)> {
    let count = *data
        .get(64)
        .ok_or_else(|| invalid("NTFS filename header"))? as usize;
    let bytes = data
        .get(66..66 + count * 2)
        .ok_or_else(|| invalid("NTFS filename bounds"))?;
    let text = String::from_utf16(
        &bytes
            .chunks_exact(2)
            .map(|b| u16::from_le_bytes([b[0], b[1]]))
            .collect::<Vec<_>>(),
    )
    .map_err(|_| invalid("NTFS filename encoding"))?;
    Ok((qword(data, 0)? & 0x0000_ffff_ffff_ffff, text))
}
fn index_entries(bytes: &[u8], base: usize) -> Result<Vec<(u64, String)>> {
    let mut at = base
        .checked_add(dword(bytes, base)? as usize)
        .ok_or_else(|| invalid("NTFS index overflow"))?;
    let end = base
        .checked_add(dword(bytes, base + 4)? as usize)
        .filter(|n| *n <= bytes.len())
        .ok_or_else(|| invalid("NTFS index bounds"))?;
    if at < base + 16 || at > end {
        return Err(invalid("NTFS index offset"));
    }
    let mut out = Vec::new();
    while at + 16 <= end {
        let length = word(bytes, at + 8)? as usize;
        let key = word(bytes, at + 10)? as usize;
        let flags = word(bytes, at + 12)?;
        if length < 16 || !length.is_multiple_of(8) || at + length > end || key > length - 16 {
            return Err(invalid("NTFS index entry bounds"));
        }
        if flags & 2 != 0 {
            break;
        }
        let (_, name) = file_name(&bytes[at + 16..at + 16 + key])?;
        out.push((qword(bytes, at)? & 0x0000_ffff_ffff_ffff, name));
        at += length;
        if out.len() > 256 {
            return Err(invalid("NTFS index entry budget"));
        }
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn attr_resident(kind: u32, data: &[u8]) -> Vec<u8> {
        let size = (24 + data.len()).div_ceil(8) * 8;
        let mut a = vec![0; size];
        a[..4].copy_from_slice(&kind.to_le_bytes());
        a[4..8].copy_from_slice(&(size as u32).to_le_bytes());
        a[16..20].copy_from_slice(&(data.len() as u32).to_le_bytes());
        a[20..22].copy_from_slice(&24u16.to_le_bytes());
        a[24..24 + data.len()].copy_from_slice(data);
        a
    }
    fn named(parent: u64, name: &str) -> Vec<u8> {
        let text: Vec<u16> = name.encode_utf16().collect();
        let mut out = vec![0; 66 + text.len() * 2];
        out[..8].copy_from_slice(&parent.to_le_bytes());
        out[64] = text.len() as u8;
        out[65] = 3;
        for (i, c) in text.iter().enumerate() {
            out[66 + i * 2..68 + i * 2].copy_from_slice(&c.to_le_bytes());
        }
        out
    }
    fn attr_run(lcn: u8, length: u8) -> Vec<u8> {
        let mut a = vec![0; 72];
        a[..4].copy_from_slice(&0x80u32.to_le_bytes());
        a[4..8].copy_from_slice(&72u32.to_le_bytes());
        a[8] = 1;
        a[24..32].copy_from_slice(&(length as u64 - 1).to_le_bytes());
        a[32..34].copy_from_slice(&64u16.to_le_bytes());
        a[48..56].copy_from_slice(&(length as u64 * CLUSTER).to_le_bytes());
        a[64..68].copy_from_slice(&[0x11, length, lcn, 0]);
        a
    }
    fn record(attrs: &[Vec<u8>]) -> Vec<u8> {
        let mut b = vec![0; 1024];
        b[..4].copy_from_slice(b"FILE");
        b[4..6].copy_from_slice(&48u16.to_le_bytes());
        b[6..8].copy_from_slice(&3u16.to_le_bytes());
        b[20..22].copy_from_slice(&56u16.to_le_bytes());
        b[22..24].copy_from_slice(&1u16.to_le_bytes());
        b[28..32].copy_from_slice(&1024u32.to_le_bytes());
        let mut at = 56;
        for a in attrs {
            b[at..at + a.len()].copy_from_slice(a);
            at += a.len();
        }
        b[at..at + 4].copy_from_slice(&u32::MAX.to_le_bytes());
        b[24..28].copy_from_slice(&((at + 4) as u32).to_le_bytes());
        b[48..50].copy_from_slice(&[0xab, 0xcd]);
        for i in 1..=2 {
            let p = i * 512 - 2;
            let saved = [b[p], b[p + 1]];
            b[48 + i * 2..50 + i * 2].copy_from_slice(&saved);
            b[p..p + 2].copy_from_slice(&[0xab, 0xcd]);
        }
        b
    }
    fn fixture() -> Vec<u8> {
        let mut disk = vec![0; PARTITION as usize + 128 * 4096];
        disk[510..512].copy_from_slice(&[0x55, 0xaa]);
        disk[450] = 7;
        disk[454..458].copy_from_slice(&2048u32.to_le_bytes());
        disk[458..462].copy_from_slice(&1024u32.to_le_bytes());
        let boot = &mut disk[PARTITION as usize..PARTITION as usize + 512];
        boot[3..11].copy_from_slice(b"NTFS    ");
        boot[11..13].copy_from_slice(&512u16.to_le_bytes());
        boot[13] = 8;
        boot[40..48].copy_from_slice(&1024u64.to_le_bytes());
        boot[48..56].copy_from_slice(&4u64.to_le_bytes());
        boot[64] = (-10i8) as u8;
        boot[68] = 1;
        boot[510..512].copy_from_slice(&[0x55, 0xaa]);
        let mft = PARTITION as usize + 4 * 4096;
        for n in 0..16 {
            let attrs = match n {
                0 => vec![attr_run(4, 8)],
                1 => vec![attr_run(20, 1)],
                2 => vec![attr_run(24, 8)],
                _ => vec![],
            };
            disk[mft + n * 1024..mft + (n + 1) * 1024].copy_from_slice(&record(&attrs));
        }
        disk
    }
    fn header_crc(header: &mut [u8]) {
        header[16..20].fill(0);
        let crc = crc32(&header[..92]);
        header[16..20].copy_from_slice(&crc.to_le_bytes());
    }
    fn gpt_fixture(capacity: u64, partition: usize, msr: bool) -> (Vec<u8>, Vec<u8>) {
        let legacy = fixture();
        let mut low = vec![0u8; partition + 128 * 4096];
        low[partition..].copy_from_slice(&legacy[PARTITION as usize..]);
        let sectors = capacity / 512;
        let last = (sectors - 34) / 8 * 8 - 1;
        low[partition + 40..partition + 48]
            .copy_from_slice(&(last - partition as u64 / 512 + 1).to_le_bytes());
        low[510..512].copy_from_slice(&[0x55, 0xaa]);
        low[450] = 0xee;
        low[454..458].copy_from_slice(&1u32.to_le_bytes());
        low[458..462].copy_from_slice(&((sectors - 1).min(u32::MAX as u64) as u32).to_le_bytes());
        let row = &mut low[1024..1152];
        row[..16].copy_from_slice(&BASIC_DATA);
        row[16..32].fill(44);
        row[32..40].copy_from_slice(&(partition as u64 / 512).to_le_bytes());
        row[40..48].copy_from_slice(&last.to_le_bytes());
        if msr {
            let row = &mut low[1152..1280];
            row[..16].copy_from_slice(&MSR);
            row[16..32].fill(55);
            row[32..40].copy_from_slice(&2048u64.to_le_bytes());
            row[40..48].copy_from_slice(&(partition as u64 / 512 - 1).to_le_bytes());
        }
        let table_crc = crc32(&low[1024..1024 + 16384]);
        let h = &mut low[512..1024];
        h[..8].copy_from_slice(b"EFI PART");
        h[8..12].copy_from_slice(&0x0001_0000u32.to_le_bytes());
        h[12..16].copy_from_slice(&92u32.to_le_bytes());
        h[24..32].copy_from_slice(&1u64.to_le_bytes());
        h[32..40].copy_from_slice(&(sectors - 1).to_le_bytes());
        h[40..48].copy_from_slice(&34u64.to_le_bytes());
        h[48..56].copy_from_slice(&(sectors - 34).to_le_bytes());
        h[56..72].fill(33);
        h[72..80].copy_from_slice(&2u64.to_le_bytes());
        h[80..84].copy_from_slice(&128u32.to_le_bytes());
        h[84..88].copy_from_slice(&128u32.to_le_bytes());
        h[88..92].copy_from_slice(&table_crc.to_le_bytes());
        header_crc(h);
        let mut backup = vec![0u8; 33 * 512];
        backup[..16384].copy_from_slice(&low[1024..1024 + 16384]);
        let h = &mut backup[16384..];
        h.copy_from_slice(&low[512..1024]);
        h[24..32].copy_from_slice(&(sectors - 1).to_le_bytes());
        h[32..40].copy_from_slice(&1u64.to_le_bytes());
        h[72..80].copy_from_slice(&(sectors - 33).to_le_bytes());
        header_crc(h);
        (low, backup)
    }
    fn probe_gpt(capacity: u64, low: &[u8], backup: &[u8]) -> Classifier {
        Classifier::probe(capacity, |at, out| {
            if at >= capacity - backup.len() as u64 {
                let offset = (at - (capacity - backup.len() as u64)) as usize;
                out.copy_from_slice(&backup[offset..offset + out.len()]);
            } else {
                let offset = usize::try_from(at).map_err(|_| invalid("fixture offset"))?;
                let data = low
                    .get(offset..offset + out.len())
                    .ok_or_else(|| invalid("fixture read outside sparse metadata"))?;
                out.copy_from_slice(data);
            }
            Ok(())
        })
        .unwrap()
    }
    fn rewrite_gpt_table(low: &mut [u8], backup: &mut [u8]) {
        backup[..16384].copy_from_slice(&low[1024..1024 + 16384]);
        let crc = crc32(&backup[..16384]);
        low[600..604].copy_from_slice(&crc.to_le_bytes());
        backup[16472..16476].copy_from_slice(&crc.to_le_bytes());
        header_crc(&mut low[512..1024]);
        header_crc(&mut backup[16384..]);
    }
    #[test]
    fn gpt_eight_tib_msr_and_single_partition_keep_absolute_classification() {
        assert_eq!(crc32(b"123456789"), 0xcbf4_3926);
        for (capacity, partition, msr) in [
            (8u64 << 40, 3 * 1024 * 1024, true),
            (64u64 << 20, 1024 * 1024, false),
        ] {
            let (low, backup) = gpt_fixture(capacity, partition, msr);
            let c = probe_gpt(capacity, &low, &backup);
            assert!(c.recognized);
            assert!(c.probe_bytes < 96 * 1024);
            assert_eq!(c.classify(partition as u64 + 25 * 4096, 4096), "ntfs_log");
            assert_eq!(c.classify(partition as u64 + 80 * 4096, 4096), "data");
            assert!(c.needs_refresh(1024, 4096));
            assert!(c.needs_refresh(capacity - 512, 512));
            assert!(c.needs_refresh(partition as u64, 4096));
            assert_eq!(
                c.classify(PARTITION + 25 * 4096, 4096),
                if msr { "data" } else { "ntfs_log" }
            );
        }
    }
    #[test]
    fn gpt_crc_copy_bounds_and_unsupported_types_fail_unclassified() {
        let capacity = 8u64 << 40;
        for case in 0..10 {
            let (mut low, mut backup) = gpt_fixture(capacity, 3 * 1024 * 1024, true);
            match case {
                0 => low[528] ^= 1,
                1 => backup[16400] ^= 1,
                2 => low[1200] ^= 1,
                3 => backup[176] ^= 1,
                4 => {
                    low[1024..1040].fill(19);
                    rewrite_gpt_table(&mut low, &mut backup);
                }
                5 => {
                    low[1184..1192].copy_from_slice(&6144u64.to_le_bytes());
                    low[1192..1200].copy_from_slice(&7000u64.to_le_bytes());
                    rewrite_gpt_table(&mut low, &mut backup);
                }
                6 => {
                    low[1056..1064].copy_from_slice(&6145u64.to_le_bytes());
                    rewrite_gpt_table(&mut low, &mut backup);
                }
                7 => {
                    low[1064..1072].copy_from_slice(&(capacity / 512).to_le_bytes());
                    rewrite_gpt_table(&mut low, &mut backup);
                }
                8 => {
                    low[584..592].copy_from_slice(&u64::MAX.to_le_bytes());
                    header_crc(&mut low[512..1024]);
                }
                9 => {
                    backup[16440] ^= 1;
                    header_crc(&mut backup[16384..]);
                }
                _ => unreachable!(),
            }
            let c = probe_gpt(capacity, &low, &backup);
            assert!(!c.recognized, "case {case}");
            assert_eq!(c.classify(3 * 1024 * 1024 + 25 * 4096, 4096), "data");
            assert!(c.needs_refresh(capacity - 512, 512));
        }
    }
    #[test]
    fn gpt_unformatted_partition_watches_discovered_boot_and_mbr_checks_length() {
        let capacity = 8u64 << 40;
        let (mut low, backup) = gpt_fixture(capacity, 3 * 1024 * 1024, true);
        low[3 * 1024 * 1024 + 3] = 0;
        let c = probe_gpt(capacity, &low, &backup);
        assert!(!c.recognized);
        assert!(c.needs_refresh(3 * 1024 * 1024, 4096));
        let mut disk = fixture();
        disk[458..462].copy_from_slice(&u32::MAX.to_le_bytes());
        let c = Classifier::probe(disk.len() as u64, |at, out| {
            out.copy_from_slice(&disk[at as usize..at as usize + out.len()]);
            Ok(())
        })
        .unwrap();
        assert!(!c.recognized);
    }
    #[test]
    fn probe_bounded_metadata_and_unknown_fallback() {
        let disk = fixture();
        let c = Classifier::probe(disk.len() as u64, |at, out| {
            out.copy_from_slice(&disk[at as usize..at as usize + out.len()]);
            Ok(())
        })
        .unwrap();
        assert!(c.recognized);
        assert!(c.probe_bytes < 32 * 1024);
        assert_eq!(c.classify(PARTITION + 4 * 4096, 4096), "ntfs_mft");
        assert_eq!(c.classify(PARTITION + 25 * 4096, 4096), "ntfs_log");
        assert_eq!(c.classify(PARTITION + 80 * 4096, 4096), "data");
        assert!(c.needs_refresh(PARTITION + 4 * 4096, 4096));
        assert!(!c.needs_refresh(PARTITION + 40 * 4096, 4096));
        let restored: Classifier =
            serde_json::from_slice(&serde_json::to_vec(&c).unwrap()).unwrap();
        assert_eq!(restored.classify(PARTITION + 25 * 4096, 4096), "ntfs_log");
    }
    #[test]
    fn sparse_and_negative_delta_runs() {
        let mut a = vec![0u8; 80];
        a[32..34].copy_from_slice(&64u16.to_le_bytes());
        a[24..32].copy_from_slice(&5u64.to_le_bytes());
        a[64..73].copy_from_slice(&[0x11, 2, 20, 0x01, 2, 0x11, 2, 0xfb, 0]);
        let r = runs(&a, 100).unwrap();
        assert_eq!(r[1].lcn, None);
        assert_eq!(r[2].lcn, Some(15));
        assert_eq!(r[2].vcn, 4);
        a[71] = 0x80;
        assert!(runs(&a, 100).is_err());
    }
    #[test]
    fn malformed_records_never_classify_arbitrary_ranges() {
        let mut r = record(&[attr_run(24, 8)]);
        r[510] ^= 1;
        assert!(fixup(&mut r, 512, b"FILE").is_err());
        let mut r = record(&[attr_run(24, 8)]);
        fixup(&mut r, 512, b"FILE").unwrap();
        r[60..64].copy_from_slice(&0xffff_fff8u32.to_le_bytes());
        assert!(attributes(&r).is_err());
        let mut a = attr_run(120, 16);
        assert!(runs(&a, 128).is_err());
        a[32..34].copy_from_slice(&65535u16.to_le_bytes());
        assert!(runs(&a, 128).is_err());
    }
    #[test]
    fn unformatted_disk_is_advisory_only() {
        let c = Classifier::probe(64 * 1024 * 1024, |_, out| {
            out.fill(0);
            Ok(())
        })
        .unwrap();
        assert!(!c.recognized);
        assert_eq!(c.classify(0, 4096), "data");
        assert!(c.needs_refresh(PARTITION, 4096));
        assert!(!c.needs_refresh(8 * 1024 * 1024, 4096));
    }
    #[test]
    fn usn_index_requires_verified_child_and_refreshes_only_metadata_records() {
        let mut disk = fixture();
        let name = named(11, "$UsnJrnl");
        let entry_size = (16 + name.len()).div_ceil(8) * 8;
        let mut index = vec![0u8; 32 + entry_size + 16];
        index[16..20].copy_from_slice(&16u32.to_le_bytes());
        index[20..24].copy_from_slice(&((16 + entry_size + 16) as u32).to_le_bytes());
        index[32..40].copy_from_slice(&24u64.to_le_bytes());
        index[40..42].copy_from_slice(&(entry_size as u16).to_le_bytes());
        index[42..44].copy_from_slice(&(name.len() as u16).to_le_bytes());
        index[48..48 + name.len()].copy_from_slice(&name);
        index[32 + entry_size + 8..32 + entry_size + 10].copy_from_slice(&16u16.to_le_bytes());
        index[32 + entry_size + 12..32 + entry_size + 14].copy_from_slice(&2u16.to_le_bytes());
        let mft = PARTITION as usize + 4 * 4096;
        disk[mft + 11 * 1024..mft + 12 * 1024]
            .copy_from_slice(&record(&[attr_resident(0x90, &index)]));
        disk[mft + 24 * 1024..mft + 25 * 1024]
            .copy_from_slice(&record(&[attr_resident(0x30, &name), attr_run(48, 4)]));
        let probe = |disk: &Vec<u8>| {
            Classifier::probe(disk.len() as u64, |at, out| {
                out.copy_from_slice(&disk[at as usize..at as usize + out.len()]);
                Ok(())
            })
            .unwrap()
        };
        let c = probe(&disk);
        assert_eq!(c.classify(PARTITION + 49 * 4096, 4096), "ntfs_usn");
        assert!(c.needs_refresh(mft as u64 + 24 * 1024, 1024));
        assert!(!c.needs_refresh(mft as u64 + 20 * 1024, 1024));
        disk[mft + 24 * 1024..mft + 25 * 1024].copy_from_slice(&record(&[
            attr_resident(0x30, &named(5, "$UsnJrnl")),
            attr_run(48, 4),
        ]));
        assert_eq!(probe(&disk).classify(PARTITION + 49 * 4096, 4096), "data");
    }
}
