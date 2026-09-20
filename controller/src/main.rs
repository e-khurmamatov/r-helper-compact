#![windows_subsystem = "windows"]

mod app;
mod config;
mod control_snapshot;
mod cooling_pad_apply;
mod cooling_pad_auto;
mod cooling_pad_control;
mod cooling_pad_enforce;
mod cooling_pad_handle;
mod device;
mod device_handle;
mod device_poll;
mod device_sync;
mod hid_enum_poll;
mod keyboard_lighting;
mod laptop_fan_cap;
mod lighting;
mod messaging;
mod polling;
mod power;
mod session_lock;
mod startup;
mod system;
mod temperature_history;
mod thermal_poll;
mod utils;
mod winui_bridge;

use anyhow::Result;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, Ordering},
    mpsc,
};

use librazer::types::{
    BatteryCare, CpuBoost, FanMode, GpuBoost, LightsAlwaysOn, LogoMode, PerfMode,
};
use librazer::{
    chroma::Rgb,
    command,
    cooling_pad::{CoolingPadDevice, PadLightingMode},
    device::Device,
};
use strum::IntoEnumIterator;

use cooling_pad_auto::{
    CoolingPadAutoInputs, CoolingPadAutoOutput, CoolingPadAutoState, clamp_auto_state_to_limits,
    clear_laptop_follow_smoothing, compute_combined_auto,
};
use cooling_pad_control::CoolingPadFanMode;
use cooling_pad_enforce::CoolingPadEnforceContext;
use cooling_pad_handle::SharedCoolingPad;
use device::CompleteDeviceState;
use device_handle::SharedDevice;
use device_handle::execute_command;
use device_poll::{
    CoolingPadPollSnapshot, DevicePollSnapshot, spawn_cooling_pad_poller, spawn_device_poller,
};
use device_sync::{
    DeviceStatus, StatusApplyContext, StatusApplyOptions, apply_fan_status, apply_state_to_status,
    perf_mode_to_string, string_to_logo_mode, string_to_perf_mode,
};
use hid_enum_poll::{HidEnumMessage, spawn_hid_enum_poller};
use laptop_fan_cap::{LaptopFanCapShared, spawn_laptop_fan_cap_enforcer};
use messaging::{MessageManager, error_message, status_message};
use power::get_power_state;
use session_lock::{DeviceSlot, SessionState, new_device_slot, spawn_session_lock_monitor};
use system::{SystemSpecs, ThermalSnapshot, get_system_specs};
use thermal_poll::spawn_thermal_poller;

// Dynamic app metadata from Cargo
#[cfg(windows)]
const SINGLE_INSTANCE_ID: &str = "R-Helper Compact Controller";
const CONFIG_SAVE_DEBOUNCE: std::time::Duration = std::time::Duration::from_millis(500);

#[derive(Debug, Clone)]
enum InitMessage {
    SystemSpecsComplete(SystemSpecs),
    PowerStateRead(bool),
    InitializationComplete,
    DeviceDetectionComplete(bool),
}

struct Controller {
    status: DeviceStatus,
    device: Option<SharedDevice>,
    device_state: Option<CompleteDeviceState>,
    system_specs: SystemSpecs,
    available_performance_modes: Vec<PerfMode>,
    base_performance_modes: Vec<PerfMode>,

    ac_power: bool,
    ac_profile: CompleteDeviceState,
    battery_profile: CompleteDeviceState,
    auto_switch_enabled: bool,
    lighting_external: bool,

    loading: bool,
    fully_initialized: bool,
    init_receiver: Option<mpsc::Receiver<InitMessage>>,
    poll_receiver: Option<mpsc::Receiver<DevicePollSnapshot>>,
    thermal: ThermalSnapshot,
    poll_brightness_skip: Arc<AtomicBool>,
    polling: Arc<polling::Schedule>,
    panel_visible: bool,
    temperature_history: Arc<Mutex<temperature_history::History>>,
    device_measured_ms: u64,
    message_manager: MessageManager,
    last_fan_enforce_time: std::time::Instant,
    last_fan_reconcile_time: std::time::Instant,
    last_cooling_pad_pull_time: std::time::Instant,
    last_cooling_pad_manual_enforce_time: std::time::Instant,
    status_messages: bool,

    manual_fan_rpm: u16,
    auto_fan_limit_enabled: bool,
    auto_fan_max_rpm: u16,
    auto_fan_cap_override: bool,
    auto_fan_max_rpm_editing: bool,
    temp_brightness_step: usize,
    brightness_slider_active: bool,
    minimize_to_tray: bool,
    run_at_startup: bool,
    #[cfg(windows)]
    single_instance: Option<app_single_instance::PrimaryHandle>,

    init_power_read: bool,
    init_specs_complete: bool,
    cpu_boost: CpuBoost,
    gpu_boost: GpuBoost,
    // Device detection state
    detecting_device: bool,
    device_detection_done: bool,
    device_support: librazer::device_registry::SupportReport,

    hid_enum_receiver: Option<mpsc::Receiver<HidEnumMessage>>,
    cooling_pad_usb_present: bool,

    device_hydrated: bool,
    native_connection_error: Option<String>,
    startup_controls_applied: bool,
    cached_allowed_cpu: Vec<CpuBoost>,
    cached_allowed_gpu: Vec<GpuBoost>,
    cached_disallowed_pairs: Vec<(CpuBoost, GpuBoost)>,

    cooling_pad: Option<SharedCoolingPad>,
    cooling_pad_fan_mode: CoolingPadFanMode,
    cooling_pad_manual_rpm: u16,
    cooling_pad_auto_min_rpm: u16,
    cooling_pad_auto_max_rpm: u16,
    cooling_pad_auto_off_below_c: f32,
    cooling_pad_auto_full_above_c: f32,
    cooling_pad_auto_turn_on_delay_secs: f32,
    cooling_pad_auto_turn_off_delay_secs: f32,
    cooling_pad_auto_overcool_hold_secs: f32,
    cooling_pad_auto_temp_ema_alpha: f32,
    cooling_pad_auto_rpm_slew_up_per_sec: u16,
    cooling_pad_auto_rpm_slew_down_per_sec: u16,
    cooling_pad_auto_follow_temp_margin_c: f32,
    cooling_pad_auto_temp_hysteresis_c: f32,
    cooling_pad_follow_laptop_fan: bool,
    cooling_pad_auto_state: CoolingPadAutoState,
    cooling_pad_lighting_mode: String,
    cooling_pad_color: [u8; 3],
    cooling_pad_brightness_step: usize,
    cooling_pad_brightness_slider_active: bool,
    cooling_pad_chroma_available: bool,
    cooling_pad_poll_receiver: Option<mpsc::Receiver<CoolingPadPollSnapshot>>,
    cooling_pad_poll_brightness_skip: Arc<AtomicBool>,
    cooling_pad_poller_running: Arc<AtomicBool>,
    cooling_pad_ignore_brightness_poll_until: Option<std::time::Instant>,
    last_cooling_pad_fan_enforce_time: std::time::Instant,
    pending_cooling_pad_config_save: Option<std::time::Instant>,
    cooling_pad_enforce: CoolingPadEnforceContext,
    laptop_fan_cap: Arc<Mutex<LaptopFanCapShared>>,
    laptop_fan_cap_enforcer_started: bool,
    shared_thermal: Arc<Mutex<ThermalSnapshot>>,
    last_cooling_pad_sync_time: std::time::Instant,
    session_device_slot: DeviceSlot,
    session_state: Arc<SessionState>,
}

impl Controller {
    fn status_apply_ctx(&mut self) -> StatusApplyContext<'_> {
        StatusApplyContext {
            status: &mut self.status,
            manual_fan_rpm: &mut self.manual_fan_rpm,
            temp_brightness_step: &mut self.temp_brightness_step,
            cpu_boost: &mut self.cpu_boost,
            gpu_boost: &mut self.gpu_boost,
            auto_fan_cap_override: None,
        }
    }

    fn cooling_pad_runtime(&self) -> config::CoolingPadRuntime {
        config::CoolingPadRuntime {
            fan_mode: self.cooling_pad_fan_mode.as_str().to_string(),
            manual_rpm: self.cooling_pad_manual_rpm,
            auto_min_rpm: self.cooling_pad_auto_min_rpm,
            auto_max_rpm: self.cooling_pad_auto_max_rpm,
            auto_off_below_c: self.cooling_pad_auto_off_below_c,
            auto_full_above_c: self.cooling_pad_auto_full_above_c,
            auto_turn_on_delay_secs: self.cooling_pad_auto_turn_on_delay_secs,
            auto_turn_off_delay_secs: self.cooling_pad_auto_turn_off_delay_secs,
            auto_overcool_hold_secs: self.cooling_pad_auto_overcool_hold_secs,
            auto_temp_ema_alpha: self.cooling_pad_auto_temp_ema_alpha,
            auto_rpm_slew_up_per_sec: self.cooling_pad_auto_rpm_slew_up_per_sec,
            auto_rpm_slew_down_per_sec: self.cooling_pad_auto_rpm_slew_down_per_sec,
            auto_follow_temp_margin_c: self.cooling_pad_auto_follow_temp_margin_c,
            auto_temp_hysteresis_c: self.cooling_pad_auto_temp_hysteresis_c,
            follow_laptop_fan: self.cooling_pad_follow_laptop_fan,
            lighting_mode: self.cooling_pad_lighting_mode.clone(),
            color: self.cooling_pad_color,
            brightness_step: self.cooling_pad_brightness_step,
        }
    }

    fn apply_cooling_pad_runtime(&mut self, runtime: config::CoolingPadRuntime) {
        self.cooling_pad_fan_mode =
            cooling_pad_control::CoolingPadFanMode::from_config(&runtime.fan_mode);
        self.cooling_pad_manual_rpm = runtime.manual_rpm;
        self.cooling_pad_auto_min_rpm = runtime.auto_min_rpm;
        self.cooling_pad_auto_max_rpm = runtime.auto_max_rpm;
        self.cooling_pad_auto_off_below_c = runtime.auto_off_below_c;
        self.cooling_pad_auto_full_above_c = runtime.auto_full_above_c;
        self.cooling_pad_auto_turn_on_delay_secs = runtime.auto_turn_on_delay_secs;
        self.cooling_pad_auto_turn_off_delay_secs = runtime.auto_turn_off_delay_secs;
        self.cooling_pad_auto_overcool_hold_secs = runtime.auto_overcool_hold_secs;
        self.cooling_pad_auto_temp_ema_alpha = runtime.auto_temp_ema_alpha;
        self.cooling_pad_auto_rpm_slew_up_per_sec = runtime.auto_rpm_slew_up_per_sec;
        self.cooling_pad_auto_rpm_slew_down_per_sec = runtime.auto_rpm_slew_down_per_sec;
        self.cooling_pad_auto_follow_temp_margin_c = runtime.auto_follow_temp_margin_c;
        self.cooling_pad_auto_temp_hysteresis_c = runtime.auto_temp_hysteresis_c;
        self.cooling_pad_follow_laptop_fan = runtime.follow_laptop_fan;
        self.cooling_pad_lighting_mode = runtime.lighting_mode;
        self.cooling_pad_color = runtime.color;
        self.cooling_pad_brightness_step = runtime.brightness_step;
    }

    fn sync_laptop_fan_cap(&mut self) {
        if let Ok(mut cap) = self.laptop_fan_cap.lock() {
            let user_auto = self.is_user_auto_mode();
            cap.limit_enabled = self.auto_fan_limit_enabled && user_auto;
            cap.max_rpm = self.auto_fan_max_rpm;
            cap.skip = self.auto_fan_max_rpm_editing;
            if !cap.limit_enabled {
                cap.cap_active = false;
            }
        }
    }

    fn sync_cooling_pad_enforce(&mut self) {
        let session_locked = self.session_state.locked.load(Ordering::Relaxed);
        if let Ok(mut settings) = self.cooling_pad_enforce.settings.lock() {
            settings.active = self.cooling_pad.is_some();
            settings.fully_initialized = self.fully_initialized && !self.loading;
            if !session_locked {
                settings.fan_mode = self.cooling_pad_fan_mode;
            }
            settings.manual_rpm = self.cooling_pad_manual_rpm;
            settings.auto_min_rpm = self.cooling_pad_auto_min_rpm;
            settings.auto_max_rpm = self.cooling_pad_auto_max_rpm;
            settings.auto_off_below_c = self.cooling_pad_auto_off_below_c;
            settings.auto_full_above_c = self.cooling_pad_auto_full_above_c;
            settings.auto_turn_on_delay_secs = self.cooling_pad_auto_turn_on_delay_secs;
            settings.auto_turn_off_delay_secs = self.cooling_pad_auto_turn_off_delay_secs;
            settings.auto_overcool_hold_secs = self.cooling_pad_auto_overcool_hold_secs;
            settings.auto_temp_ema_alpha = self.cooling_pad_auto_temp_ema_alpha;
            settings.auto_rpm_slew_up_per_sec = self.cooling_pad_auto_rpm_slew_up_per_sec;
            settings.auto_rpm_slew_down_per_sec = self.cooling_pad_auto_rpm_slew_down_per_sec;
            settings.auto_follow_temp_margin_c = self.cooling_pad_auto_follow_temp_margin_c;
            settings.auto_temp_hysteresis_c = self.cooling_pad_auto_temp_hysteresis_c;
            settings.laptop_fan_follow_enabled = self.cooling_pad_follow_laptop_fan;
            let min = settings.auto_min_rpm.clamp(
                librazer::cooling_pad::MIN_RPM,
                librazer::cooling_pad::MAX_RPM,
            );
            let max = settings
                .auto_max_rpm
                .clamp(min, librazer::cooling_pad::MAX_RPM);
            clamp_auto_state_to_limits(&mut settings.auto_state, min, max);
        }
        if let Ok(mut pad) = self.cooling_pad_enforce.pad.lock() {
            *pad = self.cooling_pad.clone();
        }
    }

    fn sync_cooling_pad_enforce_auto_state(&self) {
        if let Ok(mut settings) = self.cooling_pad_enforce.settings.lock() {
            settings.auto_state = self.cooling_pad_auto_state;
        }
    }

    fn reset_cooling_pad_auto_state(&mut self) {
        self.cooling_pad_auto_state = CoolingPadAutoState::default();
        if let Ok(mut settings) = self.cooling_pad_enforce.settings.lock() {
            settings.auto_state = CoolingPadAutoState::default();
        }
    }

    fn pull_cooling_pad_enforce_state(&mut self) {
        if let Ok(settings) = self.cooling_pad_enforce.settings.lock() {
            self.cooling_pad_auto_state = settings.auto_state;
        }
    }

    fn shared_laptop_fan_rpm_for_pad(&self) -> Option<u16> {
        if !self.cooling_pad_follow_laptop_fan {
            return None;
        }
        if let Ok(guard) = self.cooling_pad_enforce.laptop_fan_rpm.lock() {
            return *guard;
        }
        self.status.fan_actual_rpm
    }

    fn string_to_perf_mode(mode: &str) -> Option<PerfMode> {
        string_to_perf_mode(mode)
    }

    fn string_to_logo_mode(mode: &str) -> Option<LogoMode> {
        string_to_logo_mode(mode)
    }

    fn is_user_auto_mode(&self) -> bool {
        matches!(self.status.fan_mode, Some(FanMode::Auto))
    }

    fn apply_device_fan_status(&mut self, fan_mode: FanMode, set_rpm: Option<u16>) -> bool {
        let is_user_auto = self.is_user_auto_mode();
        apply_fan_status(
            &mut self.status_apply_ctx(),
            fan_mode,
            set_rpm,
            is_user_auto,
        )
    }

    fn read_current_fan_state(device: &Device) -> (FanMode, Option<u16>) {
        // Read the current fan mode from the combined perf/fan query.
        // (We intentionally avoid a second immediate retry; caller logic tolerates fallback to Auto.)
        let fan_mode = command::get_perf_mode(device)
            .map(|(_, fm)| fm)
            .unwrap_or_else(|_| {
                eprintln!("Warning: Failed to read device fan mode, assuming Auto");
                FanMode::Auto
            });
        let set_rpm = get_fan_rpm_set(device, librazer::types::FanZone::Zone1);
        (fan_mode, set_rpm)
    }

    fn set_no_device_message(&mut self) {
        self.set_status_message("No device connected".to_string());
    }

    fn new(session_device_slot: DeviceSlot, session_state: Arc<SessionState>) -> Self {
        let device_support = Device::support_report();
        let app_config = config::AppConfig::load();
        let ac_profile = app_config.ac_profile;
        let battery_profile = app_config.battery_profile;
        let auto_switch_enabled = app_config.auto_switch_enabled;
        let auto_fan_limit_enabled = app_config.auto_fan_limit_enabled;
        let auto_fan_max_rpm = app_config.auto_fan_max_rpm;
        let status_messages = app_config.debug_enabled;
        let minimize_to_tray = true; // Compact always lives in the tray.
        let run_at_startup = app_config.run_at_startup;
        let cooling_pad_cfg = config::CoolingPadRuntime::from(&app_config.cooling_pad);
        if device_support.supported {
            if let Err(e) = startup::set_startup_enabled(run_at_startup) {
                eprintln!("Failed to sync run-at-startup with registry: {}", e);
            }
        }

        let (init_sender, init_receiver) = mpsc::channel();
        let shared_thermal = Arc::new(Mutex::new(ThermalSnapshot::default()));

        let now = std::time::Instant::now();
        let mut app = Self {
            status: DeviceStatus::default(),
            device: None,
            device_state: None,
            system_specs: SystemSpecs::default(),
            available_performance_modes: Vec::new(),
            base_performance_modes: Vec::new(),
            ac_power: true,
            ac_profile,
            battery_profile,
            auto_switch_enabled,
            lighting_external: app_config.lighting_external,
            loading: true,
            fully_initialized: false,
            init_receiver: Some(init_receiver),
            poll_receiver: None,
            thermal: ThermalSnapshot::default(),
            poll_brightness_skip: Arc::new(AtomicBool::new(false)),
            polling: Arc::new(polling::Schedule::default()),
            panel_visible: false,
            temperature_history: Arc::new(Mutex::new(temperature_history::History::default())),
            device_measured_ms: 0,
            message_manager: MessageManager::new(),
            last_fan_enforce_time: std::time::Instant::now(),
            last_fan_reconcile_time: std::time::Instant::now(),
            last_cooling_pad_pull_time: std::time::Instant::now(),
            last_cooling_pad_manual_enforce_time: std::time::Instant::now(),
            status_messages,

            manual_fan_rpm: 2000,
            auto_fan_limit_enabled,
            auto_fan_max_rpm,
            auto_fan_cap_override: false,
            auto_fan_max_rpm_editing: false,
            temp_brightness_step: 0,
            brightness_slider_active: false,

            minimize_to_tray,
            run_at_startup,
            #[cfg(windows)]
            single_instance: None,

            init_power_read: false,
            init_specs_complete: false,
            cpu_boost: CpuBoost::Low,
            gpu_boost: GpuBoost::Low,
            detecting_device: true,
            device_detection_done: false,
            device_support,

            hid_enum_receiver: None,
            cooling_pad_usb_present: false,

            device_hydrated: false,
            native_connection_error: None,
            startup_controls_applied: false,
            cached_allowed_cpu: vec![
                CpuBoost::Low,
                CpuBoost::Medium,
                CpuBoost::High,
                CpuBoost::Boost,
            ],
            cached_allowed_gpu: vec![GpuBoost::Low, GpuBoost::Medium, GpuBoost::High],
            cached_disallowed_pairs: Vec::new(),

            cooling_pad: None,
            cooling_pad_fan_mode: CoolingPadFanMode::Off,
            cooling_pad_manual_rpm: 0,
            cooling_pad_auto_min_rpm: 0,
            cooling_pad_auto_max_rpm: 0,
            cooling_pad_auto_off_below_c: 0.0,
            cooling_pad_auto_full_above_c: 0.0,
            cooling_pad_auto_turn_on_delay_secs: 0.0,
            cooling_pad_auto_turn_off_delay_secs: 0.0,
            cooling_pad_auto_overcool_hold_secs: 0.0,
            cooling_pad_auto_temp_ema_alpha: 0.0,
            cooling_pad_auto_rpm_slew_up_per_sec: 0,
            cooling_pad_auto_rpm_slew_down_per_sec: 0,
            cooling_pad_auto_follow_temp_margin_c: 0.0,
            cooling_pad_auto_temp_hysteresis_c: 0.0,
            cooling_pad_follow_laptop_fan: true,
            cooling_pad_auto_state: CoolingPadAutoState::default(),
            cooling_pad_lighting_mode: String::new(),
            cooling_pad_color: [0, 0, 0],
            cooling_pad_brightness_step: 0,
            cooling_pad_brightness_slider_active: false,
            cooling_pad_chroma_available: false,
            cooling_pad_poll_receiver: None,
            cooling_pad_poll_brightness_skip: Arc::new(AtomicBool::new(false)),
            cooling_pad_poller_running: Arc::new(AtomicBool::new(false)),
            cooling_pad_ignore_brightness_poll_until: None,
            last_cooling_pad_fan_enforce_time: std::time::Instant::now(),
            pending_cooling_pad_config_save: None,
            cooling_pad_enforce: CoolingPadEnforceContext::start(Arc::clone(&shared_thermal)),
            laptop_fan_cap: Arc::new(Mutex::new(LaptopFanCapShared::default())),
            laptop_fan_cap_enforcer_started: false,
            shared_thermal,
            last_cooling_pad_sync_time: now,
            session_device_slot,
            session_state,
        };

        app.apply_cooling_pad_runtime(cooling_pad_cfg);
        app.sync_cooling_pad_enforce();

        // Kick off async device detection
        app.start_device_detection(init_sender.clone());

        // Start other background initialization (power state, system specs)
        app.start_background_initialization(init_sender);
        app.start_thermal_poller();
        app.start_hid_enum_poller();

        app
    }

    fn start_hid_enum_poller(&mut self) {
        if !self.device_support.supported {
            return;
        }
        let (tx, rx) = mpsc::channel();
        self.hid_enum_receiver = Some(rx);
        spawn_hid_enum_poller(tx);
    }

    fn process_hid_enum_messages(&mut self) -> bool {
        let mut messages = Vec::new();
        if let Some(ref rx) = self.hid_enum_receiver {
            while let Ok(message) = rx.try_recv() {
                messages.push(message);
            }
        }

        let mut changed = false;
        for message in messages {
            match message {
                HidEnumMessage::CoolingPadPresent(present) => {
                    if self.cooling_pad_usb_present != present {
                        self.cooling_pad_usb_present = present;
                        changed = true;
                    }
                    if present && self.cooling_pad.is_none() {
                        self.try_attach_cooling_pad();
                    } else if !present && self.cooling_pad.is_some() {
                        self.detach_cooling_pad();
                    }
                }
            }
        }

        if self.cooling_pad.is_some()
            && self.cooling_pad_usb_present
            && self.cooling_pad_needs_detach()
        {
            self.detach_cooling_pad();
            changed = true;
        }
        if changed {}
        changed
    }

    fn start_thermal_poller(&mut self) {
        spawn_thermal_poller(
            Arc::clone(&self.polling),
            Arc::clone(&self.shared_thermal),
            Arc::clone(&self.temperature_history),
        );
    }

    fn refresh_thermal_from_shared(&mut self) -> bool {
        let snapshot = crate::thermal_poll::read_shared_thermal(&self.shared_thermal);
        if snapshot == self.thermal {
            return false;
        }
        self.thermal = snapshot;

        true
    }

    fn maybe_sync_cooling_pad_enforce(&mut self, interval_secs: f32) {
        if self.last_cooling_pad_sync_time.elapsed().as_secs_f32() >= interval_secs {
            self.sync_cooling_pad_enforce();
            self.last_cooling_pad_sync_time = std::time::Instant::now();
        }
    }

    fn process_thermal_snapshots(&mut self) -> bool {
        self.refresh_thermal_from_shared()
    }

    fn start_device_detection(&mut self, sender: mpsc::Sender<InitMessage>) {
        self.detecting_device = true;
        std::thread::spawn(move || {
            let present = match Device::enumerate() {
                Ok(_) => true,
                Err(e) => {
                    eprintln!("No Razer laptop detected: {}", e);
                    false
                }
            };
            let _ = sender.send(InitMessage::DeviceDetectionComplete(present));
        });
    }

    fn detect_available_performance_modes(&mut self) {
        if let Some(device) = self.device.as_ref() {
            if let Some(list) = device.with(|d| d.info().perf_modes.clone()).flatten() {
                self.available_performance_modes = list;
                if self.base_performance_modes.is_empty() {
                    self.base_performance_modes = self.available_performance_modes.clone();
                }
                return;
            }
        }
        self.available_performance_modes = PerfMode::iter().collect();
        if self.base_performance_modes.is_empty() {
            self.base_performance_modes = self.available_performance_modes.clone();
        }
    }

    fn cache_performance_metadata(&mut self) {
        self.detect_available_performance_modes();
        if let Some(device) = self.device.as_ref() {
            device.with(|d| {
                let info = d.info();
                self.cached_allowed_cpu = info.cpu_boosts.clone().unwrap_or_else(|| {
                    vec![
                        CpuBoost::Low,
                        CpuBoost::Medium,
                        CpuBoost::High,
                        CpuBoost::Boost,
                    ]
                });
                self.cached_allowed_gpu = info
                    .gpu_boosts
                    .clone()
                    .unwrap_or_else(|| vec![GpuBoost::Low, GpuBoost::Medium, GpuBoost::High]);
                self.cached_disallowed_pairs = info.disallowed_boost_pairs.clone();
            });
        }
    }

    fn apply_complete_state_to_status(
        &mut self,
        state: &CompleteDeviceState,
        fan_actual_rpm: Option<u16>,
    ) {
        let is_user_auto = self.is_user_auto_mode();
        apply_state_to_status(
            &mut self.status_apply_ctx(),
            state,
            fan_actual_rpm,
            StatusApplyOptions::default(),
            is_user_auto,
        );
    }

    fn hydrate_from_device(&mut self) -> Result<()> {
        if self.device_hydrated {
            return Ok(());
        }
        let (state, fan_actual_rpm) = {
            let shared = self
                .device
                .as_ref()
                .ok_or_else(|| anyhow::anyhow!("no device connected"))?;
            shared
                .with(|device| {
                    let state = CompleteDeviceState::read_from_device(device)?;
                    let fan_actual_rpm =
                        get_fan_rpm_actual(device, librazer::types::FanZone::Zone1);
                    Ok::<_, anyhow::Error>((state, fan_actual_rpm))
                })
                .ok_or_else(|| anyhow::anyhow!("device busy"))??
        };
        self.apply_complete_state_to_status(&state, fan_actual_rpm);
        self.device_state = Some(state);
        self.device_hydrated = true;
        self.reconcile_auto_fan_cap_state();
        Ok(())
    }

    fn active_profile_fan_mode(&self) -> FanMode {
        let profile = if self.ac_power {
            &self.ac_profile
        } else {
            &self.battery_profile
        };
        profile.fan_mode
    }

    /// Safety net when the cap enforcer released but hardware stayed Manual@max.
    fn reconcile_auto_fan_cap_state(&mut self) {
        if !self.auto_fan_limit_enabled || self.auto_fan_max_rpm_editing {
            return;
        }

        let cap_active = self
            .laptop_fan_cap
            .lock()
            .map(|cap| cap.cap_active)
            .unwrap_or(false);
        if self.auto_fan_cap_override != cap_active {
            self.auto_fan_cap_override = cap_active;
        }

        if cap_active {
            return;
        }

        let Some(device) = self.device.as_ref() else {
            return;
        };

        let Some((fan_mode, set_rpm)) = device.try_with(|d| Self::read_current_fan_state(d)) else {
            return;
        };

        if fan_mode != FanMode::Manual || set_rpm != Some(self.auto_fan_max_rpm) {
            return;
        }

        if self.is_user_auto_mode() || self.active_profile_fan_mode() == FanMode::Auto {
            self.restore_auto_fan_mode();
        }
    }

    fn on_device_attached(&mut self) {
        if let Some(dev) = self.device.as_ref() {
            if let Ok(mut slot) = self.session_device_slot.lock() {
                *slot = Some(dev.clone());
            }
            dev.with(|d| {
                let info = d.info();
                self.system_specs.device_model =
                    info.display_name.clone();
            });
        }
        self.cache_performance_metadata();
        self.start_device_poller();
        self.apply_startup_controls();
    }

    /// Push saved laptop profile, fan cap, and cooling pad settings to hardware once
    /// both background init and device detection have finished (fixes autostart / boot gaps).
    fn apply_startup_controls(&mut self) {
        if !self.fully_initialized || self.device.is_none() || self.startup_controls_applied {
            return;
        }

        if self.auto_switch_enabled {
            self.auto_switch_profile();
            self.device_hydrated = true;
        } else if !self.device_hydrated {
            if let Err(e) = self.hydrate_from_device() {
                self.set_error_message(format!("Failed to read device status: {}", e));
            }
        }

        self.sync_laptop_fan_cap();
        if self.cooling_pad.is_some() {
            self.apply_cooling_pad_config_to_device();
        }
        self.sync_cooling_pad_enforce();
        self.startup_controls_applied = true;
    }

    fn start_device_poller(&mut self) {
        if self.poll_receiver.is_some() {
            return;
        }
        let Some(device) = self.device.as_ref() else {
            return;
        };
        let (tx, rx) = mpsc::channel();
        self.poll_receiver = Some(rx);
        spawn_device_poller(
            device.arc(),
            tx,
            Arc::clone(&self.poll_brightness_skip),
            Arc::clone(&self.polling),
            Arc::clone(&self.cooling_pad_enforce.laptop_fan_rpm),
        );
        if !self.laptop_fan_cap_enforcer_started {
            spawn_laptop_fan_cap_enforcer(
                device.arc(),
                Arc::clone(&self.laptop_fan_cap),
                Arc::clone(&self.shared_thermal),
            );
            self.laptop_fan_cap_enforcer_started = true;
        }
    }

    fn try_attach_cooling_pad(&mut self) {
        if !self.device_support.supported || self.device.is_none() {
            return;
        }
        if self.cooling_pad.is_some() {
            return;
        }
        match CoolingPadDevice::detect() {
            Ok(pad) => {
                self.cooling_pad_chroma_available = pad.chroma_available();
                self.cooling_pad = Some(SharedCoolingPad::new(pad));
                self.start_cooling_pad_poller();
                self.apply_cooling_pad_config_to_device();
                self.sync_cooling_pad_enforce();
                self.set_optional_status_message("Cooling pad connected".into());
            }
            Err(e) => {
                if self.cooling_pad_usb_present {
                    eprintln!("Cooling pad detected but failed to open: {e}");
                }
            }
        }
    }

    fn start_cooling_pad_poller(&mut self) {
        if self.cooling_pad_poll_receiver.is_some() {
            return;
        }
        let Some(pad) = self.cooling_pad.as_ref() else {
            return;
        };
        self.cooling_pad_poller_running
            .store(true, Ordering::Relaxed);
        let (tx, rx) = mpsc::channel();
        self.cooling_pad_poll_receiver = Some(rx);
        spawn_cooling_pad_poller(
            pad.arc(),
            tx,
            Arc::clone(&self.cooling_pad_poll_brightness_skip),
            Arc::clone(&self.polling),
            Arc::clone(&self.cooling_pad_poller_running),
        );
    }

    fn process_cooling_pad_poll_snapshots(&mut self) -> bool {
        let mut snapshots = Vec::new();
        if let Some(ref rx) = self.cooling_pad_poll_receiver {
            while let Ok(snapshot) = rx.try_recv() {
                snapshots.push(snapshot);
            }
        }
        if snapshots.is_empty() {
            return false;
        }
        let Some(snapshot) = snapshots.into_iter().last() else {
            return false;
        };
        if self.apply_cooling_pad_poll_snapshot(snapshot) {
            true
        } else {
            false
        }
    }

    fn mark_cooling_pad_lighting_changed(&mut self) {
        self.cooling_pad_ignore_brightness_poll_until =
            Some(std::time::Instant::now() + std::time::Duration::from_secs(2));
    }

    fn apply_cooling_pad_poll_snapshot(&mut self, snapshot: CoolingPadPollSnapshot) -> bool {
        let ignore_brightness = self
            .cooling_pad_ignore_brightness_poll_until
            .is_some_and(|until| std::time::Instant::now() < until);

        let mut changed = false;
        if let Some(brightness) = snapshot.brightness {
            if !ignore_brightness && !self.cooling_pad_brightness_slider_active {
                let step = lighting::raw_brightness_to_step_index(brightness);
                if step != self.cooling_pad_brightness_step {
                    self.cooling_pad_brightness_step = step;
                    changed = true;
                }
            }
        }

        if self
            .cooling_pad_ignore_brightness_poll_until
            .is_some_and(|until| std::time::Instant::now() >= until)
        {
            self.cooling_pad_ignore_brightness_poll_until = None;
        }
        changed
    }

    fn cooling_pad_display_rpm(&self) -> Option<u16> {
        match self.cooling_pad_fan_mode {
            CoolingPadFanMode::Off => None,
            CoolingPadFanMode::Manual => Some(self.cooling_pad_manual_rpm),
            CoolingPadFanMode::Auto => {
                if self.cooling_pad_auto_state.fan_running {
                    self.cooling_pad_auto_state.last_rpm
                } else {
                    None
                }
            }
        }
    }

    fn compute_cooling_pad_auto_output(&mut self) -> CoolingPadAutoOutput {
        let dt = self
            .last_cooling_pad_fan_enforce_time
            .elapsed()
            .as_secs_f32()
            .clamp(0.05, 3.0);
        let thermal = crate::thermal_poll::read_shared_thermal(&self.shared_thermal);
        let inputs = CoolingPadAutoInputs {
            cpu_temp_c: thermal.cpu_avg_c,
            gpu_temp_c: thermal.gpu_avg_c,
            laptop_fan_actual_rpm: self.shared_laptop_fan_rpm_for_pad(),
            min_rpm: self.cooling_pad_auto_min_rpm,
            max_rpm: self.cooling_pad_auto_max_rpm,
            off_below_c: self.cooling_pad_auto_off_below_c,
            full_above_c: self.cooling_pad_auto_full_above_c,
            temp_hysteresis_c: self.cooling_pad_auto_temp_hysteresis_c,
            dt_secs: dt,
            turn_on_delay_secs: self.cooling_pad_auto_turn_on_delay_secs,
            turn_off_delay_secs: self.cooling_pad_auto_turn_off_delay_secs,
            overcool_hold_secs: self.cooling_pad_auto_overcool_hold_secs,
            temp_ema_alpha: self.cooling_pad_auto_temp_ema_alpha,
            rpm_slew_up_per_sec: self.cooling_pad_auto_rpm_slew_up_per_sec,
            rpm_slew_down_per_sec: self.cooling_pad_auto_rpm_slew_down_per_sec,
            follow_temp_margin_c: self.cooling_pad_auto_follow_temp_margin_c,
            laptop_fan_follow_enabled: self.cooling_pad_follow_laptop_fan,
        };
        compute_combined_auto(&inputs, &mut self.cooling_pad_auto_state)
    }

    fn schedule_cooling_pad_redetect(&mut self) {
        self.redetect_cooling_pad();
    }

    fn redetect_cooling_pad(&mut self) {
        if !self.cooling_pad_usb_present {
            return;
        }
        if self.cooling_pad.is_some() {
            self.cooling_pad_poller_running
                .store(false, Ordering::Relaxed);
            self.cooling_pad_poll_receiver = None;
            self.cooling_pad = None;
            self.cooling_pad_chroma_available = false;
            self.sync_cooling_pad_enforce();
        }
        self.try_attach_cooling_pad();
    }

    fn detach_cooling_pad(&mut self) {
        if self.cooling_pad.is_none() {
            return;
        }
        self.cooling_pad_poller_running
            .store(false, Ordering::Relaxed);
        self.cooling_pad_poll_receiver = None;
        self.cooling_pad = None;
        self.cooling_pad_chroma_available = false;
        self.sync_cooling_pad_enforce();
        self.schedule_cooling_pad_redetect();
        self.set_optional_status_message("Cooling pad disconnected".into());
    }

    fn cooling_pad_needs_detach(&self) -> bool {
        let Some(pad) = self.cooling_pad.as_ref() else {
            return false;
        };
        !self.cooling_pad_usb_present || !pad.with(|p| p.is_responsive()).unwrap_or(false)
    }

    fn process_poll_snapshots(&mut self) -> bool {
        let mut snapshots = Vec::new();
        if let Some(ref rx) = self.poll_receiver {
            while let Ok(snapshot) = rx.try_recv() {
                snapshots.push(snapshot);
            }
        }
        if snapshots.is_empty() {
            return false;
        }
        let Some(snapshot) = snapshots.into_iter().last() else {
            return false;
        };
        if self.apply_poll_snapshot(snapshot) {
            true
        } else {
            false
        }
    }

    fn apply_poll_snapshot(&mut self, snapshot: DevicePollSnapshot) -> bool {
        self.device_measured_ms = snapshot.measured_ms;
        let mut changed = self.status.perf_mode != snapshot.perf_mode;
        if self.native_connection_error != snapshot.connection_error {
            if let Some(error) = &snapshot.connection_error {
                eprintln!("{error}");
            }
            self.native_connection_error = snapshot.connection_error.clone();
            changed = true;
        }
        self.status.perf_mode = snapshot.perf_mode;
        if snapshot.perf_mode.is_some() {
            changed |= self.apply_device_fan_status(snapshot.fan_mode, snapshot.fan_set_rpm);
        } else {
            self.status.fan_mode = None;
            self.status.fan_rpm = None;
        }
        if snapshot.ac_power != self.ac_power {
            self.ac_power = snapshot.ac_power;
            self.auto_switch_profile();
            self.schedule_cooling_pad_redetect();
            changed = true;
        }

        if self.status.fan_actual_rpm != snapshot.fan_actual_rpm {
            self.status.fan_actual_rpm = snapshot.fan_actual_rpm;
            changed = true;
        }
        if let Ok(cap) = self.laptop_fan_cap.lock() {
            if self.auto_fan_cap_override != cap.cap_active {
                self.auto_fan_cap_override = cap.cap_active;
                changed = true;
            }
        }
        if self.status.fan_mode == Some(FanMode::Manual) {
            if self.apply_device_fan_status(snapshot.fan_mode, snapshot.fan_set_rpm) {
                changed = true;
            }
        } else if self.auto_fan_limit_enabled
            && snapshot.fan_mode == FanMode::Manual
            && snapshot.fan_set_rpm == Some(self.auto_fan_max_rpm)
        {
            self.reconcile_auto_fan_cap_state();
        }

        if let Some(brightness) = snapshot.keyboard_brightness {
            if !self.brightness_slider_active && brightness != self.status.keyboard_brightness {
                self.status.keyboard_brightness = brightness;
                self.temp_brightness_step = lighting::raw_brightness_to_step_index(brightness);
                changed = true;
            }
        }

        if snapshot.optional_reads && self.status.logo_mode != snapshot.logo_mode {
            self.status.logo_mode = snapshot.logo_mode;
            changed = true;
        }
        if let Some(value) = snapshot.lights_always_on {
            if self.status.lights_always_on != value {
                self.status.lights_always_on = value;
                changed = true;
            }
        }
        if let Some(value) = snapshot.battery_care {
            if self.status.battery_care != value {
                self.status.battery_care = value;
                changed = true;
            }
        }

        if let Some(full_state) = snapshot.full_state {
            if self.apply_external_state_change(full_state) {
                changed = true;
            }
        }
        changed
    }

    fn shutdown_cooling_pad(&mut self) {
        self.cooling_pad_enforce.stop();
        if let Some(pad) = self.cooling_pad.as_ref() {
            let _ = pad.with(|p| p.fan_off());
        }
    }

    fn apply_external_state_change(&mut self, current_state: CompleteDeviceState) -> bool {
        if let Some(ref stored_state) = self.device_state {
            if current_state == *stored_state {
                return false;
            }

            let old_perf_mode = perf_mode_to_string(stored_state.perf_mode);
            let new_perf_mode = perf_mode_to_string(current_state.perf_mode);

            self.device_state = Some(current_state.clone());
            let is_user_auto = self.is_user_auto_mode();
            apply_state_to_status(
                &mut self.status_apply_ctx(),
                &current_state,
                None,
                StatusApplyOptions {
                    respect_user_auto_mode: true,
                    ..StatusApplyOptions::default()
                },
                is_user_auto,
            );
            self.status.perf_mode = Some(current_state.perf_mode);

            if old_perf_mode != new_perf_mode {
                self.set_optional_status_message("Mode updated".to_string());
            } else if self.status_messages {
                self.set_optional_status_message("Device state updated externally".to_string());
            }
            true
        } else {
            self.device_state = Some(current_state);
            true
        }
    }

    fn start_background_initialization(&mut self, sender: mpsc::Sender<InitMessage>) {
        // Device may not be known yet; pass None here. We'll still display specs we can read.
        let device_name: Option<String> = None;

        std::thread::spawn(move || {
            if let Ok(ac_power) = get_power_state() {
                let _ = sender.send(InitMessage::PowerStateRead(ac_power));
            }

            let _ = sender.send(InitMessage::InitializationComplete);

            let device_name_ref = device_name.as_deref();
            let system_specs = get_system_specs(device_name_ref, None);
            let _ = sender.send(InitMessage::SystemSpecsComplete(system_specs));
        });

        self.loading = false;
    }

    fn process_background_initialization(&mut self) {
        let mut messages_to_process = Vec::new();
        // Drain all pending init messages this frame (non-blocking).

        if let Some(ref receiver) = self.init_receiver {
            while let Ok(message) = receiver.try_recv() {
                messages_to_process.push(message);
            }
        }

        for message in messages_to_process {
            match message {
                InitMessage::DeviceDetectionComplete(present) => {
                    self.device_detection_done = true;
                    // If a device is found, switch immediately. If not, keep detecting until grace expires.
                    if present {
                        self.detecting_device = false;
                    }
                    if present {
                        // Full detect runs once on the UI thread (HID handle is not Send).
                        self.device_support = Device::support_report();
                        match Device::detect() {
                            Ok(dev) => {
                                self.native_connection_error = None;
                                self.device = Some(SharedDevice::new(dev));
                                self.on_device_attached();
                                self.set_status_message("Initializing...".to_string());
                            }
                            Err(error) => {
                                let message = format!("HID: {error:#}");
                                eprintln!("{message}");
                                self.native_connection_error = Some(message.clone());
                                self.set_error_message(message);
                            }
                        }
                        self.try_attach_cooling_pad();
                    }
                }
                InitMessage::SystemSpecsComplete(specs) => {
                    if let Some(device) = self.device.as_ref() {
                        device.with(|d| {
                            let info = d.info();
                            self.system_specs.device_model =
                                info.display_name.clone();
                        });
                    } else {
                        self.system_specs.device_model = specs.device_model;
                    }
                    self.system_specs.cpu_name = specs.cpu_name;
                    self.system_specs.ram_gb = specs.ram_gb;
                    self.system_specs.gpu_models = specs.gpu_models;
                    self.init_specs_complete = true;
                    if self.fully_initialized && self.init_power_read && self.init_specs_complete {
                        // Avoid flashing a transient message on unsupported devices.
                        if self.device.is_some() {
                            self.set_status_message("Initialization complete".to_string());
                        } else {
                            self.set_optional_status_message("Initialization complete".to_string());
                        }
                    } else {
                        self.set_optional_status_message(
                            "System specifications loaded".to_string(),
                        );
                    }
                }
                InitMessage::PowerStateRead(ac_power) => {
                    self.ac_power = ac_power;
                    self.init_power_read = true;
                }
                InitMessage::InitializationComplete => {
                    self.fully_initialized = true;
                    self.sync_cooling_pad_enforce();
                    self.try_attach_cooling_pad();
                    self.apply_startup_controls();
                }
            }
        }
    }
}

fn get_fan_rpm_actual(device: &Device, zone: librazer::types::FanZone) -> Option<u16> {
    match command::get_fan_actual_rpm(device, zone) {
        Ok(rpm) => Some(rpm),
        Err(_) => None,
    }
}

fn get_fan_rpm_set(device: &Device, zone: librazer::types::FanZone) -> Option<u16> {
    match command::get_fan_rpm(device, zone) {
        Ok(rpm) => Some(rpm),
        Err(_) => None,
    }
}

impl Controller {
    fn set_status_message(&mut self, message: String) {
        self.message_manager.add_message(status_message(message));
    }

    fn set_optional_status_message(&mut self, message: String) {
        if self.status_messages {
            self.message_manager.add_message(status_message(message));
        }
    }

    fn set_error_message(&mut self, message: String) {
        self.message_manager.add_message(error_message(message));
    }

    fn update_stored_device_state(&mut self) {
        if let Some(device) = self.device.as_ref() {
            if let Some(Ok(current_state)) =
                device.with(|d| CompleteDeviceState::read_from_device(d))
            {
                self.device_state = Some(current_state);
            }
        }
    }

    fn auto_switch_profile(&mut self) {
        if !self.auto_switch_enabled {
            return;
        }

        let Some(device) = self.device.as_ref() else {
            return;
        };

        let target_profile = if self.ac_power {
            self.ac_profile.clone()
        } else {
            self.battery_profile.clone()
        };
        let profile_name = if self.ac_power { "AC" } else { "Battery" };

        let apply_result =
            device.with(|d| target_profile.apply_to_device(d, self.lighting_external));
        match apply_result {
            Some(Ok(())) => {
                self.sync_ui_after_profile_apply(&target_profile);
                self.update_stored_device_state();
                self.set_status_message(format!("Auto-switched to {} settings", profile_name));
            }
            Some(Err(e)) => {
                self.set_error_message(format!(
                    "Failed to switch to {} profile: {}",
                    profile_name, e
                ));
            }
            None => {
                self.set_error_message(format!(
                    "Failed to switch to {} profile: device busy",
                    profile_name
                ));
            }
        }
    }

    fn sync_ui_after_profile_apply(&mut self, profile: &CompleteDeviceState) {
        self.auto_fan_cap_override = false;
        apply_state_to_status(
            &mut self.status_apply_ctx(),
            profile,
            None,
            StatusApplyOptions::default(),
            false,
        );

        if let Some(device) = self.device.as_ref() {
            self.status.fan_actual_rpm = device
                .with(|d| get_fan_rpm_actual(d, librazer::types::FanZone::Zone1))
                .flatten();
        }
    }

    fn persist_config(&self) -> Result<()> {
        let config = config::AppConfig {
            version: 1,
            auto_switch_enabled: self.auto_switch_enabled,
            lighting_external: self.lighting_external,
            ac_profile: self.ac_profile.clone(),
            battery_profile: self.battery_profile.clone(),
            auto_fan_limit_enabled: self.auto_fan_limit_enabled,
            auto_fan_max_rpm: self.auto_fan_max_rpm,
            debug_enabled: self.status_messages,
            minimize_to_tray: self.minimize_to_tray,
            run_at_startup: self.run_at_startup,
            cooling_pad: self.cooling_pad_runtime().into(),
        };
        config.save()
    }

    fn save_cooling_pad_config(&mut self) {
        self.pending_cooling_pad_config_save = None;
        if let Err(e) = self.persist_config() {
            self.set_error_message(format!("Failed to save cooling pad settings: {e}"));
        }
    }

    fn schedule_cooling_pad_config_save(&mut self) {
        self.pending_cooling_pad_config_save =
            Some(std::time::Instant::now() + CONFIG_SAVE_DEBOUNCE);
    }

    fn flush_pending_cooling_pad_config_save(&mut self) {
        let due = self
            .pending_cooling_pad_config_save
            .is_some_and(|deadline| std::time::Instant::now() >= deadline);
        if due {
            self.save_cooling_pad_config();
        }
    }

    fn save_current_as_profile(&mut self, slot: app::profiles::ProfileSlot) {
        let Some(device) = self.device.as_ref() else {
            self.set_no_device_message();
            return;
        };

        match app::profiles::read_profile_from_device(device) {
            Ok(profile) => {
                let label = match slot {
                    app::profiles::ProfileSlot::Ac => {
                        self.ac_profile = profile;
                        "AC"
                    }
                    app::profiles::ProfileSlot::Battery => {
                        self.battery_profile = profile;
                        "Battery"
                    }
                };
                match self.persist_config() {
                    Ok(_) => self.set_status_message(format!("{label} settings saved")),
                    Err(e) => self.set_error_message(format!("Failed to save config: {e}")),
                }
            }
            Err(msg) => self.set_error_message(format!("Failed to read device state: {msg}")),
        }
    }

    fn save_current_as_ac_profile(&mut self) {
        self.save_current_as_profile(app::profiles::ProfileSlot::Ac);
    }

    fn save_current_as_battery_profile(&mut self) {
        self.save_current_as_profile(app::profiles::ProfileSlot::Battery);
    }

    fn set_performance_mode(&mut self, mode: &str) {
        let perf_mode = match Self::string_to_perf_mode(mode) {
            Some(m) => m,
            None => return,
        };

        let mut restore_manual = None::<u16>;
        let mut read_boosts = false;
        let mut set_mode_ok = false;
        let mut error_msg: Option<String> = None;

        if let Some(shared) = self.device.as_ref() {
            shared.with_mut(|device| {
                let (current_fan_mode, set_rpm) = Self::read_current_fan_state(device);

                match command::set_perf_mode(device, perf_mode) {
                    Ok(_) => {
                        set_mode_ok = true;
                        if matches!(current_fan_mode, FanMode::Manual) {
                            restore_manual = set_rpm;
                        }
                        if mode == "Custom" {
                            read_boosts = true;
                        }
                    }
                    Err(e) => {
                        error_msg = Some(format!("Failed to set performance mode: {}", e));
                    }
                }

                if set_mode_ok {
                    if let Some(rpm) = restore_manual {
                        std::thread::sleep(std::time::Duration::from_millis(50));
                        if command::set_fan_mode(device, FanMode::Manual).is_ok() {
                            std::thread::sleep(std::time::Duration::from_millis(50));
                            if command::set_fan_rpm(device, rpm, true).is_err() {
                                error_msg = Some(
                                    "Failed to restore fan RPM after performance mode change"
                                        .into(),
                                );
                            } else {
                                restore_manual = Some(rpm);
                            }
                        } else {
                            error_msg = Some(
                                "Failed to restore manual fan mode after performance mode change"
                                    .into(),
                            );
                        }
                    }
                    if read_boosts {
                        if let Ok(v) = command::get_cpu_boost(device) {
                            self.cpu_boost = v;
                        }
                        if let Ok(v) = command::get_gpu_boost(device) {
                            self.gpu_boost = v;
                        }
                    }
                }
            });
        } else {
            self.set_no_device_message();
            return;
        }

        if let Some(msg) = error_msg {
            self.set_error_message(msg);
        }
        if set_mode_ok {
            self.status.perf_mode = Some(perf_mode);
            if let Some(rpm) = restore_manual {
                self.status.fan_mode = Some(FanMode::Manual);
                self.status.fan_rpm = Some(rpm);
                self.manual_fan_rpm = rpm;
            } else {
                self.auto_fan_cap_override = false;
                if self.is_user_auto_mode() {
                    self.status.fan_mode = Some(FanMode::Auto);
                    self.status.fan_rpm = None;
                }
            }
            self.set_optional_status_message("Mode changed".into());
            self.update_stored_device_state();
        }
    }

    fn set_fan_mode(&mut self, mode: &str, rpm: Option<u16>) {
        if mode != "auto" && mode != "manual" {
            return;
        }

        let Some(shared) = self.device.as_ref() else {
            self.set_no_device_message();
            return;
        };

        let result = shared.with_mut(|device| match mode {
            "auto" => {
                self.auto_fan_cap_override = false;
                match command::set_fan_mode(device, FanMode::Auto) {
                    Ok(_) => {
                        self.status.fan_mode = Some(FanMode::Auto);
                        self.status.fan_rpm = None;
                        Ok(())
                    }
                    Err(e) => Err(e),
                }
            }
            "manual" => {
                self.auto_fan_cap_override = false;
                self.auto_fan_max_rpm_editing = false;
                match command::set_fan_mode(device, FanMode::Manual) {
                    Ok(_) => {
                        let rpm_val = rpm.unwrap_or(2000);
                        match command::set_fan_rpm(device, rpm_val, true) {
                            Ok(_) => {
                                self.status.fan_mode = Some(FanMode::Manual);
                                self.status.fan_rpm = Some(rpm_val);
                                Ok(())
                            }
                            Err(e) => Err(e),
                        }
                    }
                    Err(e) => Err(e),
                }
            }
            _ => unreachable!(),
        });

        match result {
            Some(Ok(())) => {
                self.sync_laptop_fan_cap();
                self.set_optional_status_message(format!("Fan set to {} mode", mode));
            }
            Some(Err(e)) => {
                self.set_status_message(format!("Failed to set fan: {}", e));
            }
            None => {
                self.set_status_message("Failed to set fan: device busy".into());
            }
        }
    }

    fn set_fan_rpm_only(&mut self, rpm: u16) {
        match execute_command(
            self.device.as_ref(),
            |device| command::set_fan_rpm(device, rpm, true),
            &format!("Fans RPM set to: {}", rpm),
            "Failed to set fan RPM",
        ) {
            Ok(message) => {
                self.status.fan_rpm = Some(rpm);
                self.manual_fan_rpm = rpm;
                self.auto_fan_cap_override = false;
                if let Ok(mut cap) = self.laptop_fan_cap.lock() {
                    cap.cap_active = false;
                }
                self.sync_laptop_fan_cap();
                self.set_optional_status_message(message);
            }
            Err(message) => {
                self.set_error_message(message);
            }
        }
    }

    fn enforce_manual_fan_rpm(&mut self) {
        if self.auto_fan_cap_override {
            return;
        }
        if self.status.fan_mode == Some(FanMode::Manual) {
            if let Some(device) = self.device.as_ref() {
                device.try_with_mut(|d| {
                    if let Some(current_set_rpm) =
                        get_fan_rpm_set(d, librazer::types::FanZone::Zone1)
                    {
                        if command::set_fan_rpm(d, current_set_rpm, true).is_ok() {
                            self.manual_fan_rpm = current_set_rpm;
                            self.status.fan_rpm = Some(current_set_rpm);
                            self.last_fan_enforce_time = std::time::Instant::now();
                        }
                    }
                });
            }
        }
    }

    fn enforce_cooling_pad_manual_fan(&mut self) {
        if self.cooling_pad_fan_mode != CoolingPadFanMode::Manual {
            return;
        }
        if let Some(pad) = self.cooling_pad.as_ref() {
            let rpm = self.cooling_pad_manual_rpm;
            match pad.with(|p| p.set_fan_rpm(rpm)) {
                Some(Ok(())) => {
                    self.last_cooling_pad_fan_enforce_time = std::time::Instant::now();
                    self.last_cooling_pad_manual_enforce_time = std::time::Instant::now();
                }
                Some(Err(_)) | None => {
                    self.redetect_cooling_pad();
                }
            }
        }
    }

    fn process_cooling_pad_enforce_flags(&mut self) {
        if self
            .cooling_pad_enforce
            .pending_cooling_pad_restore
            .swap(false, Ordering::Relaxed)
            && self.cooling_pad.is_some()
            && self.cooling_pad_fan_mode == CoolingPadFanMode::Manual
        {
            self.apply_cooling_pad_manual_fan(self.cooling_pad_manual_rpm);
        }

        if self
            .cooling_pad_enforce
            .needs_redetect
            .swap(false, Ordering::Relaxed)
        {
            self.redetect_cooling_pad();
        }
    }

    fn run_cooling_pad_fan_enforcement(&mut self) {
        if !self.fully_initialized || self.loading || self.cooling_pad.is_none() {
            return;
        }

        self.process_cooling_pad_enforce_flags();

        if self.cooling_pad_fan_mode == CoolingPadFanMode::Manual
            && self
                .last_cooling_pad_manual_enforce_time
                .elapsed()
                .as_secs_f32()
                >= 1.0
        {
            self.enforce_cooling_pad_manual_fan();
        }
    }

    fn apply_cooling_pad_manual_fan(&mut self, rpm: u16) {
        let Some(pad) = self.cooling_pad.as_ref() else {
            return;
        };
        match pad.with(|p| p.set_fan_rpm(rpm)) {
            Some(Ok(())) => {
                self.last_cooling_pad_fan_enforce_time = std::time::Instant::now();
                self.last_cooling_pad_manual_enforce_time = std::time::Instant::now();
            }
            Some(Err(_)) | None => {
                self.redetect_cooling_pad();
            }
        }
    }

    fn apply_cooling_pad_auto_output(&mut self, output: CoolingPadAutoOutput) {
        let Some(pad) = self.cooling_pad.as_ref() else {
            return;
        };
        let result = match output {
            CoolingPadAutoOutput::Off => pad.with(|p| p.fan_off()),
            CoolingPadAutoOutput::Rpm(rpm) => pad.with(|p| p.set_fan_rpm(rpm)),
        };
        match result {
            Some(Ok(())) => {
                self.last_cooling_pad_fan_enforce_time = std::time::Instant::now();
                self.sync_cooling_pad_enforce_auto_state();
            }
            Some(Err(_)) | None => {
                self.redetect_cooling_pad();
            }
        }
    }

    fn apply_cooling_pad_fan_to_device(&mut self) {
        match self.cooling_pad_fan_mode {
            CoolingPadFanMode::Off => {
                if let Some(pad) = self.cooling_pad.as_ref() {
                    let _ = pad.with(|p| p.fan_off());
                }
                self.reset_cooling_pad_auto_state();
            }
            CoolingPadFanMode::Manual => {
                self.reset_cooling_pad_auto_state();
                self.apply_cooling_pad_manual_fan(self.cooling_pad_manual_rpm);
            }
            CoolingPadFanMode::Auto => {
                let output = self.compute_cooling_pad_auto_output();
                self.apply_cooling_pad_auto_output(output);
            }
        }
        self.last_cooling_pad_fan_enforce_time = std::time::Instant::now();
        self.sync_cooling_pad_enforce();
    }

    fn run_fan_enforcement(&mut self) {
        self.run_cooling_pad_fan_enforcement();

        if self.fully_initialized && self.device.is_some() && !self.loading {
            self.sync_laptop_fan_cap();
            if self.last_fan_reconcile_time.elapsed().as_secs_f32() >= 3.0 {
                self.reconcile_auto_fan_cap_state();
                self.last_fan_reconcile_time = std::time::Instant::now();
            }
            if self.last_fan_enforce_time.elapsed().as_secs_f32() >= 1.0 {
                self.enforce_manual_fan_rpm();
            }
        }
    }

    fn restore_auto_fan_mode(&mut self) {
        self.auto_fan_cap_override = false;
        if let Some(device) = self.device.as_ref() {
            match device.with_mut(|d| command::set_fan_mode(d, FanMode::Auto)) {
                Some(Ok(())) => {
                    self.status.fan_mode = Some(FanMode::Auto);
                    self.status.fan_rpm = None;
                    self.set_optional_status_message("Auto fan mode restored".into());
                }
                Some(Err(e)) => {
                    self.set_error_message(format!("Failed to restore auto fan mode: {}", e));
                }
                None => {
                    self.set_error_message("Failed to restore auto fan mode: device busy".into());
                }
            }
        }
    }

    fn set_logo_mode(&mut self, mode: &str) {
        if let Err(e) = keyboard_lighting::ensure_owned(self.lighting_external) {
            self.set_error_message(e.to_string());
            return;
        }
        let logo_mode = match Self::string_to_logo_mode(mode) {
            Some(mode) => mode,
            None => return,
        };

        match execute_command(
            self.device.as_ref(),
            |device| command::set_logo_mode(device, logo_mode),
            &format!("Logo mode set to {}", mode),
            "Failed to set logo mode",
        ) {
            Ok(message) => {
                self.status.logo_mode = Some(logo_mode);
                self.set_optional_status_message(message);
            }
            Err(message) => {
                self.set_error_message(message);
            }
        }
    }

    fn set_brightness(&mut self, brightness: u8) {
        if let Err(e) = keyboard_lighting::ensure_owned(self.lighting_external) {
            self.set_error_message(e.to_string());
            return;
        }
        match execute_command(
            self.device.as_ref(),
            |device| command::set_keyboard_brightness(device, brightness),
            &format!(
                "Brightness set to step {}",
                lighting::raw_brightness_to_step_index(brightness)
            ),
            "Failed to set brightness",
        ) {
            Ok(message) => {
                self.status.keyboard_brightness = brightness;
                self.temp_brightness_step = lighting::raw_brightness_to_step_index(brightness);
                self.set_optional_status_message(message);
            }
            Err(message) => {
                self.set_error_message(message);
            }
        }
    }

    fn toggle_lights_always_on(&mut self) {
        if let Err(e) = keyboard_lighting::ensure_owned(self.lighting_external) {
            self.set_error_message(e.to_string());
            return;
        }
        let lights_always_on = if self.status.lights_always_on {
            LightsAlwaysOn::Enable
        } else {
            LightsAlwaysOn::Disable
        };

        let Some(device) = self.device.as_ref() else {
            self.set_no_device_message();
            return;
        };

        match device.with_mut(|d| command::set_lights_always_on(d, lights_always_on)) {
            Some(Ok(())) => {
                self.set_optional_status_message(format!(
                    "Keyboard Backlight Always On {}",
                    if self.status.lights_always_on {
                        "enabled"
                    } else {
                        "disabled"
                    }
                ));
                self.update_stored_device_state();
            }
            Some(Err(e)) => {
                self.set_status_message(format!("Failed to set lights always on: {}", e));
                self.status.lights_always_on = !self.status.lights_always_on;
            }
            None => {
                self.set_status_message("Failed to set lights always on: device busy".into());
                self.status.lights_always_on = !self.status.lights_always_on;
            }
        }
    }

    fn apply_native_pad_fan_action(&mut self, action: cooling_pad_control::CoolingPadFanAction) {
        use cooling_pad_control::CoolingPadFanAction;
        match action {
            CoolingPadFanAction::SetMode(mode) => {
                self.set_cooling_pad_fan_mode(mode);
            }
            CoolingPadFanAction::SetManualRpm(rpm) => {
                self.cooling_pad_manual_rpm = rpm;
                if matches!(action, CoolingPadFanAction::SetManualRpm(_))
                    && self.cooling_pad_fan_mode == CoolingPadFanMode::Manual
                {
                    self.apply_cooling_pad_manual_fan(rpm);
                    self.save_cooling_pad_config();
                    self.set_optional_status_message(format!("Cooling pad fan set to {rpm} RPM"));
                }
            }
            CoolingPadFanAction::SetAutoMinRpm(min) => {
                self.cooling_pad_auto_min_rpm = min.min(self.cooling_pad_auto_max_rpm);
                let max = self.cooling_pad_auto_max_rpm;
                clamp_auto_state_to_limits(&mut self.cooling_pad_auto_state, min, max);
                if let Ok(mut settings) = self.cooling_pad_enforce.settings.lock() {
                    clamp_auto_state_to_limits(&mut settings.auto_state, min, max);
                }
                self.sync_cooling_pad_enforce();
                if self.cooling_pad_fan_mode == CoolingPadFanMode::Auto {
                    self.apply_cooling_pad_fan_to_device();
                }
                self.schedule_cooling_pad_config_save();
            }
            CoolingPadFanAction::SetAutoMaxRpm(max) => {
                self.cooling_pad_auto_max_rpm = max.max(self.cooling_pad_auto_min_rpm);
                let min = self.cooling_pad_auto_min_rpm;
                clamp_auto_state_to_limits(&mut self.cooling_pad_auto_state, min, max);
                if let Ok(mut settings) = self.cooling_pad_enforce.settings.lock() {
                    clamp_auto_state_to_limits(&mut settings.auto_state, min, max);
                }
                self.sync_cooling_pad_enforce();
                if self.cooling_pad_fan_mode == CoolingPadFanMode::Auto {
                    self.apply_cooling_pad_fan_to_device();
                }
                self.schedule_cooling_pad_config_save();
            }
            CoolingPadFanAction::SetAutoOffBelowC(off) => {
                self.cooling_pad_auto_off_below_c = off;
                if self.cooling_pad_auto_full_above_c < off + 5.0 {
                    self.cooling_pad_auto_full_above_c = off + 5.0;
                }
                self.reset_cooling_pad_auto_state();
                if self.cooling_pad_fan_mode == CoolingPadFanMode::Auto {
                    self.apply_cooling_pad_fan_to_device();
                }
                self.schedule_cooling_pad_config_save();
            }
            CoolingPadFanAction::SetAutoFullAboveC(full) => {
                self.cooling_pad_auto_full_above_c = full;
                self.reset_cooling_pad_auto_state();
                if self.cooling_pad_fan_mode == CoolingPadFanMode::Auto {
                    self.apply_cooling_pad_fan_to_device();
                }
                self.schedule_cooling_pad_config_save();
            }
            CoolingPadFanAction::ToggleFollowLaptopFan(enabled) => {
                self.cooling_pad_follow_laptop_fan = enabled;
                if !enabled {
                    if let Ok(mut settings) = self.cooling_pad_enforce.settings.lock() {
                        clear_laptop_follow_smoothing(&mut settings.auto_state);
                    }
                    clear_laptop_follow_smoothing(&mut self.cooling_pad_auto_state);
                }
                self.sync_cooling_pad_enforce();
                if self.cooling_pad_fan_mode == CoolingPadFanMode::Auto {
                    self.apply_cooling_pad_fan_to_device();
                }
                self.save_cooling_pad_config();
            }
        }
    }

    fn set_cooling_pad_fan_mode(&mut self, mode: CoolingPadFanMode) {
        if self.cooling_pad.is_none() {
            return;
        }
        self.cooling_pad_fan_mode = mode;
        self.reset_cooling_pad_auto_state();
        self.apply_cooling_pad_fan_to_device();
        let label = match mode {
            CoolingPadFanMode::Off => "Cooling pad fan turned off",
            CoolingPadFanMode::Manual => "Cooling pad fan set to manual",
            CoolingPadFanMode::Auto => "Cooling pad fan set to auto",
        };
        self.set_optional_status_message(label.into());
        self.save_cooling_pad_config();
    }

    fn cooling_pad_rgb(&self) -> Rgb {
        Rgb {
            r: self.cooling_pad_color[0],
            g: self.cooling_pad_color[1],
            b: self.cooling_pad_color[2],
        }
    }

    fn apply_cooling_pad_config_to_device(&mut self) {
        if self.cooling_pad.is_none() {
            return;
        }

        self.apply_cooling_pad_fan_to_device();

        if !self.cooling_pad_chroma_available {
            self.set_optional_status_message("Cooling pad settings restored".into());
            return;
        }

        let Some(pad_mode) = PadLightingMode::from_str(&self.cooling_pad_lighting_mode) else {
            return;
        };
        let rgb = self.cooling_pad_rgb();
        let brightness = lighting::BRIGHTNESS_LEVELS[self.cooling_pad_brightness_step];

        let Some(pad) = self.cooling_pad.as_ref() else {
            return;
        };
        let lighting_result = pad
            .with(|p| cooling_pad_apply::apply_pad_config_lighting(p, pad_mode, rgb, brightness));

        match lighting_result {
            Some(Ok(())) => {
                self.mark_cooling_pad_lighting_changed();
                self.set_optional_status_message("Cooling pad settings restored".into());
            }
            Some(Err(e)) => {
                self.set_error_message(format!("Failed to restore cooling pad lighting: {e}"));
            }
            None => {}
        }
    }

    fn apply_cooling_pad_lighting(&mut self, clear_first: bool) {
        let Some(pad_mode) = PadLightingMode::from_str(&self.cooling_pad_lighting_mode) else {
            return;
        };
        let Some(pad) = self.cooling_pad.as_ref() else {
            return;
        };
        let rgb = self.cooling_pad_rgb();
        let brightness = lighting::BRIGHTNESS_LEVELS[self.cooling_pad_brightness_step];

        match pad.with(|p| {
            cooling_pad_apply::apply_pad_lighting(p, pad_mode, rgb, brightness, clear_first)
        }) {
            Some(Ok(())) => {
                self.mark_cooling_pad_lighting_changed();
                self.save_cooling_pad_config();
                self.set_optional_status_message(format!(
                    "Cooling pad lighting: {}",
                    self.cooling_pad_lighting_mode
                ));
            }
            Some(Err(e)) => {
                self.set_error_message(format!("Failed to set cooling pad lighting: {e}"));
            }
            None => {
                self.set_error_message("Failed to set cooling pad lighting: device busy".into());
            }
        }
    }

    fn set_cooling_pad_lighting_mode(&mut self, mode: &str) {
        if PadLightingMode::from_str(mode).is_none() {
            return;
        }
        self.cooling_pad_lighting_mode = mode.to_string();
        self.apply_cooling_pad_lighting(true);
    }

    fn apply_cooling_pad_lighting_color(&mut self) {
        if self.cooling_pad_lighting_mode == "Off" {
            return;
        }
        let Some(pad_mode) = PadLightingMode::from_str(&self.cooling_pad_lighting_mode) else {
            return;
        };
        let Some(pad) = self.cooling_pad.as_ref() else {
            return;
        };
        let rgb = self.cooling_pad_rgb();
        let brightness = lighting::BRIGHTNESS_LEVELS[self.cooling_pad_brightness_step];

        match pad.with(|p| cooling_pad_apply::apply_pad_color_change(p, pad_mode, rgb, brightness))
        {
            Some(Ok(())) => {
                self.mark_cooling_pad_lighting_changed();
                self.save_cooling_pad_config();
                self.set_optional_status_message("Cooling pad color applied".into());
            }
            Some(Err(e)) => {
                self.set_error_message(format!("Failed to set cooling pad color: {e}"));
            }
            None => {
                self.set_error_message("Failed to set cooling pad color: device busy".into());
            }
        }
    }

    fn set_cooling_pad_brightness(&mut self, brightness: u8) {
        let Some(pad) = self.cooling_pad.as_ref() else {
            return;
        };
        if self.cooling_pad_lighting_mode == "Off" {
            return;
        }

        match pad.with(|p| cooling_pad_apply::set_pad_brightness(p, brightness)) {
            Some(Ok(())) => {
                self.mark_cooling_pad_lighting_changed();
                self.schedule_cooling_pad_config_save();
                self.set_optional_status_message(format!(
                    "Cooling pad brightness set to {brightness}"
                ));
            }
            Some(Err(e)) => {
                self.set_error_message(format!("Failed to set cooling pad brightness: {e}"));
            }
            None => {
                self.set_error_message("Failed to set cooling pad brightness: device busy".into());
            }
        }
    }

    fn set_battery_care(&mut self, level: BatteryCare) {
        let previous = self.status.battery_care;
        self.status.battery_care = level;

        let Some(device) = self.device.as_ref() else {
            self.set_no_device_message();
            self.status.battery_care = previous;
            return;
        };

        match device.with_mut(|d| command::set_battery_care(d, level)) {
            Some(Ok(())) => {
                self.set_optional_status_message(format!("Battery care set to {}", level.label()));
                self.update_stored_device_state();
            }
            Some(Err(e)) => {
                self.set_status_message(format!("Failed to set battery care: {}", e));
                self.status.battery_care = previous;
            }
            None => {
                self.set_status_message("Failed to set battery care: device busy".into());
                self.status.battery_care = previous;
            }
        }
    }

    fn control_snapshot(&self) -> control_snapshot::Snapshot {
        let supported = self
            .device
            .as_ref()
            .and_then(|device| device.with(|d| d.info().perf_modes.clone()).flatten())
            .unwrap_or_default();
        control_snapshot::Snapshot {
            ready: self.fully_initialized && self.device.is_some(),
            supported,
            current: self.status.perf_mode,
            fan_auto: self.is_user_auto_mode(),
            rpm: self.status.fan_rpm,
            startup: self.run_at_startup,
        }
    }

    fn set_run_at_startup(&mut self, enabled: bool) {
        let previous = self.run_at_startup;
        if let Err(error) = startup::set_startup_enabled(enabled) {
            self.set_error_message(format!("Failed to update startup setting: {error}"));
            return;
        }
        self.run_at_startup = enabled;
        if let Err(error) = self.persist_config() {
            let rollback = startup::set_startup_enabled(previous);
            self.run_at_startup = startup::is_startup_enabled();
            self.set_error_message(format!(
                "Failed to save startup setting: {error}; rollback: {rollback:?}"
            ));
        }
    }

    fn update_polling_policy(&self) {
        let automatic = self.device_support.supported
            && ((self.auto_fan_limit_enabled && self.is_user_auto_mode())
                || (self.cooling_pad.is_some()
                    && self.cooling_pad_fan_mode == CoolingPadFanMode::Auto));
        self.polling.update(polling::Policy {
            visible: self.panel_visible,
            automatic,
        });
    }
    fn poll_background(&mut self) {
        self.update_polling_policy();
        self.maybe_sync_cooling_pad_enforce(3.0);
        if self.last_cooling_pad_pull_time.elapsed().as_secs_f32() >= 2.0 {
            self.pull_cooling_pad_enforce_state();
            self.last_cooling_pad_pull_time = std::time::Instant::now();
        }
        self.process_background_initialization();
        self.process_poll_snapshots();
        self.process_cooling_pad_poll_snapshots();
        self.process_thermal_snapshots();
        self.process_hid_enum_messages();
        self.run_fan_enforcement();
        self.flush_pending_cooling_pad_config_save();
    }
}

fn main() {
    // The WinUI parent owns the process and its redirected protocol pipes.
    if !std::env::args().any(|a| a == "--winui-backend") {
        return;
    }
    if app_single_instance::notify_if_running(SINGLE_INSTANCE_ID) {
        return;
    }
    winui_bridge::run();
}
