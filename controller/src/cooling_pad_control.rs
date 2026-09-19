#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoolingPadFanMode {
    Off,
    Manual,
    Auto,
}

impl CoolingPadFanMode {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Off => "off",
            Self::Manual => "manual",
            Self::Auto => "auto",
        }
    }

    pub fn from_config(s: &str) -> Self {
        match s {
            "manual" => Self::Manual,
            "auto" => Self::Auto,
            _ => Self::Off,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum CoolingPadFanAction {
    SetMode(CoolingPadFanMode),
    SetManualRpm(u16),
    SetAutoMinRpm(u16),
    SetAutoMaxRpm(u16),
    SetAutoOffBelowC(f32),
    SetAutoFullAboveC(f32),
    ToggleFollowLaptopFan(bool),
}
