use crate::capabilities::{probe_features, resolve_descriptor, run_init_cmds};
use crate::descriptor::Descriptor;
use crate::packet::{Packet, PACKET_SIZE};
use crate::device_registry::{self, SupportReport};

use anyhow::{anyhow, Context, Result};
use std::{thread, time::Duration};

pub struct Device {
    device: hidapi::HidDevice,
    pub info: Descriptor,
}

fn read_bios_value(name: &str) -> Result<String> {
    #[cfg(target_os = "windows")]
    {
        let hklm = winreg::RegKey::predef(winreg::enums::HKEY_LOCAL_MACHINE);
        let bios = hklm.open_subkey("HARDWARE\\DESCRIPTION\\System\\BIOS")?;
        bios.get_value(name).context(format!("Failed to read BIOS value {}", name))
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = name;
        anyhow::bail!("Automatic model detection is not implemented for this platform")
    }
}

// Read the model id and clip to conform with https://mysupport.razer.com/app/answers/detail/a_id/5481
fn read_device_model() -> Result<String> {
    Ok(read_bios_value("SystemSKU")?.chars().take(10).collect())
}

impl Device {
    pub const RAZER_VID: u16 = crate::enumerate::RAZER_VID;

    pub fn info(&self) -> &Descriptor {
        &self.info
    }

    fn open_hid(pid: u16) -> Result<hidapi::HidDevice> {
        let api = hidapi::HidApi::new().context("Failed to create hid api")?;

        for info in api
            .device_list()
            .filter(|info| (info.vendor_id(), info.product_id()) == (Device::RAZER_VID, pid))
        {
            let device = api.open_path(info.path())?;
            if device.send_feature_report(&[0, 0]).is_ok() {
                return Ok(device);
            }
        }
        anyhow::bail!("Failed to open Razer device with PID {:04x}", pid)
    }

    fn open_by_pid(pid: u16) -> Result<Device> {
        let hid = Self::open_hid(pid)?;
        Ok(Device {
            device: hid,
            info: Descriptor {
                model_sku: String::new(),
                display_name: String::new(),
                pid,
                features: Vec::new(),
                perf_modes: None,
                cpu_boosts: None,
                gpu_boosts: None,
                disallowed_boost_pairs: Vec::new(),
            },
        })
    }

    pub fn send(&self, report: Packet) -> Result<Packet> {
        let mut response_buf: Vec<u8> = vec![0x00; 1 + PACKET_SIZE];

        const MAX_RETRIES: usize = 5;

        for attempt in 0..MAX_RETRIES {
            thread::sleep(Duration::from_micros(1000));

            self.device
                .send_feature_report(
                    [0_u8; 1]
                        .iter()
                        .copied()
                        .chain(Into::<Vec<u8>>::into(&report).into_iter())
                        .collect::<Vec<_>>()
                        .as_slice(),
                )
                .context("Failed to send feature report")?;

            thread::sleep(Duration::from_micros(2000));

            let response_size = self.device.get_feature_report(&mut response_buf)?;
            if response_buf.len() != response_size {
                return Err(anyhow!("Response size != {}", response_buf.len()));
            }

            let response = <&[u8] as TryInto<Packet>>::try_into(&response_buf[1..])?;

            if response.ensure_matches_report(&report).is_ok() {
                return Ok(response);
            } else if attempt == MAX_RETRIES - 1 {
                return Err(anyhow!("Failed to match report after {} attempts", MAX_RETRIES));
            }

            thread::sleep(Duration::from_millis(500));
        }

        Err(anyhow!("Failed to send feature report"))
    }

    pub fn enumerate() -> Result<(Vec<u16>, String)> {
        let razer_pid_list: Vec<_> = hidapi::HidApi::new()?
            .device_list()
            .filter(|info| info.vendor_id() == Device::RAZER_VID)
            .map(|info| info.product_id())
            .collect::<std::collections::HashSet<_>>()
            .into_iter()
            .collect();

        if razer_pid_list.is_empty() {
            anyhow::bail!("No Razer devices found")
        }

        match read_device_model() {
            Ok(model) if model.starts_with("RZ09-") => Ok((razer_pid_list, model)),
            Ok(model) => anyhow::bail!("Detected model but it's not a Razer laptop: {}", model),
            Err(e) => anyhow::bail!("Failed to detect model: {}", e),
        }
    }

    /// Enumerates metadata and BIOS identity only; never opens a control handle.
    fn control_identity() -> Result<(device_registry::ControlTarget, String)> {
        let sku=read_device_model().unwrap_or_default();
        let maker=read_bios_value("SystemManufacturer").unwrap_or_default();
        let entries=crate::enumerate::list_razer_hid_devices()?;
        let pids:Vec<_>=entries.iter().map(|i|i.pid).collect();
        let blade_pids:Vec<_>=entries.iter().filter(|i|device_registry::blade_product(i.product_string.as_deref().unwrap_or(""))).map(|i|i.pid).collect();
        let target=device_registry::resolve_target(device_registry::records()?,&pids,&blade_pids,&sku,&maker)?;
        Ok((target,sku))
    }

    /// Report identifiers only. Never include serials, device paths or raw errors.
    pub fn support_report() -> SupportReport {
        let sku=read_device_model().unwrap_or_default();
        let maker=read_bios_value("SystemManufacturer").unwrap_or_default();
        let identity=device_registry::host_identity(&maker,&sku).to_string();
        let entries=crate::enumerate::list_razer_hid_devices();
        let mut hid=Vec::new();
        let selection=match &entries {
            Ok(entries)=>{
                let pids:Vec<_>=entries.iter().map(|i|i.pid).collect();
                let blade_pids:Vec<_>=entries.iter().filter(|i|device_registry::blade_product(i.product_string.as_deref().unwrap_or(""))).map(|i|i.pid).collect();
                hid=entries.iter().map(|i|format!("1532:{:04X} interface={} usage={:04X}:{:04X}",i.pid,i.interface_number,i.usage_page,i.usage)).collect();
                device_registry::records().and_then(|r|device_registry::resolve_target(r,&pids,&blade_pids,&sku,&maker))
            },
            Err(_)=>Err(anyhow!("Could not enumerate HID devices. Controls are disabled.")),
        };
        let (supported,experimental,model,reason)=match selection {
            Ok(target)=>(true,target.experimental,target.name,if target.experimental {
                "Experimental support: this laptop has not been verified with Compact. Some controls may not work."
            } else { "User-confirmed registry profile." }.to_string()),
            Err(error)=>(false,false,String::new(),error.to_string()),
        };
        let machine=crate::diagnostics::read(&sku,identity=="razer");
        SupportReport { machine,supported,experimental,identity,model,sku,hid,reason }
    }

    pub fn detect() -> Result<Device> {
        // Re-evaluate host and controller identity immediately before opening HID.
        let (target,model_sku)=Self::control_identity()?;
        let mut device=Self::open_by_pid(target.pid)?;
        let probed=probe_features(&device);
        device.info=resolve_descriptor(model_sku,target.name,target.pid,target.generation,probed);
        let init_cmds=target.generation.default_init_cmds();
        if !init_cmds.is_empty() { run_init_cmds(&device,init_cmds)?; }
        Ok(device)
    }
}
