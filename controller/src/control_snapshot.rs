//! Hardware control availability exposed to the WinUI protocol.
use librazer::types::PerfMode;

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Snapshot {
    pub ready: bool,
    pub supported: Vec<PerfMode>,
    pub current: Option<PerfMode>,
    pub fan_auto: bool,
    pub rpm: Option<u16>,
    pub startup: bool,
}
