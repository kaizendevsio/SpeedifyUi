use std::collections::{HashMap, HashSet, VecDeque};

pub type SessionHandshakeNonce = [u8; 16];

const DEFAULT_CHALLENGE_TTL_MICROS: u64 = 5_000_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionChallengeOutcome {
    Issued { challenge: SessionHandshakeNonce },
    Existing { challenge: SessionHandshakeNonce },
    RejectedZeroSession,
    RejectedZeroRequestNonce,
    RejectedZeroChallenge,
    RejectedRetiredSession,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionProofOutcome {
    Opened,
    AlreadyCurrent,
    RejectedZeroSession,
    RejectedZeroRequestNonce,
    RejectedZeroChallenge,
    RejectedMissingChallenge,
    RejectedMismatchedChallenge,
    RejectedExpiredChallenge,
    RejectedRetiredSession,
}

#[derive(Debug, Clone, Copy)]
struct PendingChallenge {
    challenge: SessionHandshakeNonce,
    expires_at_micros: u64,
    generation: u64,
}

#[derive(Debug)]
pub struct AuthenticatedSessionTracker {
    current: Option<u64>,
    current_handshake: Option<(u64, SessionHandshakeNonce, SessionHandshakeNonce)>,
    pending_capacity: usize,
    challenge_ttl_micros: u64,
    next_generation: u64,
    pending_order: VecDeque<((u64, SessionHandshakeNonce), u64)>,
    pending: HashMap<(u64, SessionHandshakeNonce), PendingChallenge>,
    retired_order: VecDeque<u64>,
    retired: HashSet<u64>,
}

impl AuthenticatedSessionTracker {
    pub fn new(pending_capacity: usize) -> Self {
        Self::with_challenge_ttl(pending_capacity, DEFAULT_CHALLENGE_TTL_MICROS)
    }

    pub fn with_challenge_ttl(pending_capacity: usize, challenge_ttl_micros: u64) -> Self {
        let pending_capacity = pending_capacity.max(1);
        Self {
            current: None,
            current_handshake: None,
            pending_capacity,
            challenge_ttl_micros: challenge_ttl_micros.max(1),
            next_generation: 0,
            pending_order: VecDeque::with_capacity(pending_capacity),
            pending: HashMap::with_capacity(pending_capacity),
            retired_order: VecDeque::with_capacity(pending_capacity),
            retired: HashSet::with_capacity(pending_capacity),
        }
    }

    pub fn current(&self) -> Option<u64> {
        self.current
    }

    pub fn accepts(&self, session_id: u64) -> bool {
        self.current == Some(session_id)
    }

    pub fn pending_len(&self) -> usize {
        self.pending.len()
    }

    pub fn retired_len(&self) -> usize {
        self.retired.len()
    }

    pub fn issue_challenge(
        &mut self,
        session_id: u64,
        request_nonce: SessionHandshakeNonce,
        fresh_challenge: SessionHandshakeNonce,
        now_micros: u64,
    ) -> SessionChallengeOutcome {
        if session_id == 0 {
            return SessionChallengeOutcome::RejectedZeroSession;
        }
        if is_zero_nonce(&request_nonce) {
            return SessionChallengeOutcome::RejectedZeroRequestNonce;
        }
        if is_zero_nonce(&fresh_challenge) {
            return SessionChallengeOutcome::RejectedZeroChallenge;
        }
        if self.retired.contains(&session_id) {
            return SessionChallengeOutcome::RejectedRetiredSession;
        }

        self.prune_expired(now_micros);
        let key = (session_id, request_nonce);
        if let Some(existing) = self.pending.get(&key) {
            return SessionChallengeOutcome::Existing {
                challenge: existing.challenge,
            };
        }

        self.make_room();
        self.next_generation = self.next_generation.wrapping_add(1).max(1);
        let pending = PendingChallenge {
            challenge: fresh_challenge,
            expires_at_micros: now_micros.saturating_add(self.challenge_ttl_micros),
            generation: self.next_generation,
        };
        self.pending.insert(key, pending);
        self.pending_order.push_back((key, pending.generation));

        SessionChallengeOutcome::Issued {
            challenge: fresh_challenge,
        }
    }

    pub fn consume_proof(
        &mut self,
        session_id: u64,
        request_nonce: SessionHandshakeNonce,
        challenge: SessionHandshakeNonce,
        now_micros: u64,
    ) -> SessionProofOutcome {
        if session_id == 0 {
            return SessionProofOutcome::RejectedZeroSession;
        }
        if is_zero_nonce(&request_nonce) {
            return SessionProofOutcome::RejectedZeroRequestNonce;
        }
        if is_zero_nonce(&challenge) {
            return SessionProofOutcome::RejectedZeroChallenge;
        }
        if self.retired.contains(&session_id) {
            return SessionProofOutcome::RejectedRetiredSession;
        }

        let key = (session_id, request_nonce);
        let Some(pending) = self.pending.get(&key).copied() else {
            if self.current_handshake.is_some_and(
                |(current_session_id, current_request_nonce, current_challenge)| {
                    current_session_id == session_id
                        && constant_time_nonce_eq(&current_request_nonce, &request_nonce)
                        && constant_time_nonce_eq(&current_challenge, &challenge)
                },
            ) {
                return SessionProofOutcome::AlreadyCurrent;
            }
            return SessionProofOutcome::RejectedMissingChallenge;
        };
        if now_micros >= pending.expires_at_micros {
            self.pending.remove(&key);
            return SessionProofOutcome::RejectedExpiredChallenge;
        }
        if !constant_time_nonce_eq(&pending.challenge, &challenge) {
            return SessionProofOutcome::RejectedMismatchedChallenge;
        }

        self.pending.remove(&key);
        self.current_handshake = Some((session_id, request_nonce, challenge));
        if self.current == Some(session_id) {
            SessionProofOutcome::AlreadyCurrent
        } else {
            if let Some(previous_session_id) = self.current.replace(session_id) {
                self.retire(previous_session_id);
            }
            self.pending.clear();
            self.pending_order.clear();
            SessionProofOutcome::Opened
        }
    }

    fn prune_expired(&mut self, now_micros: u64) {
        self.pending
            .retain(|_, pending| now_micros < pending.expires_at_micros);
        self.prune_stale_order_entries();
    }

    fn make_room(&mut self) {
        self.prune_stale_order_entries();
        while self.pending.len() >= self.pending_capacity {
            let Some((key, generation)) = self.pending_order.pop_front() else {
                break;
            };
            if self
                .pending
                .get(&key)
                .is_some_and(|pending| pending.generation == generation)
            {
                self.pending.remove(&key);
            }
        }
    }

    fn prune_stale_order_entries(&mut self) {
        while let Some((key, generation)) = self.pending_order.front() {
            if self
                .pending
                .get(key)
                .is_some_and(|pending| pending.generation == *generation)
            {
                break;
            }
            self.pending_order.pop_front();
        }
    }

    fn retire(&mut self, session_id: u64) {
        if self.retired.insert(session_id) {
            self.retired_order.push_back(session_id);
        }
        while self.retired_order.len() > self.pending_capacity {
            if let Some(expired) = self.retired_order.pop_front() {
                self.retired.remove(&expired);
            }
        }
    }
}

fn is_zero_nonce(nonce: &SessionHandshakeNonce) -> bool {
    nonce.iter().all(|byte| *byte == 0)
}

fn constant_time_nonce_eq(left: &SessionHandshakeNonce, right: &SessionHandshakeNonce) -> bool {
    left.iter()
        .zip(right)
        .fold(0_u8, |difference, (left, right)| {
            difference | (left ^ right)
        })
        == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    fn nonce(value: u8) -> SessionHandshakeNonce {
        [value; 16]
    }

    fn complete_open(
        tracker: &mut AuthenticatedSessionTracker,
        session_id: u64,
        request: SessionHandshakeNonce,
        challenge: SessionHandshakeNonce,
        now_micros: u64,
    ) -> SessionProofOutcome {
        assert_eq!(
            tracker.issue_challenge(session_id, request, challenge, now_micros),
            SessionChallengeOutcome::Issued { challenge }
        );
        tracker.consume_proof(session_id, request, challenge, now_micros + 1)
    }

    #[test]
    fn valid_current_challenge_opens_session_once() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(8, 100);

        assert_eq!(
            complete_open(&mut tracker, 42, nonce(1), nonce(2), 10),
            SessionProofOutcome::Opened
        );
        assert!(tracker.accepts(42));
        assert_eq!(tracker.pending_len(), 0);
    }

    #[test]
    fn current_proof_is_idempotent_when_session_acceptance_is_lost() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(8, 100);
        let request = nonce(1);
        let challenge = nonce(2);

        assert_eq!(
            complete_open(&mut tracker, 42, request, challenge, 10),
            SessionProofOutcome::Opened
        );
        assert_eq!(
            tracker.consume_proof(42, request, challenge, 12),
            SessionProofOutcome::AlreadyCurrent
        );
    }

    #[test]
    fn mismatched_proof_fails_without_consuming_valid_challenge() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(8, 100);
        let request = nonce(1);
        let challenge = nonce(2);

        tracker.issue_challenge(42, request, challenge, 10);
        assert_eq!(
            tracker.consume_proof(42, request, nonce(3), 11),
            SessionProofOutcome::RejectedMismatchedChallenge
        );
        assert_eq!(
            tracker.consume_proof(42, request, challenge, 12),
            SessionProofOutcome::Opened
        );
    }

    #[test]
    fn expired_proof_fails_using_local_monotonic_time() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(8, 100);
        let request = nonce(1);
        let challenge = nonce(2);

        tracker.issue_challenge(42, request, challenge, 1_000);
        assert_eq!(
            tracker.consume_proof(42, request, challenge, 1_100),
            SessionProofOutcome::RejectedExpiredChallenge
        );
        assert_eq!(tracker.current(), None);
    }

    #[test]
    fn repeated_open_request_reuses_live_challenge() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(8, 100);
        let request = nonce(1);

        assert_eq!(
            tracker.issue_challenge(42, request, nonce(2), 10),
            SessionChallengeOutcome::Issued {
                challenge: nonce(2)
            }
        );
        assert_eq!(
            tracker.issue_challenge(42, request, nonce(9), 11),
            SessionChallengeOutcome::Existing {
                challenge: nonce(2)
            }
        );
    }

    #[test]
    fn captured_proof_cannot_reopen_after_retired_retention_eviction() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(2, 100);
        let old_request = nonce(1);
        let old_challenge = nonce(2);

        assert_eq!(
            complete_open(&mut tracker, 42, old_request, old_challenge, 10),
            SessionProofOutcome::Opened
        );

        for index in 3_u8..=40 {
            assert_eq!(
                complete_open(
                    &mut tracker,
                    u64::from(index),
                    nonce(index),
                    nonce(index.wrapping_add(64)),
                    u64::from(index) * 10,
                ),
                SessionProofOutcome::Opened
            );
        }
        assert_eq!(tracker.current(), Some(40));

        let replacement_challenge = nonce(99);
        assert_eq!(
            tracker.issue_challenge(42, old_request, replacement_challenge, 1_000),
            SessionChallengeOutcome::Issued {
                challenge: replacement_challenge
            }
        );
        assert_eq!(
            tracker.consume_proof(42, old_request, old_challenge, 1_001),
            SessionProofOutcome::RejectedMismatchedChallenge
        );
        assert_eq!(tracker.current(), Some(40));
        assert_eq!(tracker.retired_len(), 2);
    }

    #[test]
    fn opening_a_new_session_invalidates_other_in_flight_challenges() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(8, 1_000);
        let old_request = nonce(1);
        let old_challenge = nonce(2);
        let new_request = nonce(3);
        let new_challenge = nonce(4);

        assert_eq!(
            tracker.issue_challenge(41, old_request, old_challenge, 10),
            SessionChallengeOutcome::Issued {
                challenge: old_challenge
            }
        );
        assert_eq!(
            tracker.issue_challenge(42, new_request, new_challenge, 11),
            SessionChallengeOutcome::Issued {
                challenge: new_challenge
            }
        );
        assert_eq!(
            tracker.consume_proof(42, new_request, new_challenge, 12),
            SessionProofOutcome::Opened
        );
        assert_eq!(
            tracker.consume_proof(41, old_request, old_challenge, 13),
            SessionProofOutcome::RejectedMissingChallenge
        );
        assert_eq!(tracker.current(), Some(42));
    }

    #[test]
    fn retired_session_cannot_be_reopened_with_a_fresh_challenge() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(8, 1_000);

        assert_eq!(
            complete_open(&mut tracker, 41, nonce(1), nonce(2), 10),
            SessionProofOutcome::Opened
        );
        assert_eq!(
            complete_open(&mut tracker, 42, nonce(3), nonce(4), 20),
            SessionProofOutcome::Opened
        );

        assert_eq!(
            tracker.issue_challenge(41, nonce(5), nonce(6), 30),
            SessionChallengeOutcome::RejectedRetiredSession
        );
        assert_eq!(
            tracker.consume_proof(41, nonce(5), nonce(6), 31),
            SessionProofOutcome::RejectedRetiredSession
        );
        assert_eq!(tracker.current(), Some(42));
    }

    #[test]
    fn pending_challenge_state_is_bounded_and_evicts_oldest() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(2, 1_000);

        tracker.issue_challenge(1, nonce(1), nonce(11), 10);
        tracker.issue_challenge(2, nonce(2), nonce(12), 11);
        tracker.issue_challenge(3, nonce(3), nonce(13), 12);

        assert_eq!(tracker.pending_len(), 2);
        assert_eq!(
            tracker.consume_proof(1, nonce(1), nonce(11), 13),
            SessionProofOutcome::RejectedMissingChallenge
        );
        assert_eq!(
            tracker.consume_proof(2, nonce(2), nonce(12), 13),
            SessionProofOutcome::Opened
        );
    }

    #[test]
    fn retired_session_state_is_bounded_and_keeps_most_recent_ids() {
        let mut tracker = AuthenticatedSessionTracker::with_challenge_ttl(2, 1_000);

        for session_id in 1_u64..=5 {
            assert_eq!(
                complete_open(
                    &mut tracker,
                    session_id,
                    nonce(session_id as u8),
                    nonce((session_id + 10) as u8),
                    session_id * 10,
                ),
                SessionProofOutcome::Opened
            );
        }

        assert_eq!(tracker.retired_len(), 2);
        assert_eq!(
            tracker.issue_challenge(4, nonce(20), nonce(21), 100),
            SessionChallengeOutcome::RejectedRetiredSession
        );
        assert!(matches!(
            tracker.issue_challenge(1, nonce(22), nonce(23), 100),
            SessionChallengeOutcome::Issued { .. }
        ));
    }

    #[test]
    fn zero_session_or_nonce_material_is_rejected() {
        let mut tracker = AuthenticatedSessionTracker::new(8);

        assert_eq!(
            tracker.issue_challenge(0, nonce(1), nonce(2), 10),
            SessionChallengeOutcome::RejectedZeroSession
        );
        assert_eq!(
            tracker.issue_challenge(1, nonce(0), nonce(2), 10),
            SessionChallengeOutcome::RejectedZeroRequestNonce
        );
        assert_eq!(
            tracker.issue_challenge(1, nonce(1), nonce(0), 10),
            SessionChallengeOutcome::RejectedZeroChallenge
        );
    }
}
