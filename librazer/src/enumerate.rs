use anyhow::{Context, Result};
use std::ffi::CString;

pub const RAZER_VID: u16 = 0x1532;

#[derive(Debug, Clone)]
pub struct RazerHidEntry {
    pub vid: u16,
    pub pid: u16,
    pub product_string: Option<String>,
    pub manufacturer_string: Option<String>,
    pub path: CString,
    pub interface_number: i32,
    pub usage_page: u16,
    pub usage: u16,
}

/// List every HID interface exposed by Razer USB devices (VID 0x1532).
pub fn list_razer_hid_devices() -> Result<Vec<RazerHidEntry>> {
    let api = hidapi::HidApi::new().context("Failed to create hid api")?;

    let mut entries: Vec<RazerHidEntry> = api
        .device_list()
        .filter(|info| info.vendor_id() == RAZER_VID)
        .map(|info| RazerHidEntry {
            vid: info.vendor_id(),
            pid: info.product_id(),
            product_string: info.product_string().map(str::to_string),
            manufacturer_string: info.manufacturer_string().map(str::to_string),
            path: info.path().to_owned(),
            interface_number: info.interface_number(),
            usage_page: info.usage_page(),
            usage: info.usage(),
        })
        .collect();

    entries.sort_by(|a, b| {
        a.pid
            .cmp(&b.pid)
            .then(a.interface_number.cmp(&b.interface_number))
            .then(a.path.to_bytes().cmp(b.path.to_bytes()))
    });

    Ok(entries)
}
