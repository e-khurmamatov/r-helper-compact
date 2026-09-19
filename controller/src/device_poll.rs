use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, Ordering},
    mpsc::Sender,
};
use std::time::{Duration, Instant};

use librazer::{
    command,
    device::Device,
    types::{BatteryCare, FanMode, FanZone, LightsAlwaysOn, LogoMode, PerfMode},
};

use librazer::cooling_pad::CoolingPadDevice;

use crate::device::CompleteDeviceState;
use crate::polling::{ReadKind, Schedule};
use crate::power::get_power_state;

const SLOW_POLL_INTERVAL: Duration = Duration::from_secs(3);

#[derive(Debug, Clone)]
pub struct DevicePollSnapshot {
    pub measured_ms: u64,
    pub optional_reads: bool,
    pub ac_power: bool,
    pub fan_actual_rpm: Option<u16>,
    pub fan_mode: FanMode,
    pub perf_mode: Option<PerfMode>,
    pub connection_error: Option<String>,
    pub fan_set_rpm: Option<u16>,
    pub keyboard_brightness: Option<u8>,
    pub lights_always_on: Option<bool>,
    pub logo_mode: Option<LogoMode>,
    pub battery_care: Option<BatteryCare>,
    pub full_state: Option<CompleteDeviceState>,
}

#[derive(Debug, Clone)]
pub struct CoolingPadPollSnapshot {
    pub brightness: Option<u8>,
}

pub fn spawn_cooling_pad_poller(
    device: Arc<Mutex<CoolingPadDevice>>,
    tx: Sender<CoolingPadPollSnapshot>,
    brightness_slider_active: Arc<AtomicBool>,
    schedule: Arc<Schedule>,
    running: Arc<AtomicBool>,
) {
    std::thread::spawn(move || {
        let mut generation = u64::MAX;
        loop {
            if !running.load(Ordering::Relaxed) {
                break;
            }
            let policy = schedule.wait(ReadKind::PadLighting, &mut generation);
            if !policy.visible {
                continue;
            }

            let snapshot = {
                let device = match device.try_lock() {
                    Ok(guard) => guard,
                    Err(_) => continue,
                };
                read_cooling_pad_snapshot(&device, brightness_slider_active.load(Ordering::Relaxed))
            };

            if tx.send(snapshot).is_err() {
                break;
            }
        }
    });
}

fn read_cooling_pad_snapshot(
    device: &CoolingPadDevice,
    skip_brightness: bool,
) -> CoolingPadPollSnapshot {
    let brightness = if skip_brightness || !device.chroma_available() {
        None
    } else {
        device.brightness().ok()
    };

    CoolingPadPollSnapshot { brightness }
}

pub fn spawn_device_poller(
    device: Arc<Mutex<Device>>,
    tx: Sender<DevicePollSnapshot>,
    brightness_slider_active: Arc<AtomicBool>,
    schedule: Arc<Schedule>,
    laptop_fan_rpm: Arc<Mutex<Option<u16>>>,
) {
    std::thread::spawn(move || {
        let mut generation = u64::MAX;
        let mut last_slow = Instant::now()
            .checked_sub(SLOW_POLL_INTERVAL)
            .unwrap_or_else(Instant::now);

        loop {
            let policy = schedule.wait(ReadKind::Device, &mut generation);
            let include_full = policy.visible && last_slow.elapsed() >= SLOW_POLL_INTERVAL;
            let skip_brightness = brightness_slider_active.load(Ordering::Relaxed);

            let snapshot = {
                let device = match device.try_lock() {
                    Ok(guard) => guard,
                    Err(_) => continue,
                };
                match read_snapshot(&device, skip_brightness, include_full, policy.visible) {
                    Some(s) => s,
                    None => continue,
                }
            };

            if snapshot.full_state.is_some() {
                last_slow = Instant::now();
            }

            if let Ok(mut shared) = laptop_fan_rpm.lock() {
                *shared = snapshot.fan_actual_rpm;
            }

            if tx.send(snapshot).is_err() {
                break;
            }
        }
    });
}

fn read_snapshot(
    device: &Device,
    skip_brightness: bool,
    include_full_state: bool,
    optional_reads: bool,
) -> Option<DevicePollSnapshot> {
    collect_snapshot_with_policy(
        &HardwareReader(device),
        skip_brightness,
        include_full_state,
        optional_reads,
    )
}

trait PollReader {
    fn power(&self) -> bool;
    fn actual_rpm(&self) -> Option<u16>;
    fn performance(&self) -> anyhow::Result<(PerfMode, FanMode)>;
    fn set_rpm(&self) -> Option<u16>;
    fn brightness(&self) -> Option<u8>;
    fn lights(&self) -> Option<bool>;
    fn logo(&self) -> Option<LogoMode>;
    fn battery(&self) -> Option<BatteryCare>;
    fn full_state(&self) -> Option<CompleteDeviceState>;
}
struct HardwareReader<'a>(&'a Device);
impl PollReader for HardwareReader<'_> {
    fn power(&self) -> bool {
        get_power_state().unwrap_or(true)
    }
    fn actual_rpm(&self) -> Option<u16> {
        command::get_fan_actual_rpm(self.0, FanZone::Zone1).ok()
    }
    fn performance(&self) -> anyhow::Result<(PerfMode, FanMode)> {
        command::get_perf_mode(self.0)
    }
    fn set_rpm(&self) -> Option<u16> {
        command::get_fan_rpm(self.0, FanZone::Zone1).ok()
    }
    fn brightness(&self) -> Option<u8> {
        command::get_keyboard_brightness(self.0).ok()
    }
    fn lights(&self) -> Option<bool> {
        command::get_lights_always_on(self.0)
            .map(|v| matches!(v, LightsAlwaysOn::Enable))
            .ok()
    }
    fn logo(&self) -> Option<LogoMode> {
        command::get_logo_mode(self.0).ok()
    }
    fn battery(&self) -> Option<BatteryCare> {
        command::get_battery_care(self.0).ok()
    }
    fn full_state(&self) -> Option<CompleteDeviceState> {
        CompleteDeviceState::read_from_device(self.0).ok()
    }
}
fn collect_snapshot_with_policy(
    reader: &impl PollReader,
    skip_brightness: bool,
    include_full_state: bool,
    optional_reads: bool,
) -> Option<DevicePollSnapshot> {
    let ac_power = reader.power();
    let fan_actual_rpm = reader.actual_rpm();
    let (perf_mode, fan_mode, fan_set_rpm, connection_error) = match reader.performance() {
        Ok((pm, fm)) => {
            let rpm = if fm == FanMode::Manual {
                reader.set_rpm()
            } else {
                None
            };
            (Some(pm), fm, rpm, None)
        }
        Err(error) => (
            None,
            FanMode::Auto,
            None,
            Some(format!("HID get_perf_mode: {error:#}")),
        ),
    };

    let keyboard_brightness = if skip_brightness || !optional_reads {
        None
    } else {
        reader.brightness()
    };

    let lights_always_on = if optional_reads {
        reader.lights()
    } else {
        None
    };
    let battery_care = if optional_reads {
        reader.battery()
    } else {
        None
    };

    let full_state = if include_full_state && optional_reads {
        reader.full_state()
    } else {
        None
    };

    Some(DevicePollSnapshot {
        measured_ms: crate::temperature_history::now_ms(),
        optional_reads,
        ac_power,
        fan_actual_rpm,
        fan_mode,
        perf_mode,
        connection_error,
        fan_set_rpm,
        keyboard_brightness,
        lights_always_on,
        logo_mode: if optional_reads { reader.logo() } else { None },
        battery_care,
        full_state,
    })
}

#[cfg(test)]
fn collect_snapshot(
    reader: &impl PollReader,
    skip_brightness: bool,
    include_full_state: bool,
) -> Option<DevicePollSnapshot> {
    collect_snapshot_with_policy(reader, skip_brightness, include_full_state, true)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn hidden_poll_never_reads_optional_controls() {
        struct Background;
        impl PollReader for Background {
            fn power(&self) -> bool {
                true
            }
            fn actual_rpm(&self) -> Option<u16> {
                Some(3000)
            }
            fn performance(&self) -> anyhow::Result<(PerfMode, FanMode)> {
                Ok((PerfMode::Balanced, FanMode::Auto))
            }
            fn set_rpm(&self) -> Option<u16> {
                panic!("Auto does not need requested RPM")
            }
            fn brightness(&self) -> Option<u8> {
                panic!("hidden brightness read")
            }
            fn lights(&self) -> Option<bool> {
                panic!("hidden lights read")
            }
            fn logo(&self) -> Option<LogoMode> {
                panic!("hidden logo read")
            }
            fn battery(&self) -> Option<BatteryCare> {
                panic!("hidden charge-limit read")
            }
            fn full_state(&self) -> Option<CompleteDeviceState> {
                panic!("hidden full-state read")
            }
        }
        let s = collect_snapshot_with_policy(&Background, false, true, false).unwrap();
        assert_eq!(s.fan_actual_rpm, Some(3000));
        assert!(!s.optional_reads);
        assert!(s.logo_mode.is_none());
    }
    struct PartialReader {
        perf_failed: bool,
        skip_brightness: bool,
    }
    impl PollReader for PartialReader {
        fn power(&self) -> bool {
            true
        }
        fn actual_rpm(&self) -> Option<u16> {
            Some(3200)
        }
        fn performance(&self) -> anyhow::Result<(PerfMode, FanMode)> {
            if self.perf_failed {
                anyhow::bail!("disconnected")
            }
            Ok((PerfMode::Balanced, FanMode::Manual))
        }
        fn set_rpm(&self) -> Option<u16> {
            Some(4000)
        }
        fn brightness(&self) -> Option<u8> {
            assert!(!self.skip_brightness);
            Some(89)
        }
        fn lights(&self) -> Option<bool> {
            None
        }
        fn logo(&self) -> Option<LogoMode> {
            Some(LogoMode::Off)
        }
        fn battery(&self) -> Option<BatteryCare> {
            None
        }
        fn full_state(&self) -> Option<CompleteDeviceState> {
            None
        }
    }
    #[test]
    fn optional_read_failure_keeps_rpm_and_performance() {
        let s = collect_snapshot(
            &PartialReader {
                perf_failed: false,
                skip_brightness: false,
            },
            false,
            true,
        )
        .unwrap();
        assert_eq!(s.logo_mode, Some(LogoMode::Off));
        assert_eq!(s.fan_actual_rpm, Some(3200));
        assert_eq!(s.fan_set_rpm, Some(4000));
        assert_eq!(s.perf_mode, Some(PerfMode::Balanced));
        assert!(s.battery_care.is_none() && s.full_state.is_none() && s.connection_error.is_none());
    }
    #[test]
    fn core_read_failure_is_reported_without_inventing_a_mode() {
        let s = collect_snapshot(
            &PartialReader {
                perf_failed: true,
                skip_brightness: false,
            },
            false,
            true,
        )
        .unwrap();
        assert!(s.perf_mode.is_none());
        assert!(s.connection_error.unwrap().contains("disconnected"));
        assert_eq!(s.fan_actual_rpm, Some(3200));
        assert!(s.fan_set_rpm.is_none());
    }
    #[test]
    fn editing_brightness_skips_its_hardware_read_only() {
        let s = collect_snapshot(
            &PartialReader {
                perf_failed: false,
                skip_brightness: true,
            },
            true,
            false,
        )
        .unwrap();
        assert!(s.keyboard_brightness.is_none());
        assert_eq!(s.fan_actual_rpm, Some(3200));
    }
}
