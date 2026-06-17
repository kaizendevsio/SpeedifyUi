use std::collections::{HashMap, VecDeque};
use std::sync::Arc;

#[derive(Debug)]
pub struct ResendCache {
    capacity: usize,
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
        Self {
            capacity: capacity.max(1),
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

    pub fn prune(&mut self, now_micros: u64) {
        while let Some(front) = self.order.front().copied() {
            let expired = now_micros.saturating_sub(front.inserted_at_micros) > self.ttl_micros;
            let over_capacity = self.entries.len() > self.capacity;
            if !expired && !over_capacity {
                break;
            }

            self.order.pop_front();
            if self
                .entries
                .get(&front.key)
                .is_some_and(|entry| entry.inserted_at_micros == front.inserted_at_micros)
            {
                self.entries.remove(&front.key);
            }
        }
    }
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
}
