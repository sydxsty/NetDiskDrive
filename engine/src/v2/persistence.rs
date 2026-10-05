use super::*;

impl Shared {
    pub(super) fn load_meta(&self, reference: MetaRef, kind: u8) -> Result<Vec<u8>> {
        if reference.empty()
            || reference.offset < OBJECT + 17 * PAGE as u64
            || !reference.offset.is_multiple_of(PAGE as u64)
            || reference.offset % OBJECT < 17 * PAGE as u64
            || reference
                .offset
                .checked_add(PAGE as u64)
                .filter(|e| *e <= self.file.len().unwrap_or(0))
                .is_none()
        {
            return Err(integrity("metadata reference outside container"));
        }
        let mut bytes = [0; PAGE];
        self.file.read(reference.offset, &mut bytes)?;
        if codec::hash(&bytes) != reference.hash {
            return Err(integrity("metadata reference hash"));
        }
        self.crypto.unframe(kind, reference.offset, &bytes)
    }
    pub(super) fn node(&self, reference: MetaRef, level: u8) -> Result<Arc<Node>> {
        if reference.empty() {
            return Ok(Arc::new(Node::empty(level)));
        }
        if let Some(node) = self
            .cache
            .lock()
            .map_err(|_| Error::Poisoned)?
            .nodes
            .get(&reference)
            .cloned()
        {
            return Ok(node);
        }
        let node = Arc::new(Node::decode(&self.load_meta(reference, codec::NODE)?)?);
        if matches!(&*node, Node::Leaf(_)) != (level == 0) {
            return Err(integrity("index level mismatch"));
        }
        let mut cache = self.cache.lock().map_err(|_| Error::Poisoned)?;
        if !cache.nodes.contains_key(&reference) {
            while cache.nodes.len() >= META_CACHE {
                if let Some(old) = cache.order.pop_front() {
                    cache.nodes.remove(&old);
                }
            }
            cache.order.push_back(reference);
            cache.nodes.insert(reference, node.clone());
        }
        Ok(node)
    }
    pub(super) fn lookup(&self, root: &Root, page: u64) -> Result<Option<PageRef>> {
        let mut reference = root.index;
        let mut level = root.depth;
        loop {
            if reference.empty() {
                return Ok(None);
            }
            match &*self.node(reference, level)? {
                Node::Leaf(pages) => return Ok(pages[(page & 31) as usize]),
                Node::Branch(children) => {
                    reference = children[((page >> (5 + 6 * (level - 1))) & 63) as usize];
                    level -= 1;
                }
            }
        }
    }
    pub(super) fn read_page(&self, root: &Root, page: u64) -> Result<Zeroizing<[u8; PAGE]>> {
        let mut bytes = Zeroizing::new([0; PAGE]);
        if let Some(reference) = self.lookup(root, page)? {
            if reference.object == 0 || reference.slot >= SLOTS {
                return Err(integrity("invalid data location"));
            }
            self.file.read(reference.offset(), bytes.as_mut())?;
            self.crypto.decode_page(page, reference, &mut bytes)?;
        }
        Ok(bytes)
    }
    pub(super) fn walk(
        &self,
        reference: MetaRef,
        level: u8,
        base: u64,
        visit: &mut impl FnMut(u64, PageRef) -> Result<()>,
    ) -> Result<()> {
        if reference.empty() {
            return Ok(());
        }
        match &*self.node(reference, level)? {
            Node::Leaf(pages) => {
                for (i, page) in pages.iter().enumerate() {
                    if let Some(page) = page {
                        visit(base + i as u64, *page)?;
                    }
                }
            }
            Node::Branch(children) => {
                for (i, child) in children.iter().enumerate() {
                    if !child.empty() {
                        self.walk(
                            *child,
                            level - 1,
                            base + ((i as u64) << (5 + 6 * (level - 1))),
                            visit,
                        )?;
                    }
                }
            }
        }
        Ok(())
    }
    pub(super) fn index_objects(
        &self,
        reference: MetaRef,
        level: u8,
        objects: &mut BTreeSet<u64>,
    ) -> Result<()> {
        if reference.empty() {
            return Ok(());
        }
        objects.insert(reference.offset / OBJECT);
        match &*self.node(reference, level)? {
            Node::Leaf(pages) => {
                for p in pages.iter().flatten() {
                    if p.object == 0
                        || p.slot >= SLOTS
                        || p.offset() + PAGE as u64 > self.file.len()?
                    {
                        return Err(integrity("missing referenced data page"));
                    }
                    let h = self.object_header(p.object)?;
                    if h.generation != p.object_generation || h.kind != 0 {
                        return Err(integrity("data object identity"));
                    }
                    objects.insert(p.object);
                }
            }
            Node::Branch(children) => {
                for p in children.iter() {
                    self.index_objects(*p, level - 1, objects)?;
                }
            }
        }
        Ok(())
    }
    pub(super) fn object_header(&self, number: u64) -> Result<ObjectHeader> {
        if number == 0
            || number
                .checked_mul(OBJECT)
                .and_then(|n| n.checked_add(OBJECT))
                .filter(|e| *e <= self.file.len().unwrap_or(0))
                .is_none()
        {
            return Err(integrity("object outside container"));
        }
        let mut frame = [0; PAGE];
        self.file.read(number * OBJECT, &mut frame)?;
        let header: ObjectHeader = serde_json::from_slice(&self.crypto.unframe(
            codec::OBJECT_HEADER,
            number * OBJECT,
            &frame,
        )?)?;
        if header.number != number {
            return Err(integrity("object header number"));
        }
        Ok(header)
    }
    pub(super) fn read_catalog(&self, mut reference: MetaRef) -> Result<Vec<Snapshot>> {
        let mut snapshots = Vec::new();
        let mut seen = BTreeSet::new();
        while !reference.empty() {
            if !seen.insert(reference.offset) || seen.len() > 1024 {
                return Err(integrity("snapshot catalog cycle"));
            }
            let page: Catalog =
                serde_json::from_slice(&self.load_meta(reference, codec::CATALOG)?)?;
            snapshots.extend(page.items);
            if snapshots.len() > 1024 {
                return Err(integrity("snapshot catalog limit"));
            }
            reference = page.next;
        }
        Ok(snapshots)
    }
    pub(super) fn validate_journal(&self, mut reference: MetaRef) -> Result<()> {
        let mut seen = BTreeSet::new();
        while !reference.empty() {
            if !seen.insert(reference.offset) || seen.len() > 1024 {
                return Err(integrity("journal cycle"));
            }
            let bytes = self.load_meta(reference, codec::JOURNAL)?;
            if bytes.len() < 42 {
                return Err(integrity("journal layout"));
            }
            let count = u16::from_le_bytes(bytes[40..42].try_into().unwrap()) as usize;
            if bytes.len() != 42 + count * 80 {
                return Err(integrity("journal entry length"));
            }
            reference = MetaRef::get(&bytes[..40]);
        }
        Ok(())
    }
    fn chain_objects(
        &self,
        mut reference: MetaRef,
        kind: u8,
        objects: &mut BTreeSet<u64>,
    ) -> Result<()> {
        let mut seen = BTreeSet::new();
        while !reference.empty() {
            if !seen.insert(reference.offset) {
                return Err(integrity("metadata chain cycle"));
            }
            objects.insert(reference.offset / OBJECT);
            let bytes = self.load_meta(reference, kind)?;
            reference = if kind == codec::CATALOG {
                serde_json::from_slice::<Catalog>(&bytes)?.next
            } else if kind == codec::SEALS {
                serde_json::from_slice::<SealCatalog>(&bytes)?.next
            } else {
                if bytes.len() < 40 {
                    return Err(integrity("journal chain"));
                }
                MetaRef::get(&bytes[..40])
            };
        }
        Ok(())
    }
    pub(super) fn reachable_roots(&self, roots: &[Option<Root>; 2]) -> Result<BTreeSet<u64>> {
        let mut objects = BTreeSet::new();
        for root in roots.iter().flatten() {
            self.cloud_reachable(root, &mut objects)?;
            self.index_objects(root.index, root.depth, &mut objects)?;
            self.chain_objects(root.catalog, codec::CATALOG, &mut objects)?;
            self.chain_objects(root.journal, codec::JOURNAL, &mut objects)?;
            self.chain_objects(root.seals, codec::SEALS, &mut objects)?;
            for tail in [root.data_tail, root.meta_tail].iter().flatten() {
                let h = self.object_header(tail.number)?;
                if h.generation != tail.generation {
                    return Err(integrity("root tail identity"));
                }
                objects.insert(tail.number);
            }
            for snapshot in self.read_catalog(root.catalog)? {
                self.index_objects(snapshot.index, snapshot.depth, &mut objects)?;
                self.chain_objects(snapshot.seals, codec::SEALS, &mut objects)?;
            }
        }
        Ok(objects)
    }
    pub(super) fn snapshot_objects(&self, snapshot: &Snapshot) -> Result<BTreeSet<u64>> {
        let mut objects = BTreeSet::new();
        self.index_objects(snapshot.index, snapshot.depth, &mut objects)?;
        Ok(objects)
    }
    pub(super) fn find_seal(
        &self,
        mut reference: MetaRef,
        generation: u64,
    ) -> Result<Option<Seal>> {
        let mut seen = BTreeSet::new();
        while !reference.empty() {
            if !seen.insert(reference.offset) {
                return Err(integrity("seal catalog cycle"));
            }
            let page: SealCatalog =
                serde_json::from_slice(&self.load_meta(reference, codec::SEALS)?)?;
            if let Some(seal) = page
                .items
                .into_iter()
                .find(|s| s.header.generation == generation)
            {
                return Ok(Some(seal));
            }
            reference = page.next;
        }
        Ok(None)
    }
    pub(super) fn find_seal_id(&self, mut reference: MetaRef, id: Uuid) -> Result<Option<Seal>> {
        let mut seen = BTreeSet::new();
        while !reference.empty() {
            if !seen.insert(reference.offset) {
                return Err(integrity("seal catalog cycle"));
            }
            let page: SealCatalog =
                serde_json::from_slice(&self.load_meta(reference, codec::SEALS)?)?;
            if let Some(seal) = page.items.into_iter().find(|s| s.header.id == id) {
                return Ok(Some(seal));
            }
            reference = page.next;
        }
        Ok(None)
    }
    pub(super) fn seal_snapshot(&self, snapshot: &Snapshot, root: &Root) -> Result<Vec<Seal>> {
        // One bounded 64-KiB descriptor buffer; source of truth is the committed
        // authenticated index, including the partially filled active object.
        let mut seals = Vec::new();
        for number in self.snapshot_objects(snapshot)? {
            let mut header = self.object_header(number)?;
            if self.find_seal(root.seals, header.generation)?.is_some() {
                continue;
            }
            let mut descriptors = Zeroizing::new(vec![0u8; 64 * 1024]);
            if header.kind == 0 {
                self.walk(snapshot.index, snapshot.depth, 0, &mut |page, r| {
                    if r.object == number {
                        header.used_slots = header.used_slots.max(r.slot + 1);
                        let p = &mut descriptors[r.slot as usize * 64..r.slot as usize * 64 + 64];
                        p[..8].copy_from_slice(&page.to_le_bytes());
                        p[8..16].copy_from_slice(&r.version.to_le_bytes());
                        p[16..40].copy_from_slice(&r.nonce);
                        p[40..56].copy_from_slice(&r.tag);
                        p[56] = 1;
                    }
                    Ok(())
                })?;
            } else {
                header.used_slots =
                    self.metadata_highwater(snapshot.index, snapshot.depth, number)?;
            }
            for tail in [root.data_tail, root.meta_tail].iter().flatten() {
                if tail.number == number {
                    header.used_slots = header.used_slots.max(tail.slot);
                }
            }
            let aad = format!(
                "OverlayDisk object descriptors {} {}",
                self.config.id,
                object_id(header)
            );
            let (nonce, tag) = self.crypto.seal_blob(aad.as_bytes(), &mut descriptors)?;
            header.descriptor_nonce = nonce;
            header.descriptor_tag = tag;
            self.file
                .write(number * OBJECT + PAGE as u64, &descriptors)?;
            header.sealed = true;
            let mut nonce = [0; 24];
            OsRng.fill_bytes(&mut nonce);
            seals.push(Seal {
                header,
                nonce,
                seq: snapshot.seq,
            });
        }
        self.file.sync()?;
        crash_point("after-descriptors");
        Ok(seals)
    }
    fn metadata_highwater(&self, reference: MetaRef, level: u8, number: u64) -> Result<u64> {
        if reference.empty() {
            return Ok(0);
        }
        let mut maximum = if reference.offset / OBJECT == number {
            (reference.offset % OBJECT - 17 * PAGE as u64) / PAGE as u64 + 1
        } else {
            0
        };
        if let Node::Branch(children) = &*self.node(reference, level)? {
            for child in children.iter() {
                maximum = maximum.max(self.metadata_highwater(*child, level - 1, number)?);
            }
        }
        Ok(maximum)
    }
    pub(super) fn flush(&self) -> Result<()> {
        let _epoch = self.epoch.read().map_err(|_| Error::Poisoned)?;
        let _commit = self.commit.lock().map_err(|_| Error::Poisoned)?;
        let mut pending = {
            let mut state = self.state.lock().map_err(|_| Error::Poisoned)?;
            check_failure(&state)?;
            if state.dirty.is_empty() && state.trims.is_empty() {
                return Ok(());
            }
            let dirty = std::mem::replace(&mut state.dirty, Arc::new(BTreeMap::new()));
            let trims = std::mem::take(&mut state.trims);
            state.inflight = Some(Frozen {
                dirty: dirty.clone(),
                trims: trims.clone(),
            });
            State {
                current: state.current.clone(),
                roots: state.roots.clone(),
                dirty,
                trims,
                inflight: None,
                free: std::mem::take(&mut state.free),
                snapshots: state.snapshots.clone(),
                seal_updates: Vec::new(),
                pending_snapshot: None,
                failure: None,
                cloud: state.cloud.clone(),
                maintenance: state.maintenance,
            }
        };
        let result = self.persist_inner(&mut pending, false);
        let mut state = self.state.lock().map_err(|_| Error::Poisoned)?;
        match result {
            Ok(()) => {
                state.current = pending.current;
                state.roots = pending.roots;
                state.free = pending.free;
                state.inflight = None;
                Ok(())
            }
            Err(error) => {
                state.failure = Some(error.to_string());
                Err(error)
            }
        }
    }
    pub(super) fn persist(&self, state: &mut State, catalog_changed: bool) -> Result<()> {
        let result = self.persist_inner(state, catalog_changed);
        if let Err(error) = &result {
            state.failure = Some(error.to_string());
        }
        result
    }
    fn persist_inner(&self, state: &mut State, catalog_changed: bool) -> Result<()> {
        let mut root = state.current.clone();
        root.seq = root
            .seq
            .checked_add(1)
            .ok_or_else(|| invalid("commit sequence exhausted"))?;
        if (!state.dirty.is_empty() || !state.trims.is_empty()) && !state.maintenance {
            root.data_generation = root
                .data_generation
                .checked_add(1)
                .ok_or_else(|| invalid("data generation exhausted"))?;
        }
        let mut writer = Builder {
            shared: self,
            root,
            free: &mut state.free,
            data: Vec::new(),
            metadata: Vec::new(),
        };
        let mut updates = Vec::with_capacity(state.dirty.len());
        let mut nonces = vec![0u8; state.dirty.len() * 24];
        OsRng.fill_bytes(&mut nonces);
        for (i, (&page, bytes)) in state.dirty.iter().enumerate() {
            let old = if trimmed(&state.trims, page) {
                None
            } else {
                self.lookup(&state.current, page)?
            };
            let new = if bytes.iter().all(|b| *b == 0) {
                None
            } else {
                let nonce = nonces[i * 24..i * 24 + 24].try_into().unwrap();
                Some(writer.data_page(page, nonce, bytes)?)
            };
            if old.is_some() && new.is_none() {
                writer.root.allocated_pages -= 1;
            } else if old.is_none() && new.is_some() {
                writer.root.allocated_pages += 1;
            }
            if old.is_some() || new.is_some() {
                updates.push((page, new));
            }
        }
        writer.flush_data()?;
        self.file.sync()?;
        crash_point("after-data");
        #[cfg(test)]
        {
            let pause = self.pause_after_data.lock().unwrap().take();
            if let Some(pause) = pause {
                pause.wait();
                pause.wait();
            }
        }
        let (trimmed_index, deleted) = if state
            .trims
            .get(&0)
            .is_some_and(|end| *end >= self.config.capacity_bytes.div_ceil(PAGE as u64))
        {
            (MetaRef::default(), state.current.allocated_pages)
        } else {
            self.apply_trims(
                writer.root.index,
                writer.root.depth,
                0,
                &state.trims,
                &mut writer,
            )?
        };
        writer.root.allocated_pages = writer
            .root
            .allocated_pages
            .checked_sub(deleted)
            .ok_or_else(|| integrity("allocation count underflow"))?;
        writer.flush_metadata()?;
        let index = self.apply(trimmed_index, writer.root.depth, &updates, &mut writer)?;
        writer.root.index = index;
        let mut journal = MetaRef::default();
        for chunk in updates.chunks(48).rev() {
            let mut payload = vec![0u8; 42 + chunk.len() * 80];
            journal.put(&mut payload[..40]);
            payload[40..42].copy_from_slice(&(chunk.len() as u16).to_le_bytes());
            for (i, (page, reference)) in chunk.iter().enumerate() {
                let out = &mut payload[42 + i * 80..42 + (i + 1) * 80];
                out[..8].copy_from_slice(&page.to_le_bytes());
                if let Some(reference) = reference {
                    reference.put(&mut out[8..]);
                }
            }
            journal = writer.meta(codec::JOURNAL, &payload)?;
        }
        writer.root.journal = journal;
        for chunk in state.seal_updates.chunks(4) {
            let payload = serde_json::to_vec(&SealCatalog {
                next: writer.root.seals,
                items: chunk.to_vec(),
            })?;
            writer.root.seals = writer.meta(codec::SEALS, &payload)?;
        }
        if let Some(id) = &state.pending_snapshot {
            if let Some(snapshot) = state.snapshots.iter_mut().find(|s| &s.id == id) {
                snapshot.seals = writer.root.seals;
            }
        }
        if catalog_changed {
            let mut catalog = MetaRef::default();
            for chunk in state.snapshots.chunks(8).rev() {
                let payload = serde_json::to_vec(&Catalog {
                    next: catalog,
                    items: chunk.to_vec(),
                })?;
                catalog = writer.meta(codec::CATALOG, &payload)?;
            }
            writer.root.catalog = catalog;
        }
        Self::persist_cloud(&mut writer, &state.cloud)?;
        writer.flush_metadata()?;
        self.file.sync()?;
        crash_point("after-metadata");
        let root = writer.root;
        let slot = (root.seq % 2) as usize;
        let offset = (slot as u64 + 1) * PAGE as u64;
        self.file.write(
            offset,
            &self
                .crypto
                .frame(codec::ROOT, root.seq, offset, &serde_json::to_vec(&root)?)?,
        )?;
        crash_point("after-root-write");
        self.file.sync()?;
        crash_point("after-root-sync");
        let mirror = (2 - slot) as u64 * PAGE as u64;
        self.file.write(
            mirror,
            &self
                .crypto
                .frame(codec::ROOT, root.seq, mirror, &serde_json::to_vec(&root)?)?,
        )?;
        self.file.sync()?;
        crash_point("after-mirror-sync");
        state.roots = [Some(root.clone()), Some(root.clone())];
        state.current = root;
        state.dirty = Arc::new(BTreeMap::new());
        state.trims.clear();
        state.seal_updates.clear();
        state.pending_snapshot = None;
        state.cloud.log_events = if state.current.cloud_log.empty() {
            0
        } else {
            state.cloud.log_events + state.cloud.pending.len()
        };
        state.cloud.pending.clear();
        state.cloud.force_checkpoint = false;
        Ok(())
    }
    fn count_pages(&self, reference: MetaRef, level: u8) -> Result<u64> {
        if reference.empty() {
            return Ok(0);
        }
        match &*self.node(reference, level)? {
            Node::Leaf(p) => Ok(p.iter().flatten().count() as u64),
            Node::Branch(children) => {
                let mut n = 0;
                for child in children.iter() {
                    n += self.count_pages(*child, level - 1)?;
                }
                Ok(n)
            }
        }
    }
    fn apply_trims(
        &self,
        reference: MetaRef,
        level: u8,
        base: u64,
        ranges: &BTreeMap<u64, u64>,
        writer: &mut Builder<'_>,
    ) -> Result<(MetaRef, u64)> {
        if reference.empty() || ranges.is_empty() {
            return Ok((reference, 0));
        }
        let end = base + (1u64 << (5 + 6 * level));
        if !ranges.range(..end).any(|(_, e)| *e > base) {
            return Ok((reference, 0));
        }
        if ranges
            .range(..=base)
            .next_back()
            .is_some_and(|(_, e)| *e >= end)
        {
            return Ok((MetaRef::default(), self.count_pages(reference, level)?));
        }
        let mut node = (*self.node(reference, level)?).clone();
        let mut deleted = 0;
        match &mut node {
            Node::Leaf(pages) => {
                for (i, p) in pages.iter_mut().enumerate() {
                    if p.is_some() && trimmed(ranges, base + i as u64) {
                        *p = None;
                        deleted += 1;
                    }
                }
            }
            Node::Branch(children) => {
                for (i, child) in children.iter_mut().enumerate() {
                    let (next, n) = self.apply_trims(
                        *child,
                        level - 1,
                        base + ((i as u64) << (5 + 6 * (level - 1))),
                        ranges,
                        writer,
                    )?;
                    *child = next;
                    deleted += n;
                }
            }
        }
        if deleted == 0 {
            Ok((reference, 0))
        } else if node.is_empty() {
            Ok((MetaRef::default(), deleted))
        } else {
            Ok((writer.meta(codec::NODE, &node.encode())?, deleted))
        }
    }
    fn apply(
        &self,
        reference: MetaRef,
        level: u8,
        updates: &[(u64, Option<PageRef>)],
        writer: &mut Builder<'_>,
    ) -> Result<MetaRef> {
        if updates.is_empty() {
            return Ok(reference);
        }
        let mut node = (*self.node(reference, level)?).clone();
        match &mut node {
            Node::Leaf(pages) => {
                for (page, r) in updates {
                    pages[(page & 31) as usize] = *r;
                }
            }
            Node::Branch(children) => {
                let shift = 5 + 6 * (level - 1);
                let mut start = 0;
                while start < updates.len() {
                    let index = ((updates[start].0 >> shift) & 63) as usize;
                    let mut end = start + 1;
                    while end < updates.len() && ((updates[end].0 >> shift) & 63) as usize == index
                    {
                        end += 1;
                    }
                    children[index] =
                        self.apply(children[index], level - 1, &updates[start..end], writer)?;
                    start = end;
                }
            }
        }
        if node.is_empty() {
            Ok(MetaRef::default())
        } else {
            writer.meta(codec::NODE, &node.encode())
        }
    }
}

impl Shared {
    pub(super) fn append_cloud_blob(&self, state: &mut State, bytes: &[u8]) -> Result<MetaRef> {
        let mut writer = Builder {
            shared: self,
            root: state.current.clone(),
            free: &mut state.free,
            data: Vec::new(),
            metadata: Vec::new(),
        };
        let result = match writer
            .cloud_blob(bytes)
            .and_then(|r| writer.flush_metadata().map(|()| r))
        {
            Ok(r) => r,
            Err(e) => {
                state.failure = Some(e.to_string());
                return Err(e);
            }
        };
        state.current.meta_tail = writer.root.meta_tail;
        state.current.next_generation = writer.root.next_generation;
        Ok(result)
    }
    fn persist_cloud(writer: &mut Builder<'_>, db: &cloud::CloudDb) -> Result<()> {
        if db.pending.is_empty() && !db.force_checkpoint {
            return Ok(());
        }
        if db.force_checkpoint
            || writer.root.cloud_checkpoint.empty()
            || db.log_events.saturating_add(db.pending.len()) >= 1024usize.max(db.values.len())
        {
            writer.root.cloud_checkpoint = writer.cloud_checkpoint(&db.values)?;
            writer.root.cloud_log = MetaRef::default();
        } else {
            let log = cloud::CloudLog {
                previous: writer.root.cloud_log,
                events: db.pending.clone(),
            };
            writer.root.cloud_log = writer.cloud_blob(&serde_json::to_vec(&log)?)?;
        }
        Ok(())
    }
}

struct Builder<'a> {
    shared: &'a Shared,
    root: Root,
    free: &'a mut Vec<u64>,
    data: Vec<(u64, [u8; PAGE])>,
    metadata: Vec<(u64, [u8; PAGE])>,
}
impl Builder<'_> {
    fn cloud_checkpoint(
        &mut self,
        values: &BTreeMap<String, serde_json::Value>,
    ) -> Result<MetaRef> {
        // Serialize only one bounded chunk at a time, irrespective of the total
        // live object catalog. Every chunk links to its predecessor by hash.
        let budget = cloud::CHECKPOINT_CHUNK_BYTES - 4096;
        let mut previous = MetaRef::default();
        let mut entries = Vec::new();
        let mut used = 0usize;
        for (key, value) in values {
            let entry = encode_cloud_bounded(&(key, value), budget)?;
            if !entries.is_empty() && used + entry.len() + 1 > budget {
                let chunk = cloud::CloudCheckpoint {
                    checkpoint_version: 1,
                    previous,
                    entries: std::mem::take(&mut entries),
                };
                previous = self.cloud_blob(&encode_cloud_bounded(
                    &chunk,
                    cloud::CHECKPOINT_CHUNK_BYTES,
                )?)?;
                used = 0;
            }
            used += entry.len() + 1;
            entries.push((key.clone(), value.clone()));
        }
        if !entries.is_empty() || previous.empty() {
            let chunk = cloud::CloudCheckpoint {
                checkpoint_version: 1,
                previous,
                entries,
            };
            previous = self.cloud_blob(&encode_cloud_bounded(
                &chunk,
                cloud::CHECKPOINT_CHUNK_BYTES,
            )?)?;
        }
        Ok(previous)
    }

    fn cloud_blob(&mut self, bytes: &[u8]) -> Result<MetaRef> {
        if bytes.len() > 256 * 1024 * 1024 {
            return Err(invalid("cloud metadata record exceeds bounded limit"));
        }
        let mut next = MetaRef::default();
        for chunk in bytes.chunks(codec::PAYLOAD - 42).rev() {
            let mut part = vec![0; 42 + chunk.len()];
            next.put(&mut part[..40]);
            part[40..42].copy_from_slice(&(chunk.len() as u16).to_le_bytes());
            part[42..].copy_from_slice(chunk);
            next = self.meta(codec::CLOUD_BLOB, &part)?;
        }
        Ok(next)
    }

    fn allocate(&mut self, kind: u8) -> Result<Tail> {
        let number = if let Some(number) = self.free.pop() {
            number
        } else {
            let number = self.shared.file.len()? / OBJECT;
            self.shared.file.grow((number + 1) * OBJECT)?;
            self.free
                .extend((number + 1..self.shared.file.len()? / OBJECT).rev());
            number
        };
        if number == 0 || (number + 1) * OBJECT > self.shared.file.len()? {
            return Err(integrity("allocator free extent outside container"));
        }
        let generation = self.root.next_generation;
        self.root.next_generation = self
            .root
            .next_generation
            .checked_add(1)
            .ok_or_else(|| invalid("object generation exhausted"))?;
        // Reuse is allowed only for objects outside both roots and all snapshots.
        // Metadata is initialized; payload pages are written only to their final
        // COW positions. Logical exports zero the unused tail of reused objects.
        self.shared
            .file
            .write(number * OBJECT, &vec![0; 17 * PAGE])?;
        let header = ObjectHeader {
            id: Uuid::new_v4(),
            number,
            generation,
            kind,
            sealed: false,
            used_slots: 0,
            descriptor_nonce: [0; 24],
            descriptor_tag: [0; 16],
        };
        self.shared.file.write(
            number * OBJECT,
            &self.shared.crypto.frame(
                codec::OBJECT_HEADER,
                self.root.seq,
                number * OBJECT,
                &serde_json::to_vec(&header)?,
            )?,
        )?;
        Ok(Tail {
            number,
            generation,
            slot: 0,
        })
    }
    fn reserve(&mut self, kind: u8) -> Result<(Tail, u64)> {
        let mut tail = if kind == 0 {
            self.root.data_tail
        } else {
            self.root.meta_tail
        };
        if tail.is_none_or(|t| t.slot >= SLOTS) {
            tail = Some(self.allocate(kind)?);
        }
        let mut tail = tail.unwrap();
        let slot = tail.slot;
        tail.slot += 1;
        if kind == 0 {
            self.root.data_tail = Some(tail);
        } else {
            self.root.meta_tail = Some(tail);
        }
        Ok((tail, slot))
    }
    fn data_page(&mut self, page: u64, nonce: [u8; 24], bytes: &[u8; PAGE]) -> Result<PageRef> {
        let (tail, slot) = self.reserve(0)?;
        let (data, tag) = self
            .shared
            .crypto
            .encode_page(page, self.root.seq, nonce, bytes)?;
        let reference = PageRef {
            object: tail.number,
            object_generation: tail.generation,
            slot,
            version: self.root.seq,
            nonce,
            tag,
        };
        self.data.push((reference.offset(), data));
        if self.data.len() >= 256 {
            self.flush_data()?;
        }
        Ok(reference)
    }
    fn meta(&mut self, kind: u8, bytes: &[u8]) -> Result<MetaRef> {
        let (tail, slot) = self.reserve(1)?;
        let offset = tail.number * OBJECT + 17 * PAGE as u64 + slot * PAGE as u64;
        let frame = self
            .shared
            .crypto
            .frame(kind, self.root.seq, offset, bytes)?;
        let reference = MetaRef {
            offset,
            hash: codec::hash(&frame),
        };
        self.metadata.push((offset, frame));
        if self.metadata.len() >= 256 {
            self.flush_metadata()?;
        }
        Ok(reference)
    }
    fn flush_data(&mut self) -> Result<()> {
        write_coalesced(&self.shared.file, &mut self.data)
    }
    fn flush_metadata(&mut self) -> Result<()> {
        write_coalesced(&self.shared.file, &mut self.metadata)
    }
}

fn encode_cloud_bounded<T: Serialize>(value: &T, limit: usize) -> Result<Vec<u8>> {
    struct Buffer {
        bytes: Vec<u8>,
        limit: usize,
    }
    impl std::io::Write for Buffer {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            if bytes.len() > self.limit.saturating_sub(self.bytes.len()) {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidData,
                    "cloud metadata entry exceeds chunk limit",
                ));
            }
            self.bytes.extend_from_slice(bytes);
            Ok(bytes.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }
    let mut output = Buffer {
        bytes: Vec::new(),
        limit,
    };
    serde_json::to_writer(&mut output, value)?;
    Ok(output.bytes)
}
fn write_coalesced(file: &Device, pages: &mut Vec<(u64, [u8; PAGE])>) -> Result<()> {
    let mut start = 0;
    while start < pages.len() {
        let mut end = start + 1;
        while end < pages.len() && pages[end].0 == pages[end - 1].0 + PAGE as u64 {
            end += 1;
        }
        let mut bytes = Zeroizing::new(Vec::with_capacity((end - start) * PAGE));
        for (_, page) in &pages[start..end] {
            bytes.extend_from_slice(page);
        }
        file.write(pages[start].0, &bytes)?;
        start = end;
    }
    pages.clear();
    Ok(())
}

#[cfg(test)]
fn crash_point(stage: &str) {
    if std::env::var("ODV2_CRASH_STAGE").ok().as_deref() == Some(stage) {
        println!("ODV2-CRASH-READY");
        use std::io::Write;
        std::io::stdout().flush().unwrap();
        loop {
            thread::park_timeout(Duration::from_secs(1));
        }
    }
}
#[cfg(not(test))]
fn crash_point(_stage: &str) {}
