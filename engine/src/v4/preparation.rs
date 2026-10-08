//! Volatile preparation scheduling and task-attributed I/O. No diagnostic
//! state is part of the authenticated container or its commit protocol.
use super::io::ScopedIoCounts;
use super::*;
use serde_json::{json, Value};

#[derive(Default)]
struct Stage {
    io: ScopedIoCounts,
    micros: u64,
    steps: u64,
}
impl Stage {
    fn value(&self) -> Value {
        let mut value = serde_json::to_value(self.io).expect("I/O counters serialize");
        value["duration_ms"] = json!(self.micros / 1000);
        value["steps"] = json!(self.steps);
        value
    }
}

pub(super) struct Runtime {
    job_id: String,
    stages: BTreeMap<String, Stage>,
    cache: BTreeMap<String, u64>,
    pub leaf_groups: usize,
    active_stage: String,
}
impl Default for Runtime {
    fn default() -> Self {
        Self { job_id: String::new(), stages: BTreeMap::new(), cache: BTreeMap::new(), leaf_groups: 128, active_stage: String::new() }
    }
}
impl Runtime {
    pub fn begin(&mut self, job_id: &str, stage: &str) {
        if self.job_id != job_id {
            *self = Self { job_id: job_id.into(), ..Self::default() };
        }
        self.active_stage = stage.into();
    }
    pub fn budget(&self, maximum: usize, foreground_waiting: bool) -> usize {
        self.leaf_groups.min(maximum).min(if foreground_waiting { 128 } else { 512 }).max(1)
    }
    pub fn record(&mut self, stage: &str, io: ScopedIoCounts, elapsed: Duration,
        cache_before: &BTreeMap<String, u64>, cache_after: BTreeMap<String, u64>, foreground_waiting: bool) {
        let entry = self.stages.entry(stage.into()).or_default();
        entry.io.add(io);
        entry.micros = entry.micros.saturating_add(elapsed.as_micros().min(u64::MAX as u128) as u64);
        entry.steps += 1;
        for (key, value) in cache_after {
            if matches!(key.as_str(), "prepare_cache_limit_bytes" | "prepare_cache_used_bytes" | "prepare_cache_peak_bytes" | "prepare_cache_entries") {
                self.cache.insert(key, value);
            } else {
                let previous = cache_before.get(&key).copied().unwrap_or(0);
                *self.cache.entry(key).or_default() += value.saturating_sub(previous);
            }
        }
        if stage == "indexing" {
            self.observe_batch(elapsed, foreground_waiting);
        }
        self.active_stage.clear();
    }
    pub fn release_cache(&mut self) {
        self.cache.insert("prepare_cache_used_bytes".into(), 0);
        self.active_stage.clear();
    }
    fn observe_batch(&mut self, elapsed: Duration, foreground_waiting: bool) {
        if foreground_waiting {
            self.leaf_groups = 128;
        } else if elapsed > Duration::from_millis(100) {
            self.leaf_groups = (self.leaf_groups / 2).max(128);
        } else if elapsed <= Duration::from_millis(25) {
            self.leaf_groups = (self.leaf_groups * 2).min(512);
        }
    }
    pub fn value(&self, job_id: Option<&str>) -> Value {
        if self.job_id.is_empty() || job_id.is_some_and(|id| id != self.job_id) {
            return Value::Null;
        }
        let mut total = Stage::default();
        let mut stages = serde_json::Map::new();
        for (name, stage) in &self.stages {
            total.io.add(stage.io);
            total.micros += stage.micros;
            total.steps += stage.steps;
            stages.insert(name.clone(), stage.value());
        }
        json!({"job_id":self.job_id,"scope":"process_session","active_stage":self.active_stage,
            "stages":stages,"totals":total.value(),"cache":self.cache,"batch_leaf_groups":self.leaf_groups})
    }
}

impl Volume {
    pub(super) fn preparation_diagnostics(&self, job_id: Option<&str>) -> Value {
        self.shared.preparation.lock().map(|r| r.value(job_id)).unwrap_or(Value::Null)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn batch_budget_only_grows_when_fast_and_yields_to_foreground() {
        let mut runtime = Runtime::default();
        assert_eq!(runtime.budget(512, false), 128);
        runtime.observe_batch(Duration::from_millis(25), false);
        assert_eq!(runtime.budget(512, false), 256);
        runtime.observe_batch(Duration::from_millis(20), false);
        assert_eq!(runtime.budget(512, false), 512);
        assert_eq!(runtime.budget(64, false), 64);
        assert_eq!(runtime.budget(512, true), 128);
        runtime.observe_batch(Duration::from_millis(101), false);
        assert_eq!(runtime.leaf_groups, 256);
        runtime.observe_batch(Duration::ZERO, true);
        assert_eq!(runtime.leaf_groups, 128);
    }
    #[test]
    fn task_counters_exclude_between_step_activity_and_reset_on_new_job() {
        let mut runtime = Runtime::default();
        runtime.begin("job-a", "indexing");
        runtime.record("indexing", ScopedIoCounts { local_read_bytes: 4096, flush_count: 3, ..Default::default() },
            Duration::from_millis(3), &BTreeMap::from([("prepare_cache_hits".into(), 100)]),
            BTreeMap::from([("prepare_cache_hits".into(), 106), ("prepare_cache_used_bytes".into(), 8192)]), false);
        let value = runtime.value(Some("job-a"));
        assert_eq!(value["cache"]["prepare_cache_hits"], 6);
        assert_eq!(value["totals"]["local_read_bytes"], 4096);
        assert_eq!(value["totals"]["flush_count"], 3);
        assert!(runtime.value(Some("other")).is_null());
        runtime.begin("job-b", "freeze");
        assert_eq!(runtime.value(None)["totals"]["local_read_bytes"], 0);
    }
}
