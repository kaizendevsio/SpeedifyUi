use std::collections::{HashMap, VecDeque};
use std::sync::Arc;

// Covers the map key/value, order entry, Arc allocation, and collection overhead.
// Payload allocation is accounted separately using Vec::capacity().
const ENTRY_METADATA_BYTES: usize = 128;

#[derive(Debug)]
pub struct ResendCache {
    capacity: usize,
    configured_capacity: usize,
    capacity_tracks_byte_budget: bool,
    byte_capacity: usize,
    bytes: usize,
    accounted_bytes: usize,
    ttl_micros: u64,
    entries: HashMap<(u64, u64), CachedPacket>,
    order: VecDeque<CacheOrderEntry>,
}

#[derive(Debug, Clone)]
struct CachedPacket {
    payload: Arc<Vec<u8>>,
    inserted_at_micros: u64,
    accounted_bytes: usize,
}

#[derive(Debug, Clone, Copy)]
struct CacheOrderEntry {
    key: (u64, u64),
    inserted_at_micros: u64,
}

impl ResendCache {
    pub fn new(capacity: usize, ttl_micros: u64) -> Self {
        let capacity = capacity.max(1);
        Self {
            capacity,
            configured_capacity: capacity,
            capacity_tracks_byte_budget: false,
            byte_capacity: usize::MAX,
            bytes: 0,
            accounted_bytes: 0,
            ttl_micros,
            entries: HashMap::with_capacity(capacity),
            order: VecDeque::with_capacity(capacity),
        }
    }

    pub fn new_with_byte_capacity(capacity: usize, byte_capacity: usize, ttl_micros: u64) -> Self {
        let configured_capacity = capacity.max(1);
        let byte_capacity = byte_capacity.max(1);
        let capacity = effective_packet_capacity(configured_capacity, byte_capacity);
        let initial_allocation = configured_capacity.min(capacity);
        Self {
            capacity,
            configured_capacity,
            capacity_tracks_byte_budget: true,
            byte_capacity,
            bytes: 0,
            accounted_bytes: 0,
            ttl_micros,
            entries: HashMap::with_capacity(initial_allocation),
            order: VecDeque::with_capacity(initial_allocation),
        }
    }

    pub fn insert(
        &mut self,
        session_id: u64,
        sequence: u64,
        payload: Arc<Vec<u8>>,
        now_micros: u64,
    ) {
        self.prune(now_micros);
        let key = (session_id, sequence);
        if let Some(replaced) = self.entries.remove(&key) {
            self.bytes = self.bytes.saturating_sub(replaced.payload.len());
            self.accounted_bytes = self
                .accounted_bytes
                .saturating_sub(replaced.accounted_bytes);
            self.order.retain(|entry| {
                entry.key != key || entry.inserted_at_micros != replaced.inserted_at_micros
            });
        }

        let payload_accounted_bytes = accounted_packet_bytes(&payload);
        if payload_accounted_bytes > self.byte_capacity {
            return;
        }

        self.make_room_for(payload_accounted_bytes);
        self.bytes = self.bytes.saturating_add(payload.len());
        self.accounted_bytes = self.accounted_bytes.saturating_add(payload_accounted_bytes);
        self.entries.insert(
            key,
            CachedPacket {
                payload,
                inserted_at_micros: now_micros,
                accounted_bytes: payload_accounted_bytes,
            },
        );
        self.order.push_back(CacheOrderEntry {
            key,
            inserted_at_micros: now_micros,
        });
        self.prune(now_micros);
    }

    pub fn get(&mut self, session_id: u64, sequence: u64, now_micros: u64) -> Option<Arc<Vec<u8>>> {
        self.prune(now_micros);
        self.entries
            .get(&(session_id, sequence))
            .map(|entry| entry.payload.clone())
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn bytes_len(&self) -> usize {
        self.bytes
    }

    pub fn accounted_bytes_len(&self) -> usize {
        self.accounted_bytes
    }

    pub fn byte_capacity(&self) -> usize {
        self.byte_capacity
    }

    pub fn packet_capacity(&self) -> usize {
        self.capacity
    }

    pub fn set_byte_capacity(&mut self, byte_capacity: usize, now_micros: u64) {
        let previous_byte_capacity = self.byte_capacity;
        self.byte_capacity = byte_capacity.max(1);
        if self.capacity_tracks_byte_budget {
            self.capacity = effective_packet_capacity(self.configured_capacity, self.byte_capacity);
        }
        self.prune(now_micros);
        if self.byte_capacity < previous_byte_capacity {
            self.shrink_backing_allocations();
        }
    }

    pub fn prune(&mut self, now_micros: u64) {
        while let Some(front) = self.order.front().copied() {
            let expired = now_micros.saturating_sub(front.inserted_at_micros) > self.ttl_micros;
            let over_capacity = self.entries.len() > self.capacity;
            let over_byte_capacity = self.accounted_bytes > self.byte_capacity;
            if !expired && !over_capacity && !over_byte_capacity {
                break;
            }

            self.order.pop_front();
            if self
                .entries
                .get(&front.key)
                .is_some_and(|entry| entry.inserted_at_micros == front.inserted_at_micros)
            {
                if let Some(removed) = self.entries.remove(&front.key) {
                    self.bytes = self.bytes.saturating_sub(removed.payload.len());
                    self.accounted_bytes =
                        self.accounted_bytes.saturating_sub(removed.accounted_bytes);
                }
            }
        }
    }

    fn make_room_for(&mut self, incoming_bytes: usize) {
        while self.entries.len() >= self.capacity
            || self.accounted_bytes.saturating_add(incoming_bytes) > self.byte_capacity
        {
            let Some(front) = self.order.pop_front() else {
                break;
            };
            if self
                .entries
                .get(&front.key)
                .is_some_and(|entry| entry.inserted_at_micros == front.inserted_at_micros)
            {
                if let Some(removed) = self.entries.remove(&front.key) {
                    self.bytes = self.bytes.saturating_sub(removed.payload.len());
                    self.accounted_bytes =
                        self.accounted_bytes.saturating_sub(removed.accounted_bytes);
                }
            }
        }
    }

    fn shrink_backing_allocations(&mut self) {
        let target = self.entries.len().max(1);
        if self.entries.capacity() > target.saturating_mul(2).max(16) {
            self.entries.shrink_to(target);
        }
        if self.order.capacity() > target.saturating_mul(2).max(16) {
            self.order.shrink_to(target);
        }
    }
}

fn accounted_packet_bytes(payload: &Vec<u8>) -> usize {
    payload.capacity().saturating_add(ENTRY_METADATA_BYTES)
}

fn effective_packet_capacity(configured_capacity: usize, byte_capacity: usize) -> usize {
    configured_capacity.max((byte_capacity / ENTRY_METADATA_BYTES).max(1))
}

pub fn recommended_repair_cache_bytes(
    observed_bits_per_second: u64,
    retention_micros: u64,
    minimum_bytes: usize,
    maximum_bytes: usize,
) -> usize {
    let minimum_bytes = minimum_bytes.max(1);
    let maximum_bytes = maximum_bytes.max(minimum_bytes);
    let bytes_for_window = u128::from(observed_bits_per_second)
        .saturating_mul(u128::from(retention_micros))
        .saturating_div(8_000_000);
    let bytes_for_window = usize::try_from(bytes_for_window).unwrap_or(usize::MAX);
    bytes_for_window.clamp(minimum_bytes, maximum_bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resend_cache_returns_recent_payload() {
        let payload = Arc::new(b"packet".to_vec());
        let mut cache = ResendCache::new(4, 3_000_000);

        cache.insert(1, 42, payload.clone(), 10);

        assert_eq!(cache.get(1, 42, 20).as_deref(), Some(payload.as_ref()));
    }

    #[test]
    fn resend_cache_evicts_by_capacity() {
        let mut cache = ResendCache::new(2, 3_000_000);

        cache.insert(1, 1, Arc::new(vec![1]), 10);
        cache.insert(1, 2, Arc::new(vec![2]), 20);
        cache.insert(1, 3, Arc::new(vec![3]), 30);

        assert!(cache.get(1, 1, 40).is_none());
        assert!(cache.get(1, 2, 40).is_some());
        assert!(cache.get(1, 3, 40).is_some());
    }

    #[test]
    fn resend_cache_evicts_by_age() {
        let mut cache = ResendCache::new(4, 100);

        cache.insert(1, 1, Arc::new(vec![1]), 10);

        assert!(cache.get(1, 1, 109).is_some());
        assert!(cache.get(1, 1, 111).is_none());
    }

    #[test]
    fn resend_cache_evicts_by_total_payload_bytes() {
        let mut cache =
            ResendCache::new_with_byte_capacity(10, ENTRY_METADATA_BYTES + 5, 3_000_000);

        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 10);
        cache.insert(1, 2, Arc::new(vec![4, 5, 6]), 20);

        assert!(cache.get(1, 1, 30).is_none());
        assert!(cache.get(1, 2, 30).is_some());
        assert_eq!(cache.bytes_len(), 3);
        assert_eq!(cache.accounted_bytes_len(), ENTRY_METADATA_BYTES + 3);
    }

    #[test]
    fn replacing_entry_keeps_byte_accounting_exact() {
        let mut cache =
            ResendCache::new_with_byte_capacity(10, ENTRY_METADATA_BYTES + 10, 3_000_000);

        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 10);
        cache.insert(1, 1, Arc::new(vec![4, 5]), 20);

        assert_eq!(cache.len(), 1);
        assert_eq!(cache.bytes_len(), 2);
        assert_eq!(cache.accounted_bytes_len(), ENTRY_METADATA_BYTES + 2);
        assert_eq!(cache.get(1, 1, 30).as_deref(), Some(&vec![4, 5]));
    }

    #[test]
    fn lowering_byte_capacity_evicts_oldest_entries_immediately() {
        let initial_budget = (ENTRY_METADATA_BYTES + 3) * 2;
        let reduced_budget = ENTRY_METADATA_BYTES + 3;
        let mut cache = ResendCache::new_with_byte_capacity(10, initial_budget, 3_000_000);
        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 10);
        cache.insert(1, 2, Arc::new(vec![4, 5, 6]), 20);

        cache.set_byte_capacity(reduced_budget, 30);

        assert_eq!(cache.byte_capacity(), reduced_budget);
        assert_eq!(cache.packet_capacity(), 10);
        assert!(cache.get(1, 1, 30).is_none());
        assert!(cache.get(1, 2, 30).is_some());
        assert_eq!(cache.accounted_bytes_len(), reduced_budget);
    }

    #[test]
    fn lowering_byte_capacity_releases_oversized_collection_backing_storage() {
        let per_packet_bytes = ENTRY_METADATA_BYTES + 1;
        let initial_budget = per_packet_bytes * 1_024;
        let reduced_budget = per_packet_bytes * 4;
        let mut cache = ResendCache::new_with_byte_capacity(1_024, initial_budget, 3_000_000);
        for sequence in 0..1_024 {
            cache.insert(1, sequence, Arc::new(vec![1]), sequence);
        }
        let previous_entry_capacity = cache.entries.capacity();
        let previous_order_capacity = cache.order.capacity();

        cache.set_byte_capacity(reduced_budget, 2_000);

        assert!(cache.len() <= 4);
        assert!(cache.entries.capacity() < previous_entry_capacity);
        assert!(cache.order.capacity() < previous_order_capacity);
        assert!(cache.accounted_bytes_len() <= reduced_budget);
    }

    #[test]
    fn byte_budget_expands_logical_packet_capacity_beyond_fixed_hint() {
        let packet_count = 10_000;
        let per_packet_bytes = ENTRY_METADATA_BYTES + 1;
        let byte_budget = packet_count * per_packet_bytes;
        let mut cache = ResendCache::new_with_byte_capacity(4096, byte_budget, 3_000_000);

        for sequence in 0..packet_count as u64 {
            cache.insert(1, sequence, Arc::new(vec![1]), sequence);
        }

        assert_eq!(cache.packet_capacity(), byte_budget / ENTRY_METADATA_BYTES);
        assert_eq!(cache.len(), packet_count);
        assert!(cache.get(1, 0, packet_count as u64).is_some());
        assert_eq!(cache.bytes_len(), packet_count);
        assert_eq!(cache.accounted_bytes_len(), byte_budget);
    }

    #[test]
    fn byte_capacity_update_recomputes_logical_packet_capacity() {
        let initial_budget = ENTRY_METADATA_BYTES * 5000;
        let increased_budget = ENTRY_METADATA_BYTES * 9000;
        let mut cache = ResendCache::new_with_byte_capacity(4096, initial_budget, 3_000_000);

        assert_eq!(cache.packet_capacity(), 5000);

        cache.set_byte_capacity(increased_budget, 0);

        assert_eq!(cache.packet_capacity(), 9000);
    }

    #[test]
    fn packet_larger_than_total_budget_is_not_retained() {
        let mut cache =
            ResendCache::new_with_byte_capacity(4096, ENTRY_METADATA_BYTES + 2, 3_000_000);

        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 10);

        assert!(cache.is_empty());
        assert_eq!(cache.bytes_len(), 0);
        assert_eq!(cache.accounted_bytes_len(), 0);
    }

    #[test]
    fn byte_accounting_uses_payload_allocation_capacity() {
        let mut payload = Vec::with_capacity(64);
        payload.extend_from_slice(&[1, 2, 3]);
        let mut cache =
            ResendCache::new_with_byte_capacity(4096, ENTRY_METADATA_BYTES + 64, 3_000_000);

        cache.insert(1, 1, Arc::new(payload), 10);

        assert_eq!(cache.bytes_len(), 3);
        assert_eq!(cache.accounted_bytes_len(), ENTRY_METADATA_BYTES + 64);
    }

    #[test]
    fn recommended_cache_size_tracks_rate_and_is_bounded() {
        assert_eq!(
            recommended_repair_cache_bytes(8_000_000, 3_000_000, 1_000_000, 16_000_000),
            3_000_000
        );
        assert_eq!(
            recommended_repair_cache_bytes(1, 3_000_000, 1_000_000, 16_000_000),
            1_000_000
        );
        assert_eq!(
            recommended_repair_cache_bytes(1_000_000_000, 3_000_000, 1_000_000, 16_000_000),
            16_000_000
        );
    }
}
