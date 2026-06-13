use std::collections::{HashSet, VecDeque};

use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::crypto::{CryptoError, XBondKey};

pub const MAGIC: [u8; 4] = *b"XBND";
pub const VERSION: u8 = 1;
pub const HEADER_LEN: usize = 40;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PacketKind {
    Data,
    Duplicate,
    Fec,
    Heartbeat,
    Control,
}

impl PacketKind {
    pub fn as_u8(self) -> u8 {
        match self {
            Self::Data => 1,
            Self::Duplicate => 2,
            Self::Fec => 3,
            Self::Heartbeat => 4,
            Self::Control => 5,
        }
    }
}

impl TryFrom<u8> for PacketKind {
    type Error = ProtocolError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            1 => Ok(Self::Data),
            2 => Ok(Self::Duplicate),
            3 => Ok(Self::Fec),
            4 => Ok(Self::Heartbeat),
            5 => Ok(Self::Control),
            _ => Err(ProtocolError::UnknownPacketKind(value)),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondHeader {
    pub kind: PacketKind,
    pub flags: u8,
    pub session_id: u64,
    pub sequence: u64,
    pub send_micros: u64,
    pub path_id: u16,
}

impl XBondHeader {
    pub fn new(
        kind: PacketKind,
        session_id: u64,
        sequence: u64,
        send_micros: u64,
        path_id: u16,
    ) -> Self {
        Self {
            kind,
            flags: 0,
            session_id,
            sequence,
            send_micros,
            path_id,
        }
    }

    pub fn is_expired(&self, now_micros: u64, deadline_micros: u64) -> bool {
        now_micros.saturating_sub(self.send_micros) > deadline_micros
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondFrame {
    pub header: XBondHeader,
    pub payload: Vec<u8>,
}

impl XBondFrame {
    pub fn new(header: XBondHeader, payload: impl Into<Vec<u8>>) -> Self {
        Self {
            header,
            payload: payload.into(),
        }
    }

    pub fn encode(&self) -> Result<Vec<u8>, ProtocolError> {
        let payload_len = u16::try_from(self.payload.len())
            .map_err(|_| ProtocolError::PayloadTooLarge(self.payload.len()))?;
        Ok(self.encode_with_payload(&self.payload, payload_len))
    }

    pub fn encode_sealed(&self, key: &XBondKey) -> Result<Vec<u8>, ProtocolError> {
        let sealed_payload = key.seal(&self.header, &self.payload)?;
        let payload_len = u16::try_from(sealed_payload.len())
            .map_err(|_| ProtocolError::PayloadTooLarge(sealed_payload.len()))?;
        Ok(self.encode_with_payload(&sealed_payload, payload_len))
    }

    fn encode_with_payload(&self, payload: &[u8], payload_len: u16) -> Vec<u8> {
        let mut out = Vec::with_capacity(HEADER_LEN + payload.len());
        out.extend_from_slice(&MAGIC);
        out.push(VERSION);
        out.push(self.header.kind.as_u8());
        out.push(self.header.flags);
        out.push(HEADER_LEN as u8);
        out.extend_from_slice(&self.header.session_id.to_be_bytes());
        out.extend_from_slice(&self.header.sequence.to_be_bytes());
        out.extend_from_slice(&self.header.send_micros.to_be_bytes());
        out.extend_from_slice(&self.header.path_id.to_be_bytes());
        out.extend_from_slice(&payload_len.to_be_bytes());
        out.extend_from_slice(&0u32.to_be_bytes());
        out.extend_from_slice(payload);
        out
    }

    pub fn decode(bytes: &[u8]) -> Result<Self, ProtocolError> {
        if bytes.len() < HEADER_LEN {
            return Err(ProtocolError::FrameTooShort(bytes.len()));
        }
        if bytes[0..4] != MAGIC {
            return Err(ProtocolError::BadMagic);
        }
        if bytes[4] != VERSION {
            return Err(ProtocolError::UnsupportedVersion(bytes[4]));
        }
        let header_len = bytes[7] as usize;
        if header_len != HEADER_LEN {
            return Err(ProtocolError::UnsupportedHeaderLength(header_len));
        }

        let kind = PacketKind::try_from(bytes[5])?;
        let flags = bytes[6];
        let session_id = read_u64(bytes, 8);
        let sequence = read_u64(bytes, 16);
        let send_micros = read_u64(bytes, 24);
        let path_id = read_u16(bytes, 32);
        let payload_len = read_u16(bytes, 34) as usize;
        let expected_len = HEADER_LEN + payload_len;
        if bytes.len() != expected_len {
            return Err(ProtocolError::LengthMismatch {
                expected: expected_len,
                actual: bytes.len(),
            });
        }

        Ok(Self {
            header: XBondHeader {
                kind,
                flags,
                session_id,
                sequence,
                send_micros,
                path_id,
            },
            payload: bytes[HEADER_LEN..].to_vec(),
        })
    }

    pub fn decode_sealed(bytes: &[u8], key: &XBondKey) -> Result<Self, ProtocolError> {
        let sealed_frame = Self::decode(bytes)?;
        let payload = key.open(&sealed_frame.header, &sealed_frame.payload)?;
        Ok(Self {
            header: sealed_frame.header,
            payload,
        })
    }
}

fn read_u16(bytes: &[u8], offset: usize) -> u16 {
    u16::from_be_bytes([bytes[offset], bytes[offset + 1]])
}

fn read_u64(bytes: &[u8], offset: usize) -> u64 {
    u64::from_be_bytes([
        bytes[offset],
        bytes[offset + 1],
        bytes[offset + 2],
        bytes[offset + 3],
        bytes[offset + 4],
        bytes[offset + 5],
        bytes[offset + 6],
        bytes[offset + 7],
    ])
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DuplicateOutcome {
    FirstArrival,
    Duplicate,
}

#[derive(Debug)]
pub struct DuplicateWindow {
    capacity: usize,
    order: VecDeque<(u64, u64)>,
    seen: HashSet<(u64, u64)>,
}

impl DuplicateWindow {
    pub fn new(capacity: usize) -> Self {
        Self {
            capacity: capacity.max(1),
            order: VecDeque::with_capacity(capacity),
            seen: HashSet::with_capacity(capacity),
        }
    }

    pub fn observe(&mut self, sequence: u64) -> DuplicateOutcome {
        self.observe_key(0, sequence)
    }

    pub fn observe_key(&mut self, session_id: u64, sequence: u64) -> DuplicateOutcome {
        let key = (session_id, sequence);
        if self.seen.contains(&key) {
            return DuplicateOutcome::Duplicate;
        }

        self.seen.insert(key);
        self.order.push_back(key);
        while self.order.len() > self.capacity {
            if let Some(old) = self.order.pop_front() {
                self.seen.remove(&old);
            }
        }
        DuplicateOutcome::FirstArrival
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReceiveOutcome {
    Accepted,
    Duplicate,
    Expired,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReceiveStats {
    pub accepted_packets: u64,
    pub duplicate_packets_dropped: u64,
    pub late_packets_dropped: u64,
}

#[derive(Debug)]
pub struct FrameReceiver {
    deadline_micros: u64,
    duplicate_window: DuplicateWindow,
    stats: ReceiveStats,
}

impl FrameReceiver {
    pub fn new(deadline_micros: u64, duplicate_window_capacity: usize) -> Self {
        Self {
            deadline_micros,
            duplicate_window: DuplicateWindow::new(duplicate_window_capacity),
            stats: ReceiveStats::default(),
        }
    }

    pub fn observe(&mut self, frame: &XBondFrame, now_micros: u64) -> ReceiveOutcome {
        if self
            .duplicate_window
            .observe_key(frame.header.session_id, frame.header.sequence)
            == DuplicateOutcome::Duplicate
        {
            self.stats.duplicate_packets_dropped += 1;
            return ReceiveOutcome::Duplicate;
        }

        if frame.header.is_expired(now_micros, self.deadline_micros) {
            self.stats.late_packets_dropped += 1;
            return ReceiveOutcome::Expired;
        }

        self.stats.accepted_packets += 1;
        ReceiveOutcome::Accepted
    }

    pub fn stats(&self) -> ReceiveStats {
        self.stats
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum ProtocolError {
    #[error("frame too short: {0} bytes")]
    FrameTooShort(usize),
    #[error("bad magic")]
    BadMagic,
    #[error("unsupported version: {0}")]
    UnsupportedVersion(u8),
    #[error("unsupported header length: {0}")]
    UnsupportedHeaderLength(usize),
    #[error("unknown packet kind: {0}")]
    UnknownPacketKind(u8),
    #[error("payload too large: {0} bytes")]
    PayloadTooLarge(usize),
    #[error("frame length mismatch: expected {expected}, actual {actual}")]
    LengthMismatch { expected: usize, actual: usize },
    #[error("crypto failure: {0}")]
    Crypto(#[from] CryptoError),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_round_trips() {
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Data, 42, 7, 123_456, 2),
            b"hello".to_vec(),
        );

        let encoded = frame.encode().unwrap();
        let decoded = XBondFrame::decode(&encoded).unwrap();

        assert_eq!(decoded, frame);
    }

    #[test]
    fn sealed_frame_round_trips_without_plaintext_on_wire() {
        let key = XBondKey::from_passphrase("test-key");
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Data, 42, 7, 123_456, 2),
            b"hello".to_vec(),
        );

        let encoded = frame.encode_sealed(&key).unwrap();
        assert!(!encoded.windows(5).any(|window| window == b"hello"));

        let decoded = XBondFrame::decode_sealed(&encoded, &key).unwrap();

        assert_eq!(decoded, frame);
    }

    #[test]
    fn sealed_frame_rejects_wrong_key() {
        let key = XBondKey::from_passphrase("test-key");
        let wrong_key = XBondKey::from_passphrase("wrong-key");
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Data, 42, 7, 123_456, 2),
            b"hello".to_vec(),
        );

        let encoded = frame.encode_sealed(&key).unwrap();

        assert!(matches!(
            XBondFrame::decode_sealed(&encoded, &wrong_key),
            Err(ProtocolError::Crypto(CryptoError::OpenFailed))
        ));
    }

    #[test]
    fn duplicate_window_accepts_first_and_rejects_late_copy() {
        let mut window = DuplicateWindow::new(4);

        assert_eq!(window.observe(100), DuplicateOutcome::FirstArrival);
        assert_eq!(window.observe(100), DuplicateOutcome::Duplicate);
        assert_eq!(window.observe(101), DuplicateOutcome::FirstArrival);
    }

    #[test]
    fn duplicate_window_forgets_old_sequences() {
        let mut window = DuplicateWindow::new(2);

        assert_eq!(window.observe(1), DuplicateOutcome::FirstArrival);
        assert_eq!(window.observe(2), DuplicateOutcome::FirstArrival);
        assert_eq!(window.observe(3), DuplicateOutcome::FirstArrival);
        assert_eq!(window.observe(1), DuplicateOutcome::FirstArrival);
    }

    #[test]
    fn duplicate_window_distinguishes_sessions() {
        let mut window = DuplicateWindow::new(4);

        assert_eq!(window.observe_key(1, 10), DuplicateOutcome::FirstArrival);
        assert_eq!(window.observe_key(2, 10), DuplicateOutcome::FirstArrival);
        assert_eq!(window.observe_key(1, 10), DuplicateOutcome::Duplicate);
    }

    #[test]
    fn header_deadline_does_not_block_newer_packets() {
        let header = XBondHeader::new(PacketKind::Data, 1, 1, 1_000, 1);

        assert!(!header.is_expired(1_050, 100));
        assert!(header.is_expired(1_200, 100));
    }

    #[test]
    fn frame_receiver_counts_accepted_duplicate_and_late_packets() {
        let mut receiver = FrameReceiver::new(100, 16);
        let on_time = XBondFrame::new(
            XBondHeader::new(PacketKind::Data, 1, 10, 1_000, 1),
            b"first".to_vec(),
        );
        let duplicate = XBondFrame::new(
            XBondHeader::new(PacketKind::Duplicate, 1, 10, 1_000, 2),
            b"second".to_vec(),
        );
        let late = XBondFrame::new(
            XBondHeader::new(PacketKind::Data, 1, 11, 1_000, 2),
            b"late".to_vec(),
        );

        assert_eq!(receiver.observe(&on_time, 1_050), ReceiveOutcome::Accepted);
        assert_eq!(
            receiver.observe(&duplicate, 1_060),
            ReceiveOutcome::Duplicate
        );
        assert_eq!(receiver.observe(&late, 1_200), ReceiveOutcome::Expired);

        assert_eq!(
            receiver.stats(),
            ReceiveStats {
                accepted_packets: 1,
                duplicate_packets_dropped: 1,
                late_packets_dropped: 1,
            }
        );
    }
}
