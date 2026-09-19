use crate::types::{CpuBoost, GpuBoost, PerfMode};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BladeGeneration {
    /// Battery, Silent, Balanced, Custom — no Performance/Hyperboost
    Legacy4,
    /// Adds Performance, Hyperboost + CPU/GPU boost sliders
    Modern6,
    /// Expose full PerfMode enum (discovery / unknown hardware)
    Discovery,
}

const MODERN6_INIT_CMDS: &[u16] = &[0x0081, 0x0086, 0x0f90, 0x0086, 0x0f10, 0x0087];

const LEGACY4_PERF_MODES: &[PerfMode] = &[
    PerfMode::Battery,
    PerfMode::Silent,
    PerfMode::Balanced,
    PerfMode::Custom,
];

const MODERN6_PERF_MODES: &[PerfMode] = &[
    PerfMode::Battery,
    PerfMode::Silent,
    PerfMode::Balanced,
    PerfMode::Performance,
    PerfMode::Hyperboost,
    PerfMode::Custom,
];

const MODERN6_CPU_BOOSTS: &[CpuBoost] = &[CpuBoost::Low, CpuBoost::Medium, CpuBoost::High];
const MODERN6_GPU_BOOSTS: &[GpuBoost] = &[GpuBoost::Low, GpuBoost::Medium, GpuBoost::High];
const MODERN6_DISALLOWED_PAIRS: &[(CpuBoost, GpuBoost)] = &[(CpuBoost::High, GpuBoost::High)];

impl BladeGeneration {
    pub fn default_perf_modes(self) -> Option<&'static [PerfMode]> {
        match self {
            BladeGeneration::Legacy4 => Some(LEGACY4_PERF_MODES),
            BladeGeneration::Modern6 => Some(MODERN6_PERF_MODES),
            BladeGeneration::Discovery => None,
        }
    }

    pub fn cpu_boosts(self) -> Option<&'static [CpuBoost]> {
        match self {
            BladeGeneration::Modern6 => Some(MODERN6_CPU_BOOSTS),
            BladeGeneration::Legacy4 | BladeGeneration::Discovery => None,
        }
    }

    pub fn gpu_boosts(self) -> Option<&'static [GpuBoost]> {
        match self {
            BladeGeneration::Modern6 => Some(MODERN6_GPU_BOOSTS),
            BladeGeneration::Legacy4 | BladeGeneration::Discovery => None,
        }
    }

    pub fn disallowed_pairs(self) -> &'static [(CpuBoost, GpuBoost)] {
        match self {
            BladeGeneration::Modern6 => MODERN6_DISALLOWED_PAIRS,
            BladeGeneration::Legacy4 | BladeGeneration::Discovery => &[],
        }
    }

    pub fn default_init_cmds(self) -> &'static [u16] {
        match self {
            BladeGeneration::Modern6 => MODERN6_INIT_CMDS,
            BladeGeneration::Legacy4 | BladeGeneration::Discovery => &[],
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct PidProfile {
    pub pid: u16,
    pub generation: BladeGeneration,
    /// Marketing name including year when known (e.g. "Razer Blade 16 (2025)").
    pub marketing_name: &'static str,
}

/// Profiles derive from the reviewed, compiled JSON registry.
pub static KNOWN_PROFILES: std::sync::LazyLock<Vec<PidProfile>> = std::sync::LazyLock::new(|| {
    crate::device_registry::records().unwrap_or(&[]).iter().filter(|r|r.enabled).map(|r|PidProfile {
        pid:r.product_id(),generation:r.generation(),marketing_name:r.name.as_str()
    }).collect()
});

pub const GENERIC_FALLBACK: PidProfile = PidProfile {
    pid: 0,
    generation: BladeGeneration::Discovery,
    marketing_name: "Razer Blade",
};

pub fn lookup_marketing_name(pid: u16) -> Option<&'static str> {
    lookup_profile(pid).map(|p| p.marketing_name)
}

pub fn lookup_profile(pid: u16) -> Option<&'static PidProfile> {
    KNOWN_PROFILES.iter().find(|p| p.pid == pid)
}

pub fn lookup_profile_or_fallback(pid: u16) -> &'static PidProfile {
    lookup_profile(pid).unwrap_or(&GENERIC_FALLBACK)
}

/// Unknown or mismatched identity never implies a command family.
pub fn resolve_generation(pid: u16, model_sku: &str) -> BladeGeneration {
    crate::device_registry::records().ok()
        .and_then(|rows|crate::device_registry::select(rows,&[pid],model_sku).ok())
        .map(|r|r.generation()).unwrap_or(BladeGeneration::Discovery)
}
#[cfg(test)] mod tests {
    use super::*;
    #[test] fn exact_identity_resolves_generation() {
        assert_eq!(resolve_generation(0x028c,"RZ09-0427NE"),BladeGeneration::Legacy4);
        assert_eq!(resolve_generation(0x02c6,"RZ09-0528AA"),BladeGeneration::Modern6);
    }
    #[test] fn unknown_pid_and_wrong_sku_never_inherit_modern_commands() {
        assert_eq!(resolve_generation(0xffff,"RZ09-0528"),BladeGeneration::Discovery);
        assert_eq!(resolve_generation(0x028c,"RZ09-0528"),BladeGeneration::Discovery);
        assert!(lookup_profile(0xffff).is_none());
        assert!(lookup_profile(0x029c).is_none());
    }
}
