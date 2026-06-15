use std::collections::BTreeMap;

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

#[derive(Debug)]
pub struct PacketReorderBuffer {
    next_sequence: Option<u64>,
    pending: BTreeMap<u64, PendingPacket>,
    capacity: usize,
    hold_micros: u64,
}

impl PacketReorderBuffer {
    pub fn new(capacity: usize, hold_micros: u64) -> Self {
        Self {
            next_sequence: None,
            pending: BTreeMap::new(),
            capacity: capacity.max(1),
            hold_micros,
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
            return Vec::new();
        }

        let hold_deadline = now_micros.saturating_add(self.hold_micros);
        let release_after_micros = if frame_deadline_micros == 0 {
            hold_deadline
        } else {
            frame_deadline_micros.min(hold_deadline)
        };

        self.pending.entry(sequence).or_insert(PendingPacket {
            path_id,
            payload,
            release_after_micros,
        });

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
    }
}
