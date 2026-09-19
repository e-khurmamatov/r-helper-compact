// The original 0.8.5 levels are intentionally unchanged.
pub const BRIGHTNESS_LEVELS: &[u8] = &[
    0, 13, 28, 43, 59, 74, 89, 105, 120, 133, 148, 163, 179, 194, 209, 225,
];
pub fn raw_brightness_to_step_index(brightness: u8) -> usize {
    BRIGHTNESS_LEVELS
        .iter()
        .enumerate()
        .min_by_key(|&(_, level)| (*level as i16 - brightness as i16).abs())
        .map(|(index, _)| index)
        .unwrap_or(0)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn all_levels_round_trip() {
        for (index, &raw) in BRIGHTNESS_LEVELS.iter().enumerate() {
            assert_eq!(raw_brightness_to_step_index(raw), index);
        }
    }
    #[test]
    fn raw_outside_slider_range_is_clamped() {
        assert_eq!(raw_brightness_to_step_index(255), 15);
    }
}
