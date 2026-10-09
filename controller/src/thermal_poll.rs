use crate::{
    polling::{ReadKind, Schedule},
    temperature_history::{History, now_ms},
};
use std::sync::{Arc, Mutex};

use crate::system::thermal::{
    CpuTempSource, ThermalReader, ThermalSnapshot, ThermalSpikeFilterState,
    filter_thermal_raw_snapshot,
};

pub fn spawn_thermal_poller(
    schedule: Arc<Schedule>,
    shared: Arc<Mutex<ThermalSnapshot>>,
    history: Arc<Mutex<History>>,
    evidence: Arc<Mutex<ThermalEvidence>>,
) {
    std::thread::spawn(move || {
        #[cfg(target_os = "windows")]
        let mut reader = ThermalReader::new();
        let mut spike_state = ThermalSpikeFilterState::default();
        let mut last_cpu_source: Option<CpuTempSource> = None;
        let mut generation = u64::MAX;
        let mut last_read = now_ms();
        loop {
            schedule.wait(ReadKind::Thermal, &mut generation);

            #[cfg(target_os = "windows")]
            let raw = reader.read_snapshot();
            #[cfg(not(target_os = "windows"))]
            let raw = crate::system::thermal::ThermalRawSnapshot {
                snapshot: ThermalSnapshot::default(),
                cpu_source: None,
                gpu_source: None,
            };

            let mut guard = match shared.lock() {
                Ok(guard) => guard,
                Err(_) => continue,
            };
            let measured = now_ms();
            if measured.saturating_sub(last_read) > 15_000 || measured < last_read {
                spike_state = ThermalSpikeFilterState::default();
                last_cpu_source = None;
                *guard = ThermalSnapshot::default();
            }
            last_read = measured;
            let filtered =
                filter_thermal_raw_snapshot(&guard, raw.clone(), &mut spike_state, &mut last_cpu_source);
            if let Ok(mut h) = history.lock() {
                h.push(now_ms(), filtered.cpu_avg_c, filtered.gpu_avg_c);
            }
            if let Ok(mut e) = evidence.lock() {
                *e = ThermalEvidence::new(measured, &raw, &filtered);
            }
            *guard = filtered;
        }
    });
}

pub fn read_shared_thermal(shared: &Arc<Mutex<ThermalSnapshot>>) -> ThermalSnapshot {
    shared.lock().map(|guard| guard.clone()).unwrap_or_default()
}

#[derive(Debug, Clone, Default, serde::Serialize)]
pub struct ThermalEvidence {
    pub at_ms: u64,
    pub cpu_source: Option<CpuTempSource>,
    pub gpu_source: Option<&'static str>,
    pub cpu_raw_c: Option<f32>,
    pub gpu_raw_c: Option<f32>,
    pub cpu_filtered_c: Option<f32>,
    pub gpu_filtered_c: Option<f32>,
}
impl ThermalEvidence {
    fn new(at_ms: u64, raw: &crate::system::thermal::ThermalRawSnapshot, filtered: &ThermalSnapshot) -> Self {
        Self { at_ms, cpu_source: raw.cpu_source, gpu_source: raw.gpu_source,
            cpu_raw_c: raw.snapshot.cpu_avg_c, gpu_raw_c: raw.snapshot.gpu_avg_c,
            cpu_filtered_c: filtered.cpu_avg_c, gpu_filtered_c: filtered.gpu_avg_c }
    }
    pub fn report(&self, now: u64) -> serde_json::Value {
        let mut report = serde_json::to_value(self).unwrap_or_default();
        let age = (self.at_ms != 0).then(|| crate::temperature_history::age_ms(self.at_ms, now)).flatten();
        report["age_ms"] = serde_json::json!(age);
        report["fresh"] = serde_json::json!(age.is_some_and(|age| age <= 5000));
        report
    }
}
#[cfg(test)]
mod evidence_tests {
    use super::*;
    #[test]
    fn reports_raw_filtered_source_and_expiry_without_inventing_readings() {
        let raw = crate::system::thermal::ThermalRawSnapshot {
            snapshot: ThermalSnapshot {cpu_avg_c: Some(96.0), gpu_avg_c: None},
            cpu_source: Some(CpuTempSource::PerfCounter), gpu_source: None,
        };
        let filtered = ThermalSnapshot {cpu_avg_c: Some(68.0), gpu_avg_c: None};
        let evidence = ThermalEvidence::new(1000, &raw, &filtered);
        let report = evidence.report(2000);
        assert_eq!(report["cpu_source"], "perf_counter");
        assert_eq!(report["cpu_raw_c"], 96.0);
        assert_eq!(report["cpu_filtered_c"], 68.0);
        assert!(report["gpu_raw_c"].is_null());
        assert_eq!(report["fresh"], true);
        assert_eq!(evidence.report(10000)["fresh"], false);
        assert_eq!(evidence.report(500)["fresh"], false);
        assert!(ThermalEvidence::default().report(2000)["age_ms"].is_null());
    }
}
