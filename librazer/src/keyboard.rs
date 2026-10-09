//! Matrix keyboard report encoding shared by normal and diagnostic controls.
use anyhow::Result;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(tag = "effect", rename_all = "snake_case", deny_unknown_fields)]
pub enum Effect {
    Off {},
    Static {
        color: [u8; 3],
    },
    Wave {
        direction: Direction,
    },
    Breathing {
        color: [u8; 3],
    },
    BreathingDual {
        color: [u8; 3],
        color2: [u8; 3],
    },
    Reactive {
        color: [u8; 3],
        speed: ReactiveSpeed,
    },
    Starlight {},
    Spectrum {},
}
#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Direction {
    Left,
    Right,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize)]
#[serde(try_from = "u8", into = "u8")]
pub struct ReactiveSpeed(u8);

impl TryFrom<u8> for ReactiveSpeed {
    type Error = &'static str;
    fn try_from(value: u8) -> Result<Self, Self::Error> {
        if (1..=4).contains(&value) {
            Ok(Self(value))
        } else {
            Err("Reactive duration must be between 1 and 4")
        }
    }
}

pub fn report(effect: &Effect) -> [u8; 90] {
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
        Effect::BreathingDual { color, color2 } => vec![
            3, 2, color[0], color[1], color[2], color2[0], color2[1], color2[2],
        ],
        Effect::Reactive { color, speed } => vec![2, speed.0, color[0], color[1], color[2]],
        Effect::Starlight {} => vec![0x19, 1, 1, 0, 255, 0, 0, 0, 0],
        Effect::Spectrum {} => vec![4],
    };
    let mut bytes = [0; 90];
    bytes[1] = 0xff;
    bytes[5] = args.len() as u8;
    // This model's fixed Starlight command advertises one byte while carrying
    // the fixed effect parameters in the padded argument area.
    if matches!(effect, Effect::Starlight {}) {
        bytes[5] = 1;
    }
    bytes[6] = 3;
    bytes[7] = 0x0a;
    bytes[8..8 + args.len()].copy_from_slice(&args);
    bytes[88] = bytes[2..88].iter().fold(0, |crc, b| crc ^ b);
    bytes
}

impl From<ReactiveSpeed> for u8 {
    fn from(value: ReactiveSpeed) -> Self {
        value.0
    }
}

#[cfg(test)]
mod tests {
    use super::*;
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
    fn new_effects_decode_and_match_wire_vectors() {
        for (json, size, args, crc) in [
            (
                r#"{"effect":"reactive","color":[18,52,86],"speed":4}"#,
                5,
                vec![2, 4, 18, 52, 86],
                0x7a,
            ),
            (
                r#"{"effect":"breathing_dual","color":[18,52,86],"color2":[171,205,239]}"#,
                8,
                vec![3, 2, 18, 52, 86, 171, 205, 239],
                0xf9,
            ),
            (
                r#"{"effect":"starlight"}"#,
                1,
                vec![25, 1, 1, 0, 255, 0, 0, 0, 0],
                0xee,
            ),
        ] {
            let effect: Effect = serde_json::from_str(json).unwrap();
            let mut expected = [0; 90];
            expected[1] = 255;
            expected[5] = size;
            expected[6] = 3;
            expected[7] = 10;
            expected[8..8 + args.len()].copy_from_slice(&args);
            expected[88] = crc;
            assert_eq!(report(&effect), expected);
        }
        for speed in 1..=4 {
            let effect: Effect = serde_json::from_value(
                serde_json::json!({"effect":"reactive","color":[0,255,0],"speed":speed}),
            )
            .unwrap();
            assert_eq!(report(&effect)[9], speed);
        }
        for invalid in [
            r#"{"effect":"reactive","color":[0,0,0],"speed":0}"#,
            r#"{"effect":"reactive","color":[0,0,0],"speed":5}"#,
            r#"{"effect":"reactive","color":[0,0,0]}"#,
            r#"{"effect":"breathing_dual","color":[0,0,0]}"#,
            r#"{"effect":"breathing_dual","color":[0,0,0],"color2":[256,0,0]}"#,
            r#"{"effect":"starlight","color":[0,0,0]}"#,
        ] {
            assert!(
                serde_json::from_str::<Effect>(invalid).is_err(),
                "{invalid}"
            );
        }
    }
}
