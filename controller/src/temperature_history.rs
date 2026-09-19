use serde::Serialize;
use std::{
    collections::VecDeque,
    time::{SystemTime, UNIX_EPOCH},
};
pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
pub fn age_ms(at: u64, now: u64) -> Option<u64> {
    if at == 0 { None } else { now.checked_sub(at) }
}
#[derive(Clone, Serialize)]
pub struct Sample {
    pub at_ms: u64,
    pub cpu: Option<f32>,
    pub gpu: Option<f32>,
}
#[derive(Default)]
pub struct History {
    samples: VecDeque<Sample>,
}
impl History {
    pub fn push(&mut self, at_ms: u64, cpu: Option<f32>, gpu: Option<f32>) {
        if self.samples.back().is_some_and(|s| s.at_ms > at_ms) {
            self.samples.clear();
        }
        if self.samples.back().is_some_and(|s| s.at_ms == at_ms) {
            self.samples.pop_back();
        }
        self.samples.push_back(Sample {
            at_ms,
            cpu: cpu.filter(|v| v.is_finite()),
            gpu: gpu.filter(|v| v.is_finite()),
        });
        while self
            .samples
            .front()
            .is_some_and(|s| at_ms.saturating_sub(s.at_ms) > 120_000)
            || self.samples.len() > 121
        {
            self.samples.pop_front();
        }
    }
    pub fn recent(&self, now: u64) -> Vec<Sample> {
        self.samples
            .iter()
            .filter(|s| age_ms(s.at_ms, now).is_some_and(|age| age <= 120_000))
            .cloned()
            .collect()
    }
    pub fn latest(&self) -> Option<&Sample> {
        self.samples.back()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn history_is_bounded_and_keeps_missing_samples() {
        let mut h = History::default();
        for i in 1..200 {
            h.push(i * 2000, Some(50.0), None);
        }
        assert_eq!(h.recent(398000).len(), 61);
        h.push(400000, None, Some(f32::NAN));
        assert!(h.latest().unwrap().cpu.is_none());
        assert!(h.latest().unwrap().gpu.is_none());
        assert!(h.recent(600000).is_empty());
    }
    #[test]
    fn clock_reset_and_unknown_age_do_not_invent_freshness() {
        let mut h = History::default();
        h.push(200, Some(50.0), None);
        h.push(100, None, None);
        assert_eq!(h.recent(100).len(), 1);
        assert_eq!(age_ms(200, 100), None);
        assert_eq!(age_ms(0, 100), None);
    }
}
