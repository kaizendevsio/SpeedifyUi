use std::collections::{HashMap, VecDeque};
use std::sync::Arc;

#[derive(Debug)]
pub struct ResendCache {
    capacity: usize,
    byte_capacity: usize,
    bytes: usize,
    ttl_micros: u64,
    entries: HashMap<(u64, u64), CachedPacket>,
    order: VecDeque<CacheOrderEntry>,
}

#[derive(Debug, Clone)]
struct CachedPacket {
    payload: Arc<Vec<u8>>,
    inserted_at_micros: u64,
}

#[derive(Debug, Clone, Copy)]
struct CacheOrderEntry {
    key: (u64, u64),
    inserted_at_micros: u64,
}

impl ResendCache {
    pub fn new(capacity: usize, ttl_micros: u64) -> Self {
        Self::new_with_byte_capacity(capacity, usize::MAX, ttl_micros)
    }

    pub fn new_with_byte_capacity(capacity: usize, byte_capacity: usize, ttl_micros: u64) -> Self {
        Self {
            capacity: capacity.max(1),
            byte_capacity: byte_capacity.max(1),
            bytes: 0,
            ttl_micros,
            entries: HashMap::with_capacity(capacity.max(1)),
            order: VecDeque::with_capacity(capacity.max(1)),
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
        }
        self.bytes = self.bytes.saturating_add(payload.len());
        self.entries.insert(
            key,
            CachedPacket {
                payload,
                inserted_at_micros: now_micros,
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

    pub fn byte_capacity(&self) -> usize {
        self.byte_capacity
    }

    pub fn packet_capacity(&self) -> usize {
        self.capacity
    }

    pub fn set_byte_capacity(&mut self, byte_capacity: usize, now_micros: u64) {
        self.byte_capacity = byte_capacity.max(1);
        self.prune(now_micros);
    }

    pub fn prune(&mut self, now_micros: u64) {
        while let Some(front) = self.order.front().copied() {
            let expired = now_micros.saturating_sub(front.inserted_at_micros) > self.ttl_micros;
            let over_capacity = self.entries.len() > self.capacity;
            let over_byte_capacity = self.bytes > self.byte_capacity;
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
                }
            }
        }
    }
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
        let mut cache = ResendCache::new_with_byte_capacity(10, 5, 3_000_000);

        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 10);
        cache.insert(1, 2, Arc::new(vec![4, 5, 6]), 20);

        assert!(cache.get(1, 1, 30).is_none());
        assert!(cache.get(1, 2, 30).is_some());
        assert_eq!(cache.bytes_len(), 3);
    }

    #[test]
    fn replacing_entry_keeps_byte_accounting_exact() {
        let mut cache = ResendCache::new_with_byte_capacity(10, 10, 3_000_000);

        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 10);
        cache.insert(1, 1, Arc::new(vec![4, 5]), 20);

        assert_eq!(cache.len(), 1);
        assert_eq!(cache.bytes_len(), 2);
        assert_eq!(cache.get(1, 1, 30).as_deref(), Some(&vec![4, 5]));
    }

    #[test]
    fn lowering_byte_capacity_evicts_oldest_entries_immediately() {
        let mut cache = ResendCache::new_with_byte_capacity(10, 10, 3_000_000);
        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 10);
        cache.insert(1, 2, Arc::new(vec![4, 5, 6]), 20);

        cache.set_byte_capacity(3, 30);

        assert_eq!(cache.byte_capacity(), 3);
        assert_eq!(cache.packet_capacity(), 10);
        assert!(cache.get(1, 1, 30).is_none());
        assert!(cache.get(1, 2, 30).is_some());
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
