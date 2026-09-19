//! Independently encoded Blade 14 (2022) matrix reports; see PROTOCOL.md.
use anyhow::{Result, bail};
use librazer::device::Device;
use serde::Deserialize;

#[derive(Debug, Deserialize)]
#[serde(tag = "effect", rename_all = "snake_case", deny_unknown_fields)]
pub enum Effect {
    Off {},
    Static { color: [u8; 3] },
    Wave { direction: Direction },
    Breathing { color: [u8; 3] },
    Spectrum {},
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Direction {
    Left,
    Right,
}

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
fn report(effect: &Effect) -> [u8; 90] {
    let args: Vec<u8> = match effect {
        Effect::Off {} => vec![0],
        Effect::Static { color } => vec![6, color[0], color[1], color[2]],
        Effect::Wave { direction } => vec![
            1,
            match direction {
                Direction::Left => 1,
                Direction::Right => 2,
            },
        ],
        Effect::Breathing { color } => vec![3, 1, color[0], color[1], color[2], 0, 0, 0],
        Effect::Spectrum {} => vec![4],
    };
    let mut bytes = [0; 90];
    bytes[1] = 0xff;
    bytes[5] = args.len() as u8;
    bytes[6] = 3;
    bytes[7] = 0x0a;
    bytes[8..8 + args.len()].copy_from_slice(&args);
    bytes[88] = bytes[2..88].iter().fold(0, |crc, b| crc ^ b);
    bytes
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
    fn reports_match_protocol_vectors() {
        for (effect, args, crc) in [
            (Effect::Off {}, vec![0], 0x08),
            (
                Effect::Static { color: [255, 0, 0] },
                vec![6, 255, 0, 0],
                0xf4,
            ),
            (
                Effect::Wave {
                    direction: Direction::Left,
                },
                vec![1, 1],
                0x0b,
            ),
            (
                Effect::Wave {
                    direction: Direction::Right,
                },
                vec![1, 2],
                0x08,
            ),
            (
                Effect::Breathing { color: [255, 0, 0] },
                vec![3, 1, 255, 0, 0, 0, 0, 0],
                0xfc,
            ),
            (Effect::Spectrum {}, vec![4], 0x0c),
        ] {
            let bytes = report(&effect);
            assert_eq!(&bytes[..8], &[0, 255, 0, 0, 0, args.len() as u8, 3, 10]);
            assert_eq!(&bytes[8..8 + args.len()], args.as_slice());
            assert!(bytes[8 + args.len()..88].iter().all(|b| *b == 0));
            assert_eq!(bytes[88], crc);
        }
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
