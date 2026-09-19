//! Shared read cadence. Hardware enforcement has its own unchanged deadlines.
use std::sync::{Condvar, Mutex};
use std::time::Duration;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Policy {
    pub visible: bool,
    pub automatic: bool,
}
#[derive(Clone, Copy)]
pub enum ReadKind {
    Device,
    Thermal,
    PadLighting,
}
impl Policy {
    pub fn interval(self, kind: ReadKind) -> Duration {
        Duration::from_secs(match kind {
            ReadKind::Device => {
                if self.visible || self.automatic {
                    1
                } else {
                    3
                }
            }
            ReadKind::Thermal => {
                if self.visible || self.automatic {
                    2
                } else {
                    10
                }
            }
            ReadKind::PadLighting => {
                if self.visible {
                    1
                } else {
                    30
                }
            }
        })
    }
}
#[derive(Default)]
pub struct Schedule {
    state: Mutex<(Policy, u64)>,
    changed: Condvar,
}
impl Schedule {
    pub fn update(&self, policy: Policy) {
        let mut state = self.state.lock().unwrap();
        if state.0 != policy {
            state.0 = policy;
            state.1 = state.1.wrapping_add(1);
            self.changed.notify_all();
        }
    }
    pub fn refresh(&self) {
        let mut state = self.state.lock().unwrap();
        state.1 = state.1.wrapping_add(1);
        self.changed.notify_all();
    }
    pub fn wait(&self, kind: ReadKind, generation: &mut u64) -> Policy {
        let state = self.state.lock().unwrap();
        let timeout = state.0.interval(kind);
        let (state, _) = self
            .changed
            .wait_timeout_while(state, timeout, |s| s.1 == *generation)
            .unwrap();
        *generation = state.1;
        state.0
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn hidden_automation_keeps_fast_measurements() {
        for visible in [false, true] {
            let p = Policy {
                visible,
                automatic: true,
            };
            assert_eq!(p.interval(ReadKind::Thermal), Duration::from_secs(2));
            assert_eq!(p.interval(ReadKind::Device), Duration::from_secs(1));
        }
        assert_eq!(
            Policy::default().interval(ReadKind::Thermal),
            Duration::from_secs(10)
        );
    }
    #[test]
    fn opening_wakes_a_pending_slow_schedule() {
        let schedule = Schedule::default();
        let mut generation = 0;
        schedule.update(Policy {
            visible: true,
            automatic: false,
        });
        assert!(schedule.wait(ReadKind::Thermal, &mut generation).visible);
        assert_eq!(generation, 1);
        schedule.refresh();
        schedule.wait(ReadKind::Device, &mut generation);
        assert_eq!(generation, 2);
    }
}
