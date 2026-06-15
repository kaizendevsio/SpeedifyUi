use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReorderedPacket {
    pub sequence: u64,
    pub path_id: u16,
    pub payload: Vec<u8>,
}

#[derive(Debug, Clone)]
struct PendingPacket {
    path_id: u16,
    payload: Vec<u8>,
    release_after_micros: u64,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReorderStats {
    pub pending_depth: u64,
    pub held_packets: u64,
    pub released_gap_packets: u64,
    pub late_duplicates: u64,
    pub timeout_releases: u64,
    pub capacity_releases: u64,
}

#[derive(Debug)]
pub struct PacketReorderBuffer {
    next_sequence: Option<u64>,
    pending: BTreeMap<u64, PendingPacket>,
    capacity: usize,
    hold_micros: u64,
    stats: ReorderStats,
}

impl PacketReorderBuffer {
    pub fn new(capacity: usize, hold_micros: u64) -> Self {
        Self {
            next_sequence: None,
            pending: BTreeMap::new(),
            capacity: capacity.max(1),
            hold_micros,
            stats: ReorderStats::default(),
        }
    }

    pub fn push(
        &mut self,
        sequence: u64,
        path_id: u16,
        payload: Vec<u8>,
        now_micros: u64,
        frame_deadline_micros: u64,
    ) -> Vec<ReorderedPacket> {
        if self
            .next_sequence
            .is_some_and(|next_sequence| sequence < next_sequence)
        {
            self.stats.late_duplicates = self.stats.late_duplicates.saturating_add(1);
            return Vec::new();
        }

        let hold_deadline = now_micros.saturating_add(self.hold_micros);
        let release_after_micros = if frame_deadline_micros == 0 {
            hold_deadline
        } else {
            frame_deadline_micros.min(hold_deadline)
        };

        if let std::collections::btree_map::Entry::Vacant(entry) = self.pending.entry(sequence) {
            entry.insert(PendingPacket {
                path_id,
                payload,
                release_after_micros,
            });
            self.stats.held_packets = self.stats.held_packets.saturating_add(1);
            self.stats.pending_depth = self.pending.len() as u64;
        } else {
            self.stats.late_duplicates = self.stats.late_duplicates.saturating_add(1);
        }

        self.drain_ready(now_micros)
    }

    pub fn drain_ready(&mut self, now_micros: u64) -> Vec<ReorderedPacket> {
        let mut ready = Vec::new();

        if self.next_sequence.is_none() {
            self.next_sequence = self.pending.keys().next().copied();
        }

        loop {
            let Some(next_sequence) = self.next_sequence else {
                break;
            };

            if let Some(packet) = self.pending.remove(&next_sequence) {
                self.stats.pending_depth = self.pending.len() as u64;
                ready.push(ReorderedPacket {
                    sequence: next_sequence,
                    path_id: packet.path_id,
                    payload: packet.payload,
                });
                self.next_sequence = Some(next_sequence.saturating_add(1));
                continue;
            }

            let Some((&oldest_sequence, oldest_packet)) = self.pending.iter().next() else {
                break;
            };

            let gap_expired = oldest_packet.release_after_micros <= now_micros;
            let capacity_exceeded = self.pending.len() > self.capacity;
            if gap_expired || capacity_exceeded {
                self.stats.released_gap_packets =
                    self.stats.released_gap_packets.saturating_add(1);
                if gap_expired {
                    self.stats.timeout_releases = self.stats.timeout_releases.saturating_add(1);
                }
                if capacity_exceeded {
                    self.stats.capacity_releases =
                        self.stats.capacity_releases.saturating_add(1);
                }
                self.next_sequence = Some(oldest_sequence);
                continue;
            }

            break;
        }

        ready
    }

    pub fn pending_len(&self) -> usize {
        self.pending.len()
    }

    pub fn stats(&self) -> ReorderStats {
        ReorderStats {
            pending_depth: self.pending.len() as u64,
            ..self.stats
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn drains_in_order_packets_immediately() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        let ready = buffer.push(10, 1, b"ten".to_vec(), 1_000, 100_000);
        assert_eq!(
            ready,
            vec![ReorderedPacket {
                sequence: 10,
                path_id: 1,
                payload: b"ten".to_vec()
            }]
        );

        let ready = buffer.push(11, 1, b"eleven".to_vec(), 2_000, 100_000);
        assert_eq!(
            ready,
            vec![ReorderedPacket {
                sequence: 11,
                path_id: 1,
                payload: b"eleven".to_vec()
            }]
        );
    }

    #[test]
    fn holds_out_of_order_packet_until_gap_expires() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 100_000).len(), 1);
        assert!(buffer
            .push(12, 2, b"twelve".to_vec(), 2_000, 100_000)
            .is_empty());
        assert_eq!(buffer.pending_len(), 1);

        assert!(buffer.drain_ready(20_000).is_empty());
        let ready = buffer.drain_ready(28_000);

        assert_eq!(
            ready,
            vec![ReorderedPacket {
                sequence: 12,
                path_id: 2,
                payload: b"twelve".to_vec()
            }]
        );
    }

    #[test]
    fn fills_gap_when_missing_packet_arrives_before_hold_time() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 100_000).len(), 1);
        assert!(buffer
            .push(12, 2, b"twelve".to_vec(), 2_000, 100_000)
            .is_empty());

        let ready = buffer.push(11, 1, b"eleven".to_vec(), 4_000, 100_000);

        assert_eq!(
            ready,
            vec![
                ReorderedPacket {
                    sequence: 11,
                    path_id: 1,
                    payload: b"eleven".to_vec()
                },
                ReorderedPacket {
                    sequence: 12,
                    path_id: 2,
                    payload: b"twelve".to_vec()
                }
            ]
        );
    }

    #[test]
    fn drops_already_delivered_old_packets() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 100_000).len(), 1);
        assert!(buffer
            .push(10, 2, b"duplicate".to_vec(), 2_000, 100_000)
            .is_empty());
        assert_eq!(buffer.stats().late_duplicates, 1);
    }

    #[test]
    fn exposes_pending_and_gap_release_stats() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 100_000).len(), 1);
        assert!(buffer
            .push(12, 2, b"twelve".to_vec(), 2_000, 100_000)
            .is_empty());

        assert_eq!(buffer.stats().pending_depth, 1);
        assert_eq!(buffer.stats().held_packets, 2);

        let ready = buffer.drain_ready(28_000);

        assert_eq!(ready.len(), 1);
        assert_eq!(buffer.stats().pending_depth, 0);
        assert_eq!(buffer.stats().released_gap_packets, 1);
        assert_eq!(buffer.stats().timeout_releases, 1);
    }
}
