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
                filter_thermal_raw_snapshot(&guard, raw, &mut spike_state, &mut last_cpu_source);
            if let Ok(mut h) = history.lock() {
                h.push(now_ms(), filtered.cpu_avg_c, filtered.gpu_avg_c);
            }
            *guard = filtered;
        }
    });
}

pub fn read_shared_thermal(shared: &Arc<Mutex<ThermalSnapshot>>) -> ThermalSnapshot {
    shared.lock().map(|guard| guard.clone()).unwrap_or_default()
}
