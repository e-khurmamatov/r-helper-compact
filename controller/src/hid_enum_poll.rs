use std::sync::mpsc::Sender;
use std::time::Duration;

use librazer::cooling_pad::is_present;

const COOLING_PAD_CHECK_INTERVAL: Duration = Duration::from_secs(2);

#[derive(Debug, Clone)]
pub enum HidEnumMessage {
    CoolingPadPresent(bool),
}

pub fn spawn_hid_enum_poller(tx: Sender<HidEnumMessage>) {
    std::thread::spawn(move || {
        let mut last_pad_present = None;

        loop {
            std::thread::sleep(COOLING_PAD_CHECK_INTERVAL);

            let pad_present = is_present();
            if last_pad_present != Some(pad_present) {
                last_pad_present = Some(pad_present);
                if tx
                    .send(HidEnumMessage::CoolingPadPresent(pad_present))
                    .is_err()
                {
                    break;
                }
            }
        }
    });
}
