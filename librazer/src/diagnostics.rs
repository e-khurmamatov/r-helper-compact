//! Read-only, allowlisted Windows firmware metadata. Never query serial numbers.
use serde::Serialize;
use crate::device_registry::Laptop;

#[derive(Debug, Default, Clone, Serialize)]
pub struct MachineDetails {
    pub model: Option<String>,
    pub model_source: &'static str,
    pub bios_version: Option<String>,
    pub ec_version: Option<String>,
}

// Firmware strings outside a small version grammar are deliberately omitted.
pub fn version(value: &str) -> Option<String> {
    let value=value.trim();
    let digits=value.strip_prefix('v').or_else(||value.strip_prefix('V')).unwrap_or(value);
    let parts:Vec<_>=digits.split('.').collect();
    (matches!(parts.len(),2..=4) && parts.iter().all(|p| !p.is_empty() && p.len()<=3 && p.bytes().all(|b|b.is_ascii_digit())))
        .then(||value.to_string())
}

pub fn ec_version(major: Option<u32>, minor: Option<u32>) -> Option<String> {
    match (major,minor) {
        (Some(a),Some(b)) if a<255 && b<255=>Some(format!("{a}.{b}")),
        _=>None,
    }
}

pub fn model(rows: &[Laptop], sku: &str, product: &str) -> (Option<String>, &'static str) {
    let mut names:Vec<_>=rows.iter().filter(|r|r.sku_prefixes.iter().any(|s|sku.starts_with(s))).map(|r|r.name.as_str()).collect();
    names.sort_unstable();names.dedup();
    if names.len()==1 { return (Some(names[0].into()),"sku_catalogue"); }
    // Preserve only a recognized family, not arbitrary OEM text or a guessed year.
    let product=product.trim().strip_prefix("Razer ").unwrap_or(product.trim());
    for family in ["Blade Pro 17","Blade Stealth 13","Blade 14","Blade 15","Blade 16","Blade 17","Blade 18"] {
        if product==family || product.strip_prefix(family).is_some_and(|tail|tail.starts_with(' ') || tail.starts_with('-')) {
            return (Some(format!("Razer {family}")),"windows_family");
        }
    }
    (None,"unavailable")
}

pub fn read(sku: &str, razer_host: bool) -> MachineDetails {
    let mut details=MachineDetails { model_source:"unavailable",..Default::default() };
    let mut product=String::new();
    #[cfg(target_os="windows")]
    if let Ok(key)=winreg::RegKey::predef(winreg::enums::HKEY_LOCAL_MACHINE).open_subkey("HARDWARE\\DESCRIPTION\\System\\BIOS") {
        product=key.get_value::<String,_>("SystemProductName").unwrap_or_default();
        details.bios_version=key.get_value::<String,_>("BIOSVersion").ok().and_then(|v|version(&v))
            .or_else(||key.get_value::<Vec<String>,_>("BIOSVersion").ok().and_then(|v|v.iter().find_map(|s|version(s))));
        details.ec_version=ec_version(key.get_value("ECFirmwareMajorRelease").ok(),key.get_value("ECFirmwareMinorRelease").ok());
    }
    if razer_host {
        if let Ok(rows)=crate::device_registry::records() {
            (details.model,details.model_source)=model(rows,sku,&product);
        }
    }
    details
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn firmware_metadata_is_bounded_and_excludes_arbitrary_text() {
        assert_eq!(version(" v1.09 ").as_deref(),Some("v1.09"));
        for bad in ["", "Default string", "1.09\nSECRET", "C:\\Users\\name", "SN12345678", "12345678.1", "1..2"] { assert!(version(bad).is_none()); }
        assert_eq!(ec_version(Some(1),Some(0)).as_deref(),Some("1.0"));
        for pair in [(Some(255),Some(255)),(Some(1),None),(None,Some(2)),(Some(65535),Some(1))] { assert!(ec_version(pair.0,pair.1).is_none()); }
    }
    #[test] fn model_can_be_identified_without_hid_but_does_not_guess_a_year() {
        let rows=crate::device_registry::records().unwrap();
        let (name,source)=model(rows,"RZ09-0510AB","Default string");
        assert_eq!(name.as_deref(),Some("Razer Blade 16 (2024)"));assert_eq!(source,"sku_catalogue");
        let (name,source)=model(rows,"RZ09-9999","Blade 16 - SERIAL_SECRET");
        assert_eq!(name.as_deref(),Some("Razer Blade 16"));assert_eq!(source,"windows_family");
        assert!(model(rows,"RZ09-9999","PRIVATE_SECRET").0.is_none());
        assert!(model(rows,"RZ09-9999","Blade 160").0.is_none());
    }
}
