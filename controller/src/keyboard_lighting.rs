//! Independently encoded Blade 14 (2022) matrix reports.
use anyhow::{Result, bail};
use librazer::device::Device;

pub use librazer::keyboard::Effect;
use librazer::keyboard::report;

pub fn supported(pid: u16) -> bool {
    librazer::device_registry::lookup(pid)
        .is_some_and(|r| r.keyboard_protocol == "standard_matrix_ff")
}
pub fn ensure_owned(external: bool) -> Result<()> {
    if external {
        bail!("OpenRGB owns lighting. Select R-Helper Compact for direct control.");
    }
    Ok(())
}
pub fn profile_lighting(external: bool, write: impl FnOnce() -> Result<()>) -> Result<()> {
    if external { Ok(()) } else { write() }
}
pub fn apply(device: &Device, external: bool, effect: &Effect) -> Result<()> {
    ensure_owned(external)?;
    if !supported(device.info().pid) {
        bail!("Standard effects are not supported on this model yet");
    }
    let bytes = report(effect);
    device.send(bytes.as_slice().try_into()?)?;
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ownership_blocks_commands_and_profile_writes() {
        assert!(ensure_owned(true).is_err());
        assert!(ensure_owned(false).is_ok());
        let mut calls = 0;
        profile_lighting(true, || {
            calls += 1;
            Ok(())
        })
        .unwrap();
        assert_eq!(calls, 0);
        profile_lighting(false, || {
            calls += 1;
            Ok(())
        })
        .unwrap();
        assert_eq!(calls, 1);
        assert!(profile_lighting(false, || bail!("write failed")).is_err());
    }
    #[test]
    fn rejects_invalid_effects_colors_and_directions() {
        for raw in [
            r#"{"effect":"unknown"}"#,
            r#"{"effect":"static","color":[256,0,0]}"#,
            r#"{"effect":"wave","direction":"up"}"#,
            r#"{"effect":"off","extra":1}"#,
        ] {
            assert!(serde_json::from_str::<Effect>(raw).is_err());
        }
        assert!(supported(0x028c));
        assert!(!supported(0x028d));
    }
}
