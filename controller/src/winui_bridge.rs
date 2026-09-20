//! Private stdio transport. No listening socket and no device initialization in protocol tests.
use super::*;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::io::{BufRead, Write};

#[derive(Deserialize, Debug)]
#[serde(deny_unknown_fields)]
struct Request {
    id: u64,
    action: String,
    #[serde(default)]
    value: Value,
}
fn number(v: &Value, min: u64, max: u64) -> Result<u16, String> {
    v.as_u64()
        .filter(|n| *n >= min && *n <= max)
        .map(|n| n as u16)
        .ok_or("Value out of range".into())
}
fn enumeration<T: for<'a> Deserialize<'a>>(v: &Value) -> Result<T, String> {
    serde_json::from_value(v.clone()).map_err(|_| "Invalid option".into())
}
fn names<T: Serialize>(v: T) -> Value {
    serde_json::to_value(v).unwrap_or(Value::Null)
}
impl Controller {
    fn native_connection_error(&self) -> Option<String> {
        if !self.device_support.supported {
            return Some(self.device_support.reason.clone());
        }
        if self.session_state.locked.load(Ordering::Relaxed) {
            return Some("Windows session is locked".into());
        }
        if let Some(error) = &self.native_connection_error {
            return Some(error.clone());
        }
        if self.device.as_ref().is_some_and(|d| d.arc().is_poisoned()) {
            return Some("HID worker failed. Reconnect; see controller.log for details".into());
        }
        if self.device_detection_done && self.device.is_none() {
            return Some(
                "Laptop HID not found; the Windows model name does not confirm a connection".into(),
            );
        }
        if self.fully_initialized && self.device.is_some() && self.status.perf_mode.is_none() {
            return Some("HID is open but the operating mode has not been read yet".into());
        }
        None
    }
    fn native_snapshot(&self) -> Value {
        let t = self.control_snapshot();
        let connection_error = self.native_connection_error();
        let descriptor = self
            .device
            .as_ref()
            .and_then(|d| d.with(|d| (d.info().pid, d.info().features.clone())));
        let cpu: Vec<_> = self
            .cached_allowed_cpu
            .iter()
            .filter(|b| {
                **b != CpuBoost::Undervolt
                    && !self
                        .cached_disallowed_pairs
                        .contains(&(**b, self.gpu_boost))
            })
            .copied()
            .collect();
        let gpu: Vec<_> = self
            .cached_allowed_gpu
            .iter()
            .filter(|b| {
                !self
                    .cached_disallowed_pairs
                    .contains(&(self.cpu_boost, **b))
            })
            .copied()
            .collect();
        let mut state = json!({"ready":t.ready && connection_error.is_none() && self.status.perf_mode.is_some(), "model":self.system_specs.device_model,
            "modes":names(t.supported), "mode":names(t.current), "cpu_options":names(cpu),"gpu_options":names(gpu),
            "cpu_boost":names(self.cpu_boost),"gpu_boost":names(self.gpu_boost),
            "fan_auto":t.fan_auto,"fan_rpm":t.rpm,"actual_rpm":self.status.fan_actual_rpm,
            "cpu_temp":self.thermal.cpu_avg_c,"gpu_temp":self.thermal.gpu_avg_c,"ac":self.ac_power,
            "brightness":self.status.keyboard_brightness,"logo":names(self.status.logo_mode),"lights":self.status.lights_always_on,
            "battery":names(self.status.battery_care),"startup":self.run_at_startup,"auto_profiles":self.auto_switch_enabled,
            "cap_enabled":self.auto_fan_limit_enabled,"cap_rpm":self.auto_fan_max_rpm,
            "ac_profile":names(self.ac_profile.perf_mode),"battery_profile":names(self.battery_profile.perf_mode),
            "pad_mode":self.cooling_pad_fan_mode.as_str(),"pad_manual":self.cooling_pad_manual_rpm,
            "pad_min":self.cooling_pad_auto_min_rpm,"pad_max":self.cooling_pad_auto_max_rpm,
            "pad_off":self.cooling_pad_auto_off_below_c,"pad_full":self.cooling_pad_auto_full_above_c,
            "pad_follow":self.cooling_pad_follow_laptop_fan,"pad_lighting":self.cooling_pad_chroma_available,
            "pad_light_mode":self.cooling_pad_lighting_mode,"pad_brightness":self.cooling_pad_brightness_step,"pad_color":self.cooling_pad_color,
            "pad_connected":self.cooling_pad.is_some(),"pad_rpm":self.cooling_pad_display_rpm(),
            "message":self.message_manager.get_current_message().map(|m| m.content.clone()),
            "cpu_name":self.system_specs.cpu_name,"gpus":self.system_specs.gpu_models,"ram":self.system_specs.ram_gb});
        let now = temperature_history::now_ms();
        if let Ok(history) = self.temperature_history.lock() {
            let latest = history.latest();
            let age = latest.and_then(|p| temperature_history::age_ms(p.at_ms, now));
            let fresh = age.is_some_and(|a| a <= 5000);
            state["cpu_temp"] = names(if fresh {
                latest.and_then(|p| p.cpu)
            } else {
                None
            });
            state["gpu_temp"] = names(if fresh {
                latest.and_then(|p| p.gpu)
            } else {
                None
            });
            state["thermal_age_ms"] = names(age);
            state["temperature_history"] = names(history.recent(now));
        }
        let device_age = temperature_history::age_ms(self.device_measured_ms, now);
        state["device_age_ms"] = names(device_age);
        if !device_age.is_some_and(|a| a <= 5000) || connection_error.is_some() {
            state["actual_rpm"] = Value::Null;
        }
        state["support_status"] = json!(if self.device_support.supported {
            if self.device_support.experimental { "experimental" } else { "supported" }
        } else {
            "unsupported"
        });
        state["support_report"] = names(&self.device_support);
        state["connection_error"] = names(connection_error);
        state["hid_pid"] = names(
            descriptor
                .as_ref()
                .map(|(pid, _)| format!("1532:{pid:04X}")),
        );
        state["lighting_external"] = json!(self.lighting_external);
        state["keyboard_effect_supported"] = json!(
            descriptor
                .as_ref()
                .is_some_and(|(pid, _)| keyboard_lighting::supported(*pid))
        );
        state["features"] = names(descriptor.map(|(_, features)| features).unwrap_or_default());
        state
    }
    fn native_action(&mut self, request: &Request) -> Result<(), String> {
        let v = &request.value;
        if request.action == "visibility" {
            self.panel_visible = v.as_bool().ok_or("Expected boolean")?;
            self.update_polling_policy();
            if self.panel_visible {
                self.polling.refresh();
            }
            return Ok(());
        }
        if request.action == "snapshot" {
            return Ok(());
        }
        if !self.device_support.supported || self.device.is_none() {
            return Err(self
                .native_connection_error()
                .unwrap_or("Device is not ready".into()));
        }
        if request.action == "lighting_external" {
            let previous = self.lighting_external;
            self.lighting_external = v.as_bool().ok_or("Expected boolean")?;
            if let Err(e) = self.persist_config() {
                self.lighting_external = previous;
                return Err(e.to_string());
            }
            return Ok(());
        }
        if ["brightness", "logo", "lights", "keyboard_effect"].contains(&request.action.as_str()) {
            keyboard_lighting::ensure_owned(self.lighting_external).map_err(|e| e.to_string())?;
        }
        if request.action == "startup" {
            let enabled = v.as_bool().ok_or("Expected boolean")?;
            self.set_run_at_startup(enabled);
            return Ok(());
        }
        if request.action.starts_with("pad_") {
            if self.cooling_pad.is_none() || self.session_state.locked.load(Ordering::Relaxed) {
                return Err("Cooling Pad unavailable".into());
            }
            use cooling_pad_control::CoolingPadFanAction as A;
            let action = match request.action.as_str() {
                "pad_mode" => A::SetMode(match v.as_str() {
                    Some("off") => CoolingPadFanMode::Off,
                    Some("manual") => CoolingPadFanMode::Manual,
                    Some("auto") => CoolingPadFanMode::Auto,
                    _ => return Err("Invalid pad mode".into()),
                }),
                "pad_manual" => A::SetManualRpm(number(
                    v,
                    librazer::cooling_pad::MIN_RPM as u64,
                    librazer::cooling_pad::MAX_RPM as u64,
                )?),
                "pad_min" => A::SetAutoMinRpm(number(v, 500, 3200)?),
                "pad_max" => A::SetAutoMaxRpm(number(v, 500, 3200)?),
                "pad_off" => A::SetAutoOffBelowC(number(v, 30, 85)? as f32),
                "pad_full" => {
                    let n = number(v, 35, 100)? as f32;
                    if n < self.cooling_pad_auto_off_below_c + 5.0 {
                        return Err("Full speed temperature must exceed the off threshold by at least 5 degrees".into());
                    }
                    A::SetAutoFullAboveC(n)
                }
                "pad_follow" => A::ToggleFollowLaptopFan(v.as_bool().ok_or("Expected boolean")?),
                _ => {
                    if !self.cooling_pad_chroma_available {
                        return Err("Pad lighting unavailable".into());
                    }
                    match request.action.as_str() {
                        "pad_effect" => {
                            #[derive(Deserialize)]
                            #[serde(deny_unknown_fields)]
                            struct PadEffect {
                                mode: String,
                                color: [u8; 3],
                            }
                            let effect: PadEffect = enumeration(v)?;
                            let mode = PadLightingMode::from_str(&effect.mode)
                                .ok_or("Invalid pad effect")?;
                            let rgb = Rgb {
                                r: effect.color[0],
                                g: effect.color[1],
                                b: effect.color[2],
                            };
                            let brightness =
                                lighting::BRIGHTNESS_LEVELS[self.cooling_pad_brightness_step];
                            self.cooling_pad
                                .as_ref()
                                .and_then(|p| {
                                    p.with(|p| {
                                        cooling_pad_apply::apply_pad_lighting(
                                            p, mode, rgb, brightness, true,
                                        )
                                    })
                                })
                                .ok_or("Cooling Pad busy")?
                                .map_err(|e| e.to_string())?;
                            self.cooling_pad_color = effect.color;
                            self.cooling_pad_lighting_mode = effect.mode;
                            self.mark_cooling_pad_lighting_changed();
                            self.save_cooling_pad_config();
                        }
                        "pad_light_mode" => {
                            let mode = v.as_str().ok_or("Invalid mode")?;
                            if !["Off", "Static", "Breathing"].contains(&mode) {
                                return Err("Invalid mode".into());
                            }
                            self.set_cooling_pad_lighting_mode(mode);
                        }
                        "pad_brightness" => {
                            let step = number(v, 0, 15)? as usize;
                            self.cooling_pad_brightness_step = step;
                            self.set_cooling_pad_brightness(lighting::BRIGHTNESS_LEVELS[step]);
                        }
                        "pad_color" => {
                            let color: [u8; 3] = enumeration(v)?;
                            self.cooling_pad_color = color;
                            self.apply_cooling_pad_lighting_color();
                        }
                        _ => return Err("Unknown command".into()),
                    }
                    return Ok(());
                }
            };
            self.apply_native_pad_fan_action(action);
            return Ok(());
        }
        if let Some(error) = self.native_connection_error() {
            return Err(error);
        }
        if !self.control_snapshot().ready || self.status.perf_mode.is_none() {
            return Err("HID is not ready for control".into());
        }
        match request.action.as_str() {
            "keyboard_effect" => {
                let effect: keyboard_lighting::Effect = enumeration(v)?;
                self.device
                    .as_ref()
                    .and_then(|d| {
                        d.with(|d| keyboard_lighting::apply(d, self.lighting_external, &effect))
                    })
                    .ok_or("Device busy")?
                    .map_err(|e| e.to_string())?;
            }
            "performance" => {
                let mode: PerfMode = enumeration(v)?;
                if !self.control_snapshot().supported.contains(&mode) {
                    return Err("Unsupported mode".into());
                }
                self.set_performance_mode(&format!("{mode:?}"));
            }
            "fan_auto" => self.set_fan_mode("auto", None),
            "fan_rpm" => {
                let rpm = number(v, 2000, 5500)?;
                self.set_fan_mode("manual", Some(rpm));
                if self.status.fan_rpm == Some(rpm) {
                    self.manual_fan_rpm = rpm;
                }
            }
            "brightness" => {
                let step = number(v, 0, 15)? as usize;
                self.set_brightness(lighting::BRIGHTNESS_LEVELS[step]);
            }
            "logo" => {
                let mode: LogoMode = enumeration(v)?;
                self.set_logo_mode(&format!("{mode:?}"));
            }
            "lights" => {
                let value = v.as_bool().ok_or("Expected boolean")?;
                if value != self.status.lights_always_on {
                    self.status.lights_always_on = value;
                    self.toggle_lights_always_on();
                }
            }
            "battery" => {
                let care: BatteryCare = enumeration(v)?;
                self.set_battery_care(care);
            }
            "cpu" => {
                let b: CpuBoost = enumeration(v)?;
                if self.status.perf_mode != Some(PerfMode::Custom)
                    || b == CpuBoost::Undervolt
                    || !self.cached_allowed_cpu.contains(&b)
                    || self.cached_disallowed_pairs.contains(&(b, self.gpu_boost))
                {
                    return Err("Unsupported CPU boost".into());
                }
                let r = self
                    .device
                    .as_ref()
                    .and_then(|d| d.with_mut(|d| command::set_cpu_boost(d, b)))
                    .ok_or("Device busy")?;
                r.map_err(|e| e.to_string())?;
                self.cpu_boost = b;
            }
            "gpu" => {
                let b: GpuBoost = enumeration(v)?;
                if self.status.perf_mode != Some(PerfMode::Custom)
                    || !self.cached_allowed_gpu.contains(&b)
                    || self.cached_disallowed_pairs.contains(&(self.cpu_boost, b))
                {
                    return Err("Unsupported GPU boost".into());
                }
                let r = self
                    .device
                    .as_ref()
                    .and_then(|d| d.with_mut(|d| command::set_gpu_boost(d, b)))
                    .ok_or("Device busy")?;
                r.map_err(|e| e.to_string())?;
                self.gpu_boost = b;
            }
            "save_ac" => self.save_current_as_ac_profile(),
            "save_battery" => self.save_current_as_battery_profile(),
            "auto_profiles" => {
                let previous = self.auto_switch_enabled;
                self.auto_switch_enabled = v.as_bool().ok_or("Expected boolean")?;
                if let Err(e) = self.persist_config() {
                    self.auto_switch_enabled = previous;
                    return Err(e.to_string());
                }
            }
            "cap" => {
                self.auto_fan_limit_enabled = v.as_bool().ok_or("Expected boolean")?;
                if !self.auto_fan_limit_enabled && self.auto_fan_cap_override {
                    self.restore_auto_fan_mode();
                }
                self.sync_laptop_fan_cap();
                self.persist_config().map_err(|e| e.to_string())?;
            }
            "cap_rpm" => {
                self.auto_fan_max_rpm = number(v, 2000, 5500)?;
                if self.auto_fan_cap_override {
                    self.set_fan_rpm_only(self.auto_fan_max_rpm);
                }
                self.sync_laptop_fan_cap();
                self.sync_cooling_pad_enforce();
                self.persist_config().map_err(|e| e.to_string())?;
            }
            _ => return Err("Unknown command".into()),
        }
        Ok(())
    }
}
pub fn run() {
    let session = Arc::new(SessionState::default());
    let slot = new_device_slot();
    let mut app = Controller::new(slot.clone(), session.clone());
    app.single_instance = Some(app_single_instance::start_primary(
        SINGLE_INSTANCE_ID,
        || {},
    ));
    spawn_session_lock_monitor(
        session,
        slot,
        app.laptop_fan_cap.clone(),
        app.cooling_pad_enforce.settings.clone(),
        app.cooling_pad_enforce.pending_cooling_pad_restore.clone(),
    );
    let (tx, rx) = mpsc::sync_channel(8);
    std::thread::spawn(move || {
        for line in std::io::stdin().lock().lines() {
            let Ok(line) = line else {
                break;
            };
            if line.len() > 4096 || tx.send(line).is_err() {
                break;
            }
        }
    });
    loop {
        app.poll_background();
        app.message_manager.update();
        match rx.recv_timeout(std::time::Duration::from_millis(100)) {
            Ok(line) => {
                let response = match serde_json::from_str::<Request>(&line) {
                    Ok(r) => {
                        let before = app
                            .message_manager
                            .get_current_message()
                            .map(|m| m.timestamp);
                        let mut error = app.native_action(&r).err();
                        if error.is_none() {
                            if let Some(m) = app.message_manager.get_current_message() {
                                if Some(m.timestamp) != before
                                    && (m.message_type == messaging::MessageType::Error
                                        || m.content.starts_with("Failed"))
                                {
                                    error = Some(m.content.clone());
                                }
                            }
                        }
                        app.update_polling_policy();
                        json!({"id":r.id,"error":error,"state":app.native_snapshot()})
                    }
                    Err(e) => json!({"id":0,"error":e.to_string()}),
                };
                let mut out = std::io::stdout().lock();
                if writeln!(out, "{response}")
                    .and_then(|_| out.flush())
                    .is_err()
                {
                    break;
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }
    app.shutdown_cooling_pad();
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn protocol_rejects_malformed_and_extra_fields() {
        assert!(
            serde_json::from_str::<Request>(r#"{"id":1,"action":"snapshot","arbitrary":1}"#)
                .is_err()
        );
        assert!(serde_json::from_str::<Request>(r#"{"id":1,"action":"snapshot"}"#).is_ok());
    }
    #[test]
    fn commands_reject_invalid_values() {
        for v in [
            json!(-1),
            json!(5501),
            json!(3.5),
            json!("4000"),
            Value::Null,
        ] {
            assert!(number(&v, 3000, 5500).is_err());
        }
        assert_eq!(number(&json!(4000), 3000, 5500), Ok(4000));
        assert!(enumeration::<PerfMode>(&json!("invalid")).is_err());
    }
}
