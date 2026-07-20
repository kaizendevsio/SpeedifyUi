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
    received_at_micros: u64,
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
    initial_sequence: Option<u64>,
    next_sequence: Option<u64>,
    pending: BTreeMap<u64, PendingPacket>,
    repair_requests: BTreeMap<u64, u64>,
    capacity: usize,
    hold_micros: u64,
    stats: ReorderStats,
}

impl PacketReorderBuffer {
    pub fn new(capacity: usize, hold_micros: u64) -> Self {
        Self {
            initial_sequence: None,
            next_sequence: None,
            pending: BTreeMap::new(),
            repair_requests: BTreeMap::new(),
            capacity: capacity.max(1),
            hold_micros,
            stats: ReorderStats::default(),
        }
    }

    pub fn with_initial_sequence(capacity: usize, hold_micros: u64, initial_sequence: u64) -> Self {
        Self {
            initial_sequence: Some(initial_sequence),
            next_sequence: Some(initial_sequence),
            pending: BTreeMap::new(),
            repair_requests: BTreeMap::new(),
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
        _frame_deadline_micros: u64,
    ) -> Vec<ReorderedPacket> {
        if self
            .next_sequence
            .is_some_and(|next_sequence| sequence < next_sequence)
        {
            self.stats.late_duplicates = self.stats.late_duplicates.saturating_add(1);
            return Vec::new();
        }

        let release_after_micros = now_micros.saturating_add(self.hold_micros);

        if let std::collections::btree_map::Entry::Vacant(entry) = self.pending.entry(sequence) {
            entry.insert(PendingPacket {
                path_id,
                payload,
                received_at_micros: now_micros,
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
                self.prune_repair_requests();
                continue;
            }

            let Some((&oldest_sequence, oldest_packet)) = self.pending.iter().next() else {
                break;
            };

            let gap_expired = oldest_packet.release_after_micros <= now_micros;
            let capacity_exceeded = self.pending.len() > self.capacity;
            if gap_expired || capacity_exceeded {
                self.stats.released_gap_packets = self.stats.released_gap_packets.saturating_add(1);
                if gap_expired {
                    self.stats.timeout_releases = self.stats.timeout_releases.saturating_add(1);
                }
                if capacity_exceeded {
                    self.stats.capacity_releases = self.stats.capacity_releases.saturating_add(1);
                }
                self.next_sequence = Some(oldest_sequence);
                self.prune_repair_requests();
                continue;
            }

            break;
        }

        ready
    }

    pub fn repair_requests(
        &mut self,
        now_micros: u64,
        interval_micros: u64,
        max_requests: usize,
    ) -> Vec<u64> {
        if max_requests == 0 {
            return Vec::new();
        }

        if self.next_sequence.is_none() {
            self.next_sequence = self.pending.keys().next().copied();
        }

        let Some(next_sequence) = self.next_sequence else {
            return Vec::new();
        };

        let Some((&oldest_sequence, oldest_packet)) = self.pending.iter().next() else {
            return Vec::new();
        };

        if oldest_sequence <= next_sequence || oldest_packet.release_after_micros <= now_micros {
            return Vec::new();
        }

        let mut requests = Vec::new();
        for sequence in next_sequence..oldest_sequence {
            if requests.len() >= max_requests {
                break;
            }

            let should_request = self
                .repair_requests
                .get(&sequence)
                .is_none_or(|last_request| {
                    now_micros.saturating_sub(*last_request) >= interval_micros
                });
            if should_request {
                self.repair_requests.insert(sequence, now_micros);
                requests.push(sequence);
            }
        }

        requests
    }

    pub fn pending_len(&self) -> usize {
        self.pending.len()
    }

    pub fn hold_micros(&self) -> u64 {
        self.hold_micros
    }

    pub fn set_hold_micros(&mut self, hold_micros: u64) {
        self.hold_micros = hold_micros;
        for packet in self.pending.values_mut() {
            packet.release_after_micros = packet.received_at_micros.saturating_add(hold_micros);
        }
    }

    pub fn reset(&mut self) {
        self.next_sequence = self.initial_sequence;
        self.pending.clear();
        self.repair_requests.clear();
        self.stats.pending_depth = 0;
    }

    pub fn stats(&self) -> ReorderStats {
        ReorderStats {
            pending_depth: self.pending.len() as u64,
            ..self.stats
        }
    }

    fn prune_repair_requests(&mut self) {
        if let Some(next_sequence) = self.next_sequence {
            self.repair_requests
                .retain(|sequence, _| *sequence >= next_sequence);
        } else {
            self.repair_requests.clear();
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
    fn explicit_initial_sequence_waits_for_the_true_first_packet() {
        let mut buffer = PacketReorderBuffer::with_initial_sequence(16, 25_000, 10);

        assert!(buffer
            .push(11, 2, b"eleven".to_vec(), 1_000, 100_000)
            .is_empty());
        assert_eq!(
            buffer.push(10, 1, b"ten".to_vec(), 2_000, 100_000),
            vec![
                ReorderedPacket {
                    sequence: 10,
                    path_id: 1,
                    payload: b"ten".to_vec(),
                },
                ReorderedPacket {
                    sequence: 11,
                    path_id: 2,
                    payload: b"eleven".to_vec(),
                },
            ]
        );

        buffer.reset();
        assert!(buffer
            .push(11, 2, b"eleven-again".to_vec(), 3_000, 100_000)
            .is_empty());
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
    fn peer_timestamp_deadline_does_not_shorten_local_hold_time() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 1_000).len(), 1);
        assert!(buffer
            .push(12, 2, b"twelve".to_vec(), 2_000, 2_001)
            .is_empty());

        assert!(buffer.drain_ready(3_000).is_empty());
        assert!(buffer.drain_ready(20_000).is_empty());
        assert_eq!(buffer.drain_ready(28_000).len(), 1);
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

    #[test]
    fn reset_allows_lower_sequence_for_new_session() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(
            buffer.push(100, 1, b"old".to_vec(), 1_000, 100_000).len(),
            1
        );
        assert!(buffer
            .push(0, 1, b"late".to_vec(), 2_000, 100_000)
            .is_empty());

        buffer.reset();

        assert_eq!(
            buffer.push(0, 1, b"new".to_vec(), 3_000, 100_000),
            vec![ReorderedPacket {
                sequence: 0,
                path_id: 1,
                payload: b"new".to_vec()
            }]
        );
        assert_eq!(buffer.pending_len(), 0);
    }

    #[test]
    fn updated_hold_time_applies_to_newly_held_packets() {
        let mut normal = PacketReorderBuffer::new(16, 50_000);
        assert_eq!(
            normal.push(1, 1, b"one".to_vec(), 1_000, 1_000_000).len(),
            1
        );
        assert!(normal
            .push(3, 2, b"three".to_vec(), 2_000, 1_000_000)
            .is_empty());
        assert_eq!(normal.hold_micros(), 50_000);
        assert_eq!(normal.drain_ready(60_000).len(), 1);

        let mut recovery = PacketReorderBuffer::new(16, 50_000);
        recovery.set_hold_micros(500_000);
        assert_eq!(recovery.hold_micros(), 500_000);
        assert_eq!(
            recovery.push(1, 1, b"one".to_vec(), 1_000, 1_000_000).len(),
            1
        );
        assert!(recovery
            .push(3, 2, b"three".to_vec(), 2_000, 1_000_000)
            .is_empty());
        assert!(recovery.drain_ready(60_000).is_empty());
        assert_eq!(recovery.drain_ready(503_000).len(), 1);
    }

    #[test]
    fn increasing_hold_time_extends_existing_pending_deadlines() {
        let mut buffer = PacketReorderBuffer::new(16, 50_000);
        assert_eq!(
            buffer.push(1, 1, b"one".to_vec(), 1_000, 1_000_000).len(),
            1
        );
        assert!(buffer
            .push(3, 2, b"three".to_vec(), 2_000, 1_000_000)
            .is_empty());

        buffer.set_hold_micros(500_000);

        assert!(buffer.drain_ready(60_000).is_empty());
        assert_eq!(buffer.drain_ready(503_000).len(), 1);
    }

    #[test]
    fn decreasing_hold_time_shortens_existing_pending_deadlines() {
        let mut buffer = PacketReorderBuffer::new(16, 500_000);
        assert_eq!(
            buffer.push(1, 1, b"one".to_vec(), 1_000, 1_000_000).len(),
            1
        );
        assert!(buffer
            .push(3, 2, b"three".to_vec(), 2_000, 1_000_000)
            .is_empty());

        buffer.set_hold_micros(50_000);

        assert!(buffer.drain_ready(51_999).is_empty());
        assert_eq!(buffer.drain_ready(52_000).len(), 1);
    }

    #[test]
    fn repair_requests_report_missing_gap_before_timeout() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 0).len(), 1);
        assert!(buffer.push(12, 2, b"twelve".to_vec(), 2_000, 0).is_empty());

        assert_eq!(buffer.repair_requests(3_000, 5_000, 64), vec![11]);
        assert!(buffer.repair_requests(4_000, 5_000, 64).is_empty());
        assert_eq!(buffer.repair_requests(8_000, 5_000, 64), vec![11]);
    }

    #[test]
    fn repair_requests_stop_after_gap_releases() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 0).len(), 1);
        assert!(buffer.push(12, 2, b"twelve".to_vec(), 2_000, 0).is_empty());
        assert_eq!(buffer.repair_requests(3_000, 5_000, 64), vec![11]);

        assert_eq!(buffer.drain_ready(28_000).len(), 1);
        assert!(buffer.repair_requests(29_000, 5_000, 64).is_empty());
    }

    #[test]
    fn repair_packet_fills_gap_before_timeout() {
        let mut buffer = PacketReorderBuffer::new(16, 25_000);

        assert_eq!(buffer.push(10, 1, b"ten".to_vec(), 1_000, 0).len(), 1);
        assert!(buffer.push(12, 2, b"twelve".to_vec(), 2_000, 0).is_empty());
        assert_eq!(buffer.repair_requests(3_000, 5_000, 64), vec![11]);

        let ready = buffer.push(11, 3, b"eleven".to_vec(), 10_000, 0);

        assert_eq!(
            ready,
            vec![
                ReorderedPacket {
                    sequence: 11,
                    path_id: 3,
                    payload: b"eleven".to_vec()
                },
                ReorderedPacket {
                    sequence: 12,
                    path_id: 2,
                    payload: b"twelve".to_vec()
                }
            ]
        );
        assert!(buffer.repair_requests(11_000, 5_000, 64).is_empty());
    }
}
