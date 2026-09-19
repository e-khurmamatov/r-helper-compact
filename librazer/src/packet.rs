use anyhow::{ensure, Result};
use rand::Rng;

pub(crate) const PACKET_SIZE: usize = 90;

/// Packet is the structure of the packet that is sent to the Razer HID device and received back.
/// Source https://github.com/Razer-Linux/razer-laptop-control-no-dkms/blob/main/razer_control_gui/src/device.rs.
#[repr(C)]
#[derive(Debug)]
pub struct Packet {
    status: u8,
    id: u8,
    remaining_packets: u16,
    protocol_type: u8,
    data_size: u8,
    command_class: u8,
    command_id: u8,
    args: [u8; 80],
    crc: u8,
    reserved: u8,
}

enum CommandStatus {
    New = 0x00,
    Successful = 0x02,
    NotSupported = 0x05,
}

impl Packet {
    pub fn new(command: u16, args: &[u8]) -> Packet {
        let mut args_buffer = [0x00; 80];
        args_buffer[..args.len()].copy_from_slice(args);

        let mut rng = rand::thread_rng();
        Packet {
            status: CommandStatus::New as u8,
            id: rng.gen_range(u8::MIN..=u8::MAX),
            remaining_packets: 0x0000,
            protocol_type: 0x00,
            data_size: args.len() as u8,
            command_class: (command >> 8) as u8,
            command_id: (command & 0xff) as u8,
            args: args_buffer,
            crc: 0x00,
            reserved: 0x00,
        }
    }

    pub fn get_args(&self) -> &[u8] {
        &self.args
    }

    pub fn ensure_matches_report(&self, report: &Packet) -> Result<()> {
        ensure!(
            (report.command_class, report.command_id, report.id)
                == (self.command_class, self.command_id, self.id),
            "Response does not match the report"
        );

        ensure!(
            self.remaining_packets == report.remaining_packets
            || (self.command_class, self.command_id) == (0x07, 0x92) /* 0x0792 (bho) has special handling */
            || (self.command_class, self.command_id) == (0x07, 0x8f), /* 0x078f max fan speed mode has special handling */
            "Response command does not match the report"
        );

        ensure!(self.status != CommandStatus::NotSupported as u8, "Command not supported");

        ensure!(
            self.status == CommandStatus::Successful as u8,
            "Command failed with unknown status: {:02X?}",
            self.status
        );

        Ok(())
    }
}

impl From<&Packet> for Vec<u8> {
    fn from(packet: &Packet) -> Vec<u8> {
        let mut data = vec![0; PACKET_SIZE];
        data[0] = packet.status;
        data[1] = packet.id;
        // Preserve the existing wire format, including little-endian packet count.
        data[2..4].copy_from_slice(&packet.remaining_packets.to_le_bytes());
        data[4] = packet.protocol_type;
        data[5] = packet.data_size;
        data[6] = packet.command_class;
        data[7] = packet.command_id;
        data[8..88].copy_from_slice(&packet.args);
        data[88] = packet.crc;
        data[89] = packet.reserved;
        data
    }
}

impl TryFrom<&[u8]> for Packet {
    type Error = anyhow::Error;

    fn try_from(data: &[u8]) -> Result<Self, Self::Error> {
        ensure!(data.len() == PACKET_SIZE, "Invalid raw data size");
        let mut args = [0; 80];
        args.copy_from_slice(&data[8..88]);
        Ok(Packet {
            status: data[0],
            id: data[1],
            remaining_packets: u16::from_le_bytes([data[2], data[3]]),
            protocol_type: data[4],
            data_size: data[5],
            command_class: data[6],
            command_id: data[7],
            args,
            crc: data[88],
            reserved: data[89],
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Captured from the previous bincode 1.3.3 codec: nonzero values expose
    // offsets, byte order, argument boundaries and trailing fields.
    const LEGACY_REPORT: [u8; 90] = [
        2, 165, 52, 18, 7, 80, 3, 2,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39,
        40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
        50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
        60, 61, 62, 63, 64, 65, 66, 67, 68, 69,
        70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
        204, 238,
    ];

    #[test]
    fn decodes_legacy_report_fields() {
        let p = Packet::try_from(LEGACY_REPORT.as_slice()).unwrap();
        assert_eq!((p.status, p.id, p.remaining_packets), (2, 165, 0x1234));
        assert_eq!(
            (p.protocol_type, p.data_size, p.command_class, p.command_id),
            (7, 80, 3, 2),
        );
        assert_eq!(p.args, std::array::from_fn(|i| i as u8));
        assert_eq!((p.crc, p.reserved), (204, 238));
    }

    #[test]
    fn encodes_legacy_report_bytes() {
        let p = Packet {
            status: 2,
            id: 165,
            remaining_packets: 0x1234,
            protocol_type: 7,
            data_size: 80,
            command_class: 3,
            command_id: 2,
            args: std::array::from_fn(|i| i as u8),
            crc: 204,
            reserved: 238,
        };
        assert_eq!(Vec::<u8>::from(&p), LEGACY_REPORT);
    }

    #[test]
    fn rejects_wrong_report_lengths() {
        for length in [0, 1, 80, 88, 89, 91, 92, 180] {
            assert!(Packet::try_from(vec![0; length].as_slice()).is_err());
        }
    }

    #[test]
    fn command_payload_is_fixed_width_and_zero_padded() {
        for length in 0..=80 {
            let args: Vec<u8> = (1..=length as u8).collect();
            let packet = Packet::new(0x0302, &args);
            let bytes = Vec::<u8>::from(&packet);
            assert_eq!(bytes.len(), PACKET_SIZE);
            assert_eq!(bytes[0], 0);
            assert_eq!(&bytes[2..8], &[0, 0, 0, length as u8, 3, 2]);
            assert_eq!(&bytes[8..8 + length], args);
            assert!(bytes[8 + length..].iter().all(|&b| b == 0));
        }
    }
}
