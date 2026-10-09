//! Explicit, typed experiments. No model overrides, initialization sweeps or raw-command input.
use crate::{command, device::Transport, keyboard, packet::Packet, types::*};
use anyhow::{Result, bail, ensure};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    cell::RefCell,
    collections::VecDeque,
    time::{Instant, SystemTime, UNIX_EPOCH},
};

pub const MAX_RECORDS: usize = 64;
const MAX_TRACE: usize = 128;

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq)]
pub enum Protocol {
    Legacy4,
    Modern6,
    StandardMatrix,
    ExtendedMatrix,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Operation {
    Performance { value: PerfMode },
    FanMode { value: FanMode },
    FanRpm { value: u16 },
    CpuBoost { value: CpuBoost },
    GpuBoost { value: GpuBoost },
    Battery { value: BatteryCare },
    Brightness { value: u8 },
    Logo { value: LogoMode },
    IdleLighting { value: LightsAlwaysOn },
    MaxFan { value: MaxFanSpeedMode },
    Effect { value: keyboard::Effect },
}
impl Operation {
    pub fn lighting(&self) -> bool {
        matches!(
            self,
            Self::Brightness { .. }
                | Self::Logo { .. }
                | Self::IdleLighting { .. }
                | Self::Effect { .. }
        )
    }
}
#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Experiment {
    pub protocol: Protocol,
    pub write: bool,
    pub operation: Operation,
}
impl Experiment {
    pub fn validate(&self) -> Result<()> {
        use Operation::*;
        match (&self.operation, self.protocol) {
            (Effect { .. }, Protocol::StandardMatrix | Protocol::ExtendedMatrix) => {}
            (Effect { .. }, _) => bail!("Choose a matrix protocol for keyboard effects"),
            (Brightness { .. }, _) => {}
            (_, Protocol::StandardMatrix | Protocol::ExtendedMatrix) => {
                bail!("This protocol only supports keyboard experiments")
            }
            _ => {}
        }
        match &self.operation {
            FanRpm { value } => ensure!(
                (2000..=5500).contains(value) && value % 100 == 0,
                "RPM must be 2000–5500 in steps of 100"
            ),
            CpuBoost {
                value: crate::types::CpuBoost::Undervolt,
            } => bail!("Undervolt is not a diagnostic option"),
            Performance { value } if self.protocol == Protocol::Legacy4 => ensure!(
                crate::profile::BladeGeneration::Legacy4
                    .default_perf_modes()
                    .unwrap()
                    .contains(value),
                "This mode requires Modern6"
            ),
            Effect { value } if self.protocol == Protocol::ExtendedMatrix => ensure!(
                matches!(
                    value,
                    keyboard::Effect::Off {}
                        | keyboard::Effect::Static { .. }
                        | keyboard::Effect::Breathing { .. }
                ),
                "ExtendedMatrix supports Off, Static and Breathing"
            ),
            _ => {}
        }
        Ok(())
    }
}

#[derive(Debug, Serialize, Clone)]
pub struct Exchange {
    pub elapsed_ms: u64,
    pub phase: &'static str,
    pub event: &'static str,
    pub hex: String,
    pub status: Option<u8>,
}
struct Capture {
    started: Instant,
    phase: &'static str,
    events: Vec<Exchange>,
    dropped: usize,
}
thread_local! { static CAPTURE: RefCell<Option<Capture>> = const { RefCell::new(None) }; }
/// Only called for typed control packets; never records enumeration, paths or HID error strings.
pub(crate) fn trace(event: &'static str, bytes: &[u8], status: Option<u8>) {
    CAPTURE.with(|slot| {
        if let Some(capture) = slot.borrow_mut().as_mut() {
            if capture.events.len() >= MAX_TRACE {
                capture.dropped += 1;
                return;
            }
            capture.events.push(Exchange {
                elapsed_ms: capture.started.elapsed().as_millis() as u64,
                phase: capture.phase,
                event,
                hex: bytes
                    .iter()
                    .take(90)
                    .map(|b| format!("{b:02X}"))
                    .collect::<Vec<_>>()
                    .join(" "),
                status,
            });
        }
    });
}
pub(crate) fn trace_request(packet: &Packet) {
    if CAPTURE.with(|slot| slot.borrow().is_some()) {
        trace("request", &Vec::<u8>::from(packet), None);
    }
}
fn phase(value: &'static str) {
    CAPTURE.with(|slot| {
        if let Some(c) = slot.borrow_mut().as_mut() {
            c.phase = value;
        }
    });
}
struct CaptureGuard;
impl Drop for CaptureGuard {
    fn drop(&mut self) {
        CAPTURE.with(|s| {
            s.borrow_mut().take();
        });
    }
}

#[derive(Debug, Serialize)]
pub struct Reading {
    pub value: Option<Value>,
    pub error: Option<&'static str>,
}
fn read(device: &impl Transport, experiment: &Experiment) -> Reading {
    use Operation::*;
    fn value<T: Serialize>(v: Result<T>) -> Result<Value> {
        Ok(serde_json::to_value(v?)?)
    }
    let result = match &experiment.operation {
        Performance { .. } | FanMode { .. } => {
            value(command::get_perf_mode(device)).map(|v| json!({"mode":v[0],"fan_mode":v[1]}))
        }
        FanRpm { .. } => (|| {
            Ok(
                json!({"zone1":command::get_fan_rpm(device,FanZone::Zone1)?,"zone2":command::get_fan_rpm(device,FanZone::Zone2)?}),
            )
        })(),
        CpuBoost { .. } => value(command::get_cpu_boost(device)),
        GpuBoost { .. } => value(command::get_gpu_boost(device)),
        Battery { .. } => value(command::get_battery_care(device)),
        Brightness { .. } if experiment.protocol == Protocol::ExtendedMatrix => (|| {
            let bytes = crate::chroma::effects::build_extended_get_brightness(0xff, 1, 0);
            let packet = device.send(Packet::try_from(bytes.as_slice())?)?;
            ensure!(
                packet.get_args()[..2] == [1, 0],
                "Unexpected brightness response"
            );
            Ok(json!(packet.get_args()[2]))
        })(),
        Brightness { .. } => value(command::get_keyboard_brightness(device)),
        Logo { .. } => value(command::get_logo_mode(device)),
        IdleLighting { .. } => value(command::get_lights_always_on(device)),
        MaxFan { .. } => value(command::get_max_fan_speed_mode(device)),
        Effect { .. } => {
            return Reading {
                value: None,
                error: Some("effect_readback_unavailable"),
            };
        }
    };
    match result {
        Ok(value) => Reading {
            value: Some(value),
            error: None,
        },
        Err(_) => Reading {
            value: None,
            error: Some("device_read_failed"),
        },
    }
}
fn write(device: &impl Transport, experiment: &Experiment) -> Result<()> {
    use Operation::*;
    match &experiment.operation {
        Performance { value } => command::set_perf_mode(device, *value),
        FanMode { value } => command::set_fan_mode(device, *value),
        // Preserve the Custom/Manual prerequisites; each is an explicit separate experiment.
        FanRpm { value } => command::set_fan_rpm(device, *value, true),
        CpuBoost { value } => command::set_cpu_boost(device, *value),
        GpuBoost { value } => command::set_gpu_boost(device, *value),
        Battery { value } => command::set_battery_care(device, *value),
        Brightness { value } if experiment.protocol == Protocol::ExtendedMatrix => {
            let bytes = crate::chroma::effects::build_extended_brightness(0xff, 1, 0, *value);
            device.send(Packet::try_from(bytes.as_slice())?)?;
            Ok(())
        }
        Brightness { value } => command::set_keyboard_brightness(device, *value),
        Logo { value } => command::set_logo_mode(device, *value),
        IdleLighting { value } => command::set_lights_always_on(device, *value),
        MaxFan { value } => command::set_max_fan_speed_mode(device, *value),
        Effect { value } => {
            let bytes = if experiment.protocol == Protocol::ExtendedMatrix {
                use crate::chroma::effects::*;
                match value {
                    keyboard::Effect::Off {} => build_extended_none(0xff, 1, 0),
                    keyboard::Effect::Static { color } => build_extended_static(
                        0xff,
                        1,
                        0,
                        Rgb {
                            r: color[0],
                            g: color[1],
                            b: color[2],
                        },
                    ),
                    keyboard::Effect::Breathing { color } => build_extended_breathing_single(
                        0xff,
                        1,
                        0,
                        Rgb {
                            r: color[0],
                            g: color[1],
                            b: color[2],
                        },
                    ),
                    _ => bail!("Unsupported extended effect"),
                }
            } else {
                keyboard::report(value)
            };
            device.send(Packet::try_from(bytes.as_slice())?)?;
            Ok(())
        }
    }
}
#[derive(Debug, Serialize)]
pub struct Record {
    pub sequence: u64,
    pub at_ms: u64,
    pub experiment: Experiment,
    pub before: Reading,
    pub write_result: &'static str,
    pub write_error: Option<&'static str>,
    pub after: Reading,
    pub exchanges: Vec<Exchange>,
    pub dropped_exchanges: usize,
}
pub fn execute(device: &impl Transport, experiment: Experiment) -> Result<Record> {
    experiment.validate()?;
    let at_ms = now_ms();
    CAPTURE.with(|slot| {
        ensure!(slot.borrow().is_none(), "Diagnostic capture already active");
        *slot.borrow_mut() = Some(Capture {
            started: Instant::now(),
            phase: "before",
            events: Vec::new(),
            dropped: 0,
        });
        Ok::<_, anyhow::Error>(())
    })?;
    let _guard = CaptureGuard;
    let before = read(device, &experiment);
    phase("write");
    let (write_result, write_error) = if !experiment.write {
        ("not_requested", None)
    } else {
        match write(device, &experiment) {
            Ok(()) => ("acknowledged", None),
            Err(error) => {
                // Classify known preconditions without copying HID errors or paths into the export.
                let error = error.to_string();
                let reason = if error.contains("Performance mode must be Custom") {
                    "custom_mode_required"
                } else if error.contains("Fan mode must be set to Manual") {
                    "manual_fan_mode_required"
                } else {
                    "device_write_failed"
                };
                ("failed", Some(reason))
            }
        }
    };
    // Partial writes may have taken effect even when their response failed.
    phase("after");
    let after = if experiment.write {
        read(device, &experiment)
    } else {
        Reading {
            value: None,
            error: Some("not_requested"),
        }
    };
    let capture = CAPTURE.with(|slot| slot.borrow_mut().take().unwrap());
    Ok(Record {
        sequence: 0,
        at_ms,
        experiment,
        before,
        write_result,
        write_error,
        after,
        exchanges: capture.events,
        dropped_exchanges: capture.dropped,
    })
}
pub fn blocked(experiment: Experiment, reason: &'static str) -> Record {
    Record {
        sequence: 0,
        at_ms: now_ms(),
        experiment,
        before: Reading {
            value: None,
            error: Some("not_attempted"),
        },
        write_result: "blocked",
        write_error: Some(reason),
        after: Reading {
            value: None,
            error: Some("not_attempted"),
        },
        exchanges: Vec::new(),
        dropped_exchanges: 0,
    }
}
fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
#[derive(Default, Serialize)]
pub struct Session {
    pub active: bool,
    pub started_ms: u64,
    pub ended_ms: Option<u64>,
    pub metadata: Value,
    pub dropped_records: usize,
    pub records: VecDeque<Record>,
    #[serde(skip)]
    next_sequence: u64,
}
impl Session {
    pub fn start(&mut self, metadata: Value) {
        let started_ms = now_ms().max(self.started_ms.saturating_add(1));
        *self = Self {
            active: true,
            started_ms,
            metadata,
            ..Self::default()
        };
    }
    pub fn stop(&mut self) {
        if self.active {
            self.ended_ms = Some(now_ms());
        }
        self.active = false;
    }
    pub fn push(&mut self, mut record: Record) {
        self.next_sequence += 1;
        record.sequence = self.next_sequence;
        if self.records.len() == MAX_RECORDS {
            self.records.pop_front();
            self.dropped_records += 1;
        }
        self.records.push_back(record);
    }
    pub fn matches_identity(&self, pid: u16, sku: &str) -> bool {
        self.metadata.get("pid").and_then(Value::as_str) == Some(format!("1532:{pid:04X}").as_str())
            && self.metadata.get("sku").and_then(Value::as_str) == Some(sku)
    }
    pub fn summary(&self) -> Value {
        json!({"active":self.active,"started_ms":self.started_ms,"count":self.next_sequence,"dropped_records":self.dropped_records})
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::{Cell, RefCell};
    struct Fake {
        brightness: Cell<u8>,
        fail_write: Cell<bool>,
        fail_read: Cell<bool>,
        packets: RefCell<Vec<Vec<u8>>>,
    }
    impl Default for Fake {
        fn default() -> Self {
            Self {
                brightness: Cell::new(40),
                fail_write: Cell::new(false),
                fail_read: Cell::new(false),
                packets: RefCell::new(Vec::new()),
            }
        }
    }
    impl Transport for Fake {
        fn send(&self, packet: Packet) -> Result<Packet> {
            let mut bytes = Vec::<u8>::from(&packet);
            trace("request", &bytes, None);
            self.packets.borrow_mut().push(bytes.clone());
            let command = u16::from_be_bytes([bytes[6], bytes[7]]);
            match command {
                0x0303 | 0x0f04 => {
                    self.brightness.set(bytes[10]);
                    if self.fail_write.get() {
                        trace("read_error", &[], None);
                        bail!("PRIVATE_DEVICE_PATH: response unavailable");
                    }
                }
                0x0383 | 0x0f84 => {
                    if self.fail_read.get() {
                        trace("read_error", &[], None);
                        bail!("PRIVATE_SERIAL: no response");
                    }
                    bytes[10] = self.brightness.get();
                }
                0x0d82 => {
                    bytes[10] = PerfMode::Balanced as u8;
                    bytes[11] = FanMode::Auto as u8;
                }
                _ => {}
            }
            bytes[0] = 2;
            trace("response", &bytes, Some(2));
            Packet::try_from(bytes.as_slice())
        }
    }
    fn brightness(protocol: Protocol, write: bool) -> Experiment {
        Experiment {
            protocol,
            write,
            operation: Operation::Brightness { value: 99 },
        }
    }
    #[test]
    fn captures_actual_before_write_after_and_protocol_packets() {
        for (protocol, get, set) in [
            (Protocol::StandardMatrix, 0x0383, 0x0303),
            (Protocol::ExtendedMatrix, 0x0f84, 0x0f04),
        ] {
            let device = Fake::default();
            let result = execute(&device, brightness(protocol, true)).unwrap();
            assert_eq!(result.before.value, Some(json!(40)));
            assert_eq!(result.after.value, Some(json!(99)));
            assert_eq!(result.write_result, "acknowledged");
            let commands: Vec<_> = device
                .packets
                .borrow()
                .iter()
                .map(|p| u16::from_be_bytes([p[6], p[7]]))
                .collect();
            assert_eq!(commands, vec![get, set, get]);
            assert_eq!(
                result.exchanges.iter().map(|x| x.phase).collect::<Vec<_>>(),
                vec!["before", "before", "write", "write", "after", "after"]
            );
        }
    }
    #[test]
    fn failed_response_still_reads_back_partial_write_and_never_exports_raw_errors() {
        let device = Fake::default();
        device.fail_write.set(true);
        let result = execute(&device, brightness(Protocol::Legacy4, true)).unwrap();
        assert_eq!(result.write_result, "failed");
        assert_eq!(result.after.value, Some(json!(99)));
        assert!(result.exchanges.iter().any(|e| e.event == "read_error"));
        assert!(!serde_json::to_string(&result).unwrap().contains("PRIVATE"));
    }
    #[test]
    fn read_failure_is_unknown_and_read_only_never_writes() {
        let device = Fake::default();
        device.fail_read.set(true);
        let result = execute(&device, brightness(Protocol::Modern6, false)).unwrap();
        assert!(result.before.value.is_none());
        assert_eq!(result.before.error, Some("device_read_failed"));
        assert_eq!(result.write_result, "not_requested");
        assert_eq!(device.packets.borrow().len(), 1);
    }
    #[test]
    fn invalid_ranges_and_protocols_never_reach_transport() {
        let device = Fake::default();
        for operation in [
            Operation::FanRpm { value: 1999 },
            Operation::FanRpm { value: 5501 },
            Operation::FanRpm { value: 4350 },
            Operation::CpuBoost {
                value: CpuBoost::Undervolt,
            },
            Operation::Performance {
                value: PerfMode::Hyperboost,
            },
        ] {
            assert!(
                execute(
                    &device,
                    Experiment {
                        protocol: Protocol::Legacy4,
                        write: true,
                        operation
                    }
                )
                .is_err()
            );
        }
        assert!(
            execute(
                &device,
                Experiment {
                    protocol: Protocol::ExtendedMatrix,
                    write: true,
                    operation: Operation::Battery {
                        value: BatteryCare::Percent80
                    }
                }
            )
            .is_err()
        );
        assert!(device.packets.borrow().is_empty());
        for raw in [
            r#"{"protocol":"Unknown","write":true,"operation":{"kind":"brightness","value":10}}"#,
            r#"{"protocol":"Legacy4","write":true,"operation":{"kind":"brightness","value":256}}"#,
            r#"{"protocol":"Legacy4","write":true,"operation":{"kind":"brightness","value":10,"command":123}}"#,
            r#"{"protocol":"Legacy4","write":true,"operation":{"kind":"raw","value":123}}"#,
        ] {
            assert!(serde_json::from_str::<Experiment>(raw).is_err());
        }
    }
    #[test]
    fn manual_fan_prerequisite_is_preserved() {
        let device = Fake::default();
        let record = execute(
            &device,
            Experiment {
                protocol: Protocol::Modern6,
                write: true,
                operation: Operation::FanRpm { value: 4000 },
            },
        )
        .unwrap();
        assert_eq!(record.write_result, "failed");
        assert!(
            !device
                .packets
                .borrow()
                .iter()
                .any(|p| p[6..8] == [0x0d, 0x01])
        );
    }
    #[test]
    fn effect_reports_do_not_pretend_to_have_readback() {
        let device = Fake::default();
        let record = execute(
            &device,
            Experiment {
                protocol: Protocol::StandardMatrix,
                write: true,
                operation: Operation::Effect {
                    value: keyboard::Effect::Static { color: [7, 8, 9] },
                },
            },
        )
        .unwrap();
        assert_eq!(record.before.error, Some("effect_readback_unavailable"));
        assert_eq!(record.after.error, Some("effect_readback_unavailable"));
        assert_eq!(device.packets.borrow()[0][8..12], [6, 7, 8, 9]);
        assert_eq!(record.write_result, "acknowledged");
    }
    #[test]
    fn sessions_bound_memory_and_keep_final_results_with_explicit_truncation() {
        let mut session = Session::default();
        session.start(json!({"pid":"1532:02B7"}));
        for _ in 0..MAX_RECORDS + 3 {
            session.push(execute(&Fake::default(), brightness(Protocol::Legacy4, false)).unwrap());
        }
        assert_eq!(session.records.len(), MAX_RECORDS);
        assert_eq!(session.dropped_records, 3);
        assert_eq!(
            session.records.back().unwrap().sequence,
            (MAX_RECORDS + 3) as u64
        );
        session.stop();
        assert!(!session.active);
        assert!(session.ended_ms.is_some());
        session.start(json!({}));
        assert!(session.records.is_empty());
        assert_eq!(session.dropped_records, 0);
    }
    #[test]
    fn capture_does_not_leak_into_other_operations_or_threads() {
        let a = std::thread::spawn(|| {
            execute(&Fake::default(), brightness(Protocol::StandardMatrix, true)).unwrap()
        });
        let b = std::thread::spawn(|| {
            execute(
                &Fake::default(),
                brightness(Protocol::ExtendedMatrix, false),
            )
            .unwrap()
        });
        assert_eq!(a.join().unwrap().exchanges.len(), 6);
        assert_eq!(b.join().unwrap().exchanges.len(), 2);
        trace("request", &[99], None);
        assert_eq!(
            execute(&Fake::default(), brightness(Protocol::Legacy4, false))
                .unwrap()
                .exchanges
                .len(),
            2
        );
    }
    #[test]
    fn session_identity_is_bound_to_the_original_controller_and_sku() {
        let mut session = Session::default();
        session.start(json!({"pid":"1532:02B7","sku":"RZ09-0483"}));
        assert!(session.matches_identity(0x02b7, "RZ09-0483"));
        assert!(!session.matches_identity(0x029f, "RZ09-0483"));
        assert!(!session.matches_identity(0x02b7, "RZ09-0510"));
        assert!(!Session::default().matches_identity(0x02b7, "RZ09-0483"));
    }
    #[test]
    fn blocked_operations_export_a_reason_without_any_transport_requests() {
        let record = blocked(
            brightness(Protocol::ExtendedMatrix, true),
            "external_lighting_owner",
        );
        assert_eq!(record.write_result, "blocked");
        assert_eq!(record.write_error, Some("external_lighting_owner"));
        assert!(record.exchanges.is_empty());
        assert!(record.before.value.is_none());
    }
    #[test]
    fn trace_truncation_is_counted_and_capture_is_reset_after_unwinding() {
        struct Flood;
        impl Transport for Flood {
            fn send(&self, _: Packet) -> Result<Packet> {
                for _ in 0..MAX_TRACE + 3 {
                    trace("read_error", &[], None);
                }
                bail!("READ_FAILED")
            }
        }
        let record = execute(&Flood, brightness(Protocol::Legacy4, false)).unwrap();
        assert_eq!(record.exchanges.len(), MAX_TRACE);
        assert_eq!(record.dropped_exchanges, 3);
        struct Panic;
        impl Transport for Panic {
            fn send(&self, _: Packet) -> Result<Packet> {
                panic!("synthetic transport panic");
            }
        }
        assert!(
            std::panic::catch_unwind(|| execute(&Panic, brightness(Protocol::Legacy4, false)))
                .is_err()
        );
        assert!(execute(&Fake::default(), brightness(Protocol::Legacy4, false)).is_ok());
    }
}
