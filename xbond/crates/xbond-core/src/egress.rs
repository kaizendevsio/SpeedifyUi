use serde::{Deserialize, Serialize};

use crate::{PathHealthSnapshot, RecoveryConfig, TrafficMode};

const REPLACEMENT_FRESH_MS: u64 = 1_000;
const REPLACEMENT_SUCCESS_COUNT: u32 = 3;
// Leave enough of the one-second failover budget for the transactional route helper.
// The replacement must still have three fresh successful heartbeat samples.
const ACTIVE_SILENT_FAILURE_MS: u64 = 750;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "kebab-case")]
pub enum EgressTarget {
    Tunnel,
    Direct {
        path_id: u16,
        interface_name: String,
    },
    None,
}

impl Default for EgressTarget {
    fn default() -> Self {
        Self::Tunnel
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EgressDecision {
    pub target: EgressTarget,
    pub reason: String,
    pub failed_path_id: Option<u16>,
}

#[derive(Debug, Clone, Copy)]
pub struct EgressThresholds {
    pub adaptive_enter_rounds: u32,
    pub adaptive_return_ms: u64,
}

impl Default for EgressThresholds {
    fn default() -> Self {
        Self {
            adaptive_enter_rounds: 2,
            adaptive_return_ms: 30_000,
        }
    }
}

pub struct EgressEvaluation<'a> {
    pub mode: TrafficMode,
    pub paths: &'a [PathHealthSnapshot],
    pub preferred_path_id: Option<u16>,
    pub tunnel_usable: bool,
    pub scheduler_round: bool,
    pub now_ms: u64,
    pub recovery: RecoveryConfig,
}

#[derive(Debug, Clone)]
pub struct EgressController {
    active: EgressTarget,
    reason: String,
    switch_count: u64,
    last_switch_ms: Option<u64>,
    degraded_rounds: u32,
    clean_since: Option<(u16, u64)>,
    thresholds: EgressThresholds,
}

impl EgressController {
    pub fn new(mode: TrafficMode, thresholds: EgressThresholds) -> Self {
        let active = if mode == TrafficMode::Tunnel {
            EgressTarget::Tunnel
        } else {
            EgressTarget::None
        };
        Self {
            active,
            reason: "Waiting for a proven egress path.".to_string(),
            switch_count: 0,
            last_switch_ms: None,
            degraded_rounds: 0,
            clean_since: None,
            thresholds,
        }
    }

    pub fn active(&self) -> &EgressTarget {
        &self.active
    }

    pub fn reason(&self) -> &str {
        &self.reason
    }

    pub fn switch_count(&self) -> u64 {
        self.switch_count
    }

    pub fn last_switch_ms(&self) -> Option<u64> {
        self.last_switch_ms
    }

    pub fn clean_return_progress_ms(&self, now_ms: u64) -> u64 {
        self.clean_since
            .map(|(_, since)| {
                now_ms
                    .saturating_sub(since)
                    .min(self.thresholds.adaptive_return_ms)
            })
            .unwrap_or(0)
    }

    pub fn restore_active(&mut self, target: EgressTarget, reason: impl Into<String>) {
        self.active = target;
        self.reason = reason.into();
    }

    pub fn evaluate(&mut self, input: EgressEvaluation<'_>) -> Option<EgressDecision> {
        let decision = match input.mode {
            TrafficMode::Tunnel => EgressDecision {
                target: EgressTarget::Tunnel,
                reason: "Tunnel mode is configured.".to_string(),
                failed_path_id: None,
            },
            TrafficMode::DirectFailover => self.direct_decision(&input),
            TrafficMode::Adaptive => self.adaptive_decision(&input),
        };

        if decision.target == self.active {
            self.reason = decision.reason;
            return None;
        }

        self.clean_since = None;
        self.active = decision.target.clone();
        self.reason = decision.reason.clone();
        self.switch_count = self.switch_count.saturating_add(1);
        self.last_switch_ms = Some(input.now_ms);
        Some(decision)
    }

    fn direct_decision(&mut self, input: &EgressEvaluation<'_>) -> EgressDecision {
        self.degraded_rounds = 0;
        self.clean_since = None;
        let current = active_direct_path(&self.active, input.paths);
        if let Some(current) = current {
            if path_hard_failed(current) || active_silent_failure(current) {
                if let Some(replacement) =
                    best_proven_path(input.paths, input.preferred_path_id, Some(current.path_id))
                {
                    return direct_target(
                        replacement,
                        "Active direct path failed; switched to a proven replacement.",
                        Some(current.path_id),
                    );
                }
                if heartbeat_only_failure(current) {
                    return direct_target(
                        current,
                        "Active path heartbeat is stale, but no fresh replacement is proven yet.",
                        None,
                    );
                }
                return EgressDecision {
                    target: EgressTarget::None,
                    reason:
                        "The active physical interface failed and no usable replacement exists."
                            .to_string(),
                    failed_path_id: Some(current.path_id),
                };
            }

            if input.scheduler_round {
                if let Some(preferred) = preferred_proven_path(input.paths, input.preferred_path_id)
                {
                    if preferred.path_id != current.path_id {
                        return direct_target(
                            preferred,
                            "A better stable direct path became preferred.",
                            None,
                        );
                    }
                }
            }
            return direct_target(current, "The active direct path remains healthy.", None);
        }

        if let Some(path) = best_proven_path(input.paths, input.preferred_path_id, None) {
            direct_target(path, "Selected the best proven direct path.", None)
        } else {
            EgressDecision {
                target: EgressTarget::None,
                reason: "No physical path has enough fresh health evidence.".to_string(),
                failed_path_id: None,
            }
        }
    }

    fn adaptive_decision(&mut self, input: &EgressEvaluation<'_>) -> EgressDecision {
        let clean = best_clean_path(input.paths, input.preferred_path_id, input.recovery);
        let usable = best_usable_path(input.paths, input.preferred_path_id);

        if matches!(self.active, EgressTarget::Tunnel) {
            if let Some(clean) = clean {
                let since = match self.clean_since {
                    Some((path_id, since)) if path_id == clean.path_id => since,
                    _ => {
                        self.clean_since = Some((clean.path_id, input.now_ms));
                        input.now_ms
                    }
                };
                if input.now_ms.saturating_sub(since) >= self.thresholds.adaptive_return_ms {
                    self.degraded_rounds = 0;
                    self.clean_since = None;
                    return direct_target(
                        clean,
                        "A direct path stayed clean for 30 seconds.",
                        None,
                    );
                }
            } else {
                self.clean_since = None;
            }

            if input.tunnel_usable {
                return EgressDecision {
                    target: EgressTarget::Tunnel,
                    reason: "Reliable tunnel fallback remains active while direct paths recover."
                        .to_string(),
                    failed_path_id: None,
                };
            }
            if let Some(path) = usable {
                return direct_target(
                    path,
                    "Tunnel fallback is unavailable; using the best reachable direct path.",
                    None,
                );
            }
            if let Some(path) = best_emergency_direct_path(input.paths, input.preferred_path_id) {
                return direct_target(
                    path,
                    "Tunnel fallback is unavailable; using an interface-up direct path despite missing server heartbeats.",
                    None,
                );
            }
            return EgressDecision {
                target: EgressTarget::None,
                reason: "Neither the tunnel nor a physical path is usable.".to_string(),
                failed_path_id: None,
            };
        }

        let current = active_direct_path(&self.active, input.paths);
        if let Some(current) = current {
            if path_hard_failed(current) || active_silent_failure(current) {
                if let Some(replacement) = clean.or_else(|| {
                    best_proven_path(input.paths, input.preferred_path_id, Some(current.path_id))
                }) {
                    return direct_target(
                        replacement,
                        "Active direct path failed; switched to a proven replacement.",
                        Some(current.path_id),
                    );
                }
                if current.interface_up {
                    return direct_target(
                        current,
                        "The active path is unhealthy, but no fresh direct replacement is proven; retaining its interface-up physical route.",
                        None,
                    );
                }
                if input.tunnel_usable {
                    return EgressDecision {
                        target: EgressTarget::Tunnel,
                        reason: "No proven direct replacement exists; Reliable tunnel fallback activated.".to_string(),
                        failed_path_id: Some(current.path_id),
                    };
                }
            }
        }

        if clean.is_some() {
            self.degraded_rounds = 0;
            self.clean_since = None;
            let current = current.filter(|path| !path_hard_failed(path));
            if input.scheduler_round {
                if let Some(preferred) = clean {
                    if current.is_none_or(|path| path.path_id != preferred.path_id) {
                        return direct_target(
                            preferred,
                            "Selected the best clean direct path.",
                            None,
                        );
                    }
                }
            }
            if let Some(current) = current {
                return direct_target(current, "A clean direct path remains active.", None);
            }
            return direct_target(
                clean.expect("clean path checked above"),
                "Selected a clean direct path.",
                None,
            );
        }

        if input.paths.iter().any(path_quality_is_warming_up) {
            self.degraded_rounds = 0;
            if let Some(current) = current.filter(|path| !path_hard_failed(path)) {
                return direct_target(
                    current,
                    "Direct path quality is warming up; remaining direct until enough samples exist.",
                    None,
                );
            }
            if let Some(path) = usable {
                return direct_target(
                    path,
                    "Selected a reachable direct path while quality samples warm up.",
                    None,
                );
            }
        }

        if input.scheduler_round {
            self.degraded_rounds = self.degraded_rounds.saturating_add(1);
        }
        if self.degraded_rounds >= self.thresholds.adaptive_enter_rounds && input.tunnel_usable {
            return EgressDecision {
                target: EgressTarget::Tunnel,
                reason: "All direct paths stayed degraded for two scheduler rounds; Reliable tunnel fallback activated.".to_string(),
                failed_path_id: current.filter(|path| path_hard_failed(path)).map(|path| path.path_id),
            };
        }

        if let Some(current) = current.filter(|path| !path_hard_failed(path)) {
            return direct_target(
                current,
                "Direct paths are degraded; waiting for confirmation before tunnel fallback.",
                None,
            );
        }
        if let Some(path) = usable {
            return direct_target(
                path,
                "Using the best reachable direct path while the tunnel is unavailable or pending.",
                None,
            );
        }
        if !input.tunnel_usable {
            if let Some(path) = best_emergency_direct_path(input.paths, input.preferred_path_id) {
                return direct_target(
                    path,
                    "Tunnel is unavailable; using an interface-up direct path despite missing server heartbeats.",
                    None,
                );
            }
        }
        EgressDecision {
            target: EgressTarget::None,
            reason: "No usable egress is available.".to_string(),
            failed_path_id: None,
        }
    }
}

fn direct_target(
    path: &PathHealthSnapshot,
    reason: &str,
    failed_path_id: Option<u16>,
) -> EgressDecision {
    EgressDecision {
        target: EgressTarget::Direct {
            path_id: path.path_id,
            interface_name: path.interface_name.clone().unwrap_or_default(),
        },
        reason: reason.to_string(),
        failed_path_id,
    }
}

fn active_direct_path<'a>(
    target: &EgressTarget,
    paths: &'a [PathHealthSnapshot],
) -> Option<&'a PathHealthSnapshot> {
    let EgressTarget::Direct { path_id, .. } = target else {
        return None;
    };
    paths.iter().find(|path| path.path_id == *path_id)
}

fn path_hard_failed(path: &PathHealthSnapshot) -> bool {
    !path.interface_up || path.in_cooldown || path.send_failure_streak >= 2 || path.heartbeat_failed
}

fn heartbeat_only_failure(path: &PathHealthSnapshot) -> bool {
    path.interface_up
        && path.send_failure_streak < 2
        && (path.heartbeat_failed || active_silent_failure(path))
}

fn active_silent_failure(path: &PathHealthSnapshot) -> bool {
    path.stale_ack_ms
        .is_some_and(|age| age >= ACTIVE_SILENT_FAILURE_MS)
}

fn path_proven(path: &PathHealthSnapshot) -> bool {
    !path_hard_failed(path)
        && path
            .interface_name
            .as_ref()
            .is_some_and(|name| !name.trim().is_empty())
        && path
            .stale_ack_ms
            .is_some_and(|age| age <= REPLACEMENT_FRESH_MS)
        && path.heartbeat_consecutive_successes >= REPLACEMENT_SUCCESS_COUNT
}

fn path_clean(path: &PathHealthSnapshot, recovery: RecoveryConfig) -> bool {
    path_proven(path)
        && !path.heartbeat_warming_up
        && path.rtt_ms.is_some_and(|rtt| rtt <= recovery.clean_rtt_ms)
        && path.loss_rate <= recovery.clean_loss_threshold
        && path.late_rate <= recovery.clean_late_threshold
        && path.jitter_ms.unwrap_or_default() <= recovery.clean_jitter_ms
        && path
            .stale_ack_ms
            .is_some_and(|age| age <= recovery.clean_stale_ack_ms)
        && path.queue_pressure <= recovery.clean_queue_pressure
}

fn path_quality_is_warming_up(path: &PathHealthSnapshot) -> bool {
    path_proven(path) && path.heartbeat_warming_up
}

fn preferred_proven_path(
    paths: &[PathHealthSnapshot],
    preferred: Option<u16>,
) -> Option<&PathHealthSnapshot> {
    preferred.and_then(|id| {
        paths
            .iter()
            .find(|path| path.path_id == id && path_proven(path))
    })
}

fn ranked<'a>(
    paths: &'a [PathHealthSnapshot],
    preferred: Option<u16>,
    predicate: impl Fn(&PathHealthSnapshot) -> bool,
) -> Option<&'a PathHealthSnapshot> {
    preferred
        .and_then(|id| {
            paths
                .iter()
                .find(|path| path.path_id == id && predicate(path))
        })
        .or_else(|| {
            paths
                .iter()
                .filter(|path| predicate(path))
                .max_by(|left, right| left.score().total_cmp(&right.score()))
        })
}

fn best_proven_path(
    paths: &[PathHealthSnapshot],
    preferred: Option<u16>,
    exclude: Option<u16>,
) -> Option<&PathHealthSnapshot> {
    ranked(paths, preferred.filter(|id| Some(*id) != exclude), |path| {
        Some(path.path_id) != exclude && path_proven(path)
    })
}

fn best_clean_path(
    paths: &[PathHealthSnapshot],
    preferred: Option<u16>,
    recovery: RecoveryConfig,
) -> Option<&PathHealthSnapshot> {
    ranked(paths, preferred, |path| path_clean(path, recovery))
}

fn best_usable_path(
    paths: &[PathHealthSnapshot],
    preferred: Option<u16>,
) -> Option<&PathHealthSnapshot> {
    ranked(paths, preferred, |path| {
        !path_hard_failed(path)
            && path
                .interface_name
                .as_ref()
                .is_some_and(|name| !name.trim().is_empty())
    })
}

fn best_emergency_direct_path(
    paths: &[PathHealthSnapshot],
    preferred: Option<u16>,
) -> Option<&PathHealthSnapshot> {
    ranked(paths, preferred, |path| {
        path.interface_up
            && path
                .interface_name
                .as_ref()
                .is_some_and(|name| !name.trim().is_empty())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn path(id: u16, rtt: f64) -> PathHealthSnapshot {
        PathHealthSnapshot {
            path_id: id,
            name: format!("wan{id}"),
            interface_name: Some(format!("wan{id}")),
            rtt_ms: Some(rtt),
            jitter_ms: Some(2.0),
            loss_rate: 0.0,
            late_rate: 0.0,
            queue_depth: 0,
            outbound_throughput_bps: 0,
            inbound_throughput_bps: 0,
            duplicate_inbound_throughput_bps: 0,
            raw_inbound_throughput_bps: 0,
            throughput_bps: 0,
            interface_up: true,
            in_cooldown: false,
            send_failure_streak: 0,
            stale_ack_ms: Some(100),
            queue_pressure: 0.0,
            duplicate_usefulness: 1.0,
            throughput_collapse_score: 0.0,
            demotion_reason: None,
            role_reason: None,
            socket_generation: 1,
            socket_ifindex: Some(u32::from(id)),
            socket_bind_addr: None,
            last_socket_error: None,
            last_rebind_reason: None,
            last_rebind_error: None,
            last_rebind_at_micros: None,
            rebind_count: 0,
            heartbeat_sent: 20,
            heartbeat_acked: 20,
            heartbeat_expired: 0,
            heartbeat_late_acks: 0,
            heartbeat_rebind_discarded: 0,
            pending_probes: 2,
            heartbeat_sample_count: 20,
            heartbeat_consecutive_misses: 0,
            heartbeat_consecutive_successes: 20,
            heartbeat_warming_up: false,
            heartbeat_failed: false,
        }
    }

    fn input<'a>(
        mode: TrafficMode,
        paths: &'a [PathHealthSnapshot],
        now_ms: u64,
    ) -> EgressEvaluation<'a> {
        EgressEvaluation {
            mode,
            paths,
            preferred_path_id: paths.first().map(|path| path.path_id),
            tunnel_usable: true,
            scheduler_round: true,
            now_ms,
            recovery: ClientConfig::default().recovery_config(),
        }
    }

    use crate::ClientConfig;

    #[test]
    fn direct_mode_selects_preferred_proven_path() {
        let paths = vec![path(1, 40.0), path(2, 20.0)];
        let mut controller =
            EgressController::new(TrafficMode::DirectFailover, EgressThresholds::default());
        let decision = controller
            .evaluate(input(TrafficMode::DirectFailover, &paths, 0))
            .unwrap();
        assert!(matches!(
            decision.target,
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }

    #[test]
    fn heartbeat_failure_switches_only_to_a_proven_replacement() {
        let mut failed = path(1, 40.0);
        let replacement = path(2, 50.0);
        let mut controller =
            EgressController::new(TrafficMode::DirectFailover, EgressThresholds::default());
        controller.evaluate(input(
            TrafficMode::DirectFailover,
            &[failed.clone(), replacement.clone()],
            0,
        ));
        failed.heartbeat_failed = true;
        failed.heartbeat_consecutive_misses = 4;
        let paths = vec![failed, replacement];
        let decision = controller
            .evaluate(input(TrafficMode::DirectFailover, &paths, 800))
            .unwrap();
        assert!(matches!(
            decision.target,
            EgressTarget::Direct { path_id: 2, .. }
        ));
        assert_eq!(decision.failed_path_id, Some(1));
    }

    #[test]
    fn four_missed_opportunities_switch_before_probe_timeout_expires() {
        let mut active = path(1, 40.0);
        let replacement = path(2, 50.0);
        let mut controller =
            EgressController::new(TrafficMode::DirectFailover, EgressThresholds::default());
        controller.evaluate(input(
            TrafficMode::DirectFailover,
            &[active.clone(), replacement.clone()],
            0,
        ));
        active.stale_ack_ms = Some(800);
        active.heartbeat_consecutive_misses = 0;
        active.heartbeat_failed = false;
        let paths = vec![active, replacement];

        let decision = controller
            .evaluate(input(TrafficMode::DirectFailover, &paths, 800))
            .unwrap();

        assert!(matches!(
            decision.target,
            EgressTarget::Direct { path_id: 2, .. }
        ));
        assert_eq!(decision.failed_path_id, Some(1));
    }

    #[test]
    fn active_path_is_not_silently_failed_before_the_stale_threshold() {
        let mut active = path(1, 40.0);
        let replacement = path(2, 50.0);
        let mut controller =
            EgressController::new(TrafficMode::DirectFailover, EgressThresholds::default());
        controller.evaluate(input(
            TrafficMode::DirectFailover,
            &[active.clone(), replacement.clone()],
            0,
        ));
        active.stale_ack_ms = Some(ACTIVE_SILENT_FAILURE_MS - 1);

        assert!(controller
            .evaluate(input(
                TrafficMode::DirectFailover,
                &[active, replacement],
                ACTIVE_SILENT_FAILURE_MS - 1,
            ))
            .is_none());
    }

    #[test]
    fn isolated_misses_do_not_demote_active_direct_path() {
        let mut active = path(1, 40.0);
        let replacement = path(2, 50.0);
        let mut controller =
            EgressController::new(TrafficMode::DirectFailover, EgressThresholds::default());
        controller.evaluate(input(
            TrafficMode::DirectFailover,
            &[active.clone(), replacement.clone()],
            0,
        ));
        active.heartbeat_consecutive_misses = 3;
        active.stale_ack_ms = Some(700);
        let paths = vec![active, replacement];
        assert!(controller
            .evaluate(input(TrafficMode::DirectFailover, &paths, 600))
            .is_none());
        assert!(matches!(
            controller.active(),
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }

    #[test]
    fn adaptive_enters_tunnel_after_two_degraded_rounds() {
        let mut degraded = path(1, 250.0);
        degraded.loss_rate = 0.10;
        let paths = vec![degraded];
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());
        controller.evaluate(input(TrafficMode::Adaptive, &paths, 0));
        let decision = controller
            .evaluate(input(TrafficMode::Adaptive, &paths, 1_000))
            .unwrap();
        assert_eq!(decision.target, EgressTarget::Tunnel);
    }

    #[test]
    fn adaptive_does_not_treat_warmup_as_degradation() {
        let mut warming = path(1, 40.0);
        warming.heartbeat_warming_up = true;
        warming.heartbeat_sample_count = 10;
        let paths = vec![warming];
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());

        controller.evaluate(input(TrafficMode::Adaptive, &paths, 0));
        assert!(controller
            .evaluate(input(TrafficMode::Adaptive, &paths, 1_000))
            .is_none());
        assert!(controller
            .evaluate(input(TrafficMode::Adaptive, &paths, 2_000))
            .is_none());
        assert!(matches!(
            controller.active(),
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }

    #[test]
    fn adaptive_returns_after_thirty_clean_seconds() {
        let mut degraded = path(1, 250.0);
        degraded.loss_rate = 0.10;
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());
        controller.evaluate(input(TrafficMode::Adaptive, &[degraded.clone()], 0));
        controller.evaluate(input(TrafficMode::Adaptive, &[degraded.clone()], 1_000));
        controller.evaluate(input(TrafficMode::Adaptive, &[degraded], 2_000));
        let clean = vec![path(1, 40.0)];
        assert!(controller
            .evaluate(input(TrafficMode::Adaptive, &clean, 3_000))
            .is_none());
        let decision = controller
            .evaluate(input(TrafficMode::Adaptive, &clean, 33_000))
            .unwrap();
        assert!(matches!(
            decision.target,
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }

    #[test]
    fn adaptive_clean_timer_restarts_when_the_clean_path_changes() {
        let mut degraded = path(1, 250.0);
        degraded.loss_rate = 0.10;
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());
        controller.evaluate(input(TrafficMode::Adaptive, &[degraded.clone()], 0));
        controller.evaluate(input(TrafficMode::Adaptive, &[degraded], 1_000));

        let clean_one = vec![path(1, 40.0)];
        controller.evaluate(input(TrafficMode::Adaptive, &clean_one, 2_000));
        let clean_two = vec![path(2, 35.0)];
        assert!(controller
            .evaluate(input(TrafficMode::Adaptive, &clean_two, 31_000))
            .is_none());
        assert!(matches!(controller.active(), EgressTarget::Tunnel));
        assert!(controller
            .evaluate(input(TrafficMode::Adaptive, &clean_two, 61_000))
            .is_some());
    }

    #[test]
    fn adaptive_uses_degraded_direct_when_tunnel_is_unavailable() {
        let mut degraded = path(1, 250.0);
        degraded.loss_rate = 0.10;
        let paths = vec![degraded];
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());
        let mut evaluation = input(TrafficMode::Adaptive, &paths, 0);
        evaluation.tunnel_usable = false;
        let decision = controller.evaluate(evaluation).unwrap();
        assert!(matches!(
            decision.target,
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }

    #[test]
    fn adaptive_uses_interface_up_path_when_server_heartbeats_are_unavailable() {
        let mut server_unreachable = path(1, 40.0);
        server_unreachable.heartbeat_failed = true;
        server_unreachable.stale_ack_ms = Some(6_000);
        server_unreachable.heartbeat_consecutive_successes = 0;
        let paths = vec![server_unreachable];
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());
        let mut evaluation = input(TrafficMode::Adaptive, &paths, 0);
        evaluation.tunnel_usable = false;

        let decision = controller.evaluate(evaluation).unwrap();

        assert!(matches!(
            decision.target,
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }

    #[test]
    fn adaptive_keeps_current_direct_route_during_server_only_heartbeat_failure() {
        let healthy = vec![path(1, 40.0)];
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());
        controller.evaluate(input(TrafficMode::Adaptive, &healthy, 0));

        let mut server_unreachable = path(1, 40.0);
        server_unreachable.heartbeat_failed = true;
        server_unreachable.stale_ack_ms = Some(6_000);
        server_unreachable.heartbeat_consecutive_successes = 0;
        let paths = vec![server_unreachable];

        assert!(controller
            .evaluate(input(TrafficMode::Adaptive, &paths, 1_000))
            .is_none());
        assert!(matches!(
            controller.active(),
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }

    #[test]
    fn adaptive_keeps_interface_up_direct_route_during_server_socket_failure() {
        let healthy = vec![path(1, 40.0)];
        let mut controller =
            EgressController::new(TrafficMode::Adaptive, EgressThresholds::default());
        controller.evaluate(input(TrafficMode::Adaptive, &healthy, 0));

        let mut server_refused = path(1, 40.0);
        server_refused.send_failure_streak = 2;
        server_refused.in_cooldown = true;
        let paths = vec![server_refused];

        assert!(controller
            .evaluate(input(TrafficMode::Adaptive, &paths, 1_000))
            .is_none());
        assert!(matches!(
            controller.active(),
            EgressTarget::Direct { path_id: 1, .. }
        ));
    }
}
