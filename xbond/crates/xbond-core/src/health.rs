use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PathHealthSnapshot {
    pub path_id: u16,
    pub name: String,
    pub interface_name: Option<String>,
    pub rtt_ms: Option<f64>,
    pub jitter_ms: Option<f64>,
    pub loss_rate: f64,
    pub late_rate: f64,
    pub queue_depth: u32,
    #[serde(default)]
    pub outbound_throughput_bps: u64,
    #[serde(default)]
    pub inbound_throughput_bps: u64,
    #[serde(default)]
    pub duplicate_inbound_throughput_bps: u64,
    #[serde(default)]
    pub raw_inbound_throughput_bps: u64,
    pub throughput_bps: u64,
    pub interface_up: bool,
    pub in_cooldown: bool,
    #[serde(default)]
    pub send_failure_streak: u32,
    #[serde(default)]
    pub stale_ack_ms: Option<u64>,
    #[serde(default)]
    pub queue_pressure: f64,
    #[serde(default)]
    pub duplicate_usefulness: f64,
    #[serde(default)]
    pub throughput_collapse_score: f64,
    #[serde(default)]
    pub demotion_reason: Option<String>,
    #[serde(default)]
    pub role_reason: Option<String>,
    #[serde(default)]
    pub socket_generation: u64,
    #[serde(default)]
    pub socket_ifindex: Option<u32>,
    #[serde(default)]
    pub socket_bind_addr: Option<String>,
    #[serde(default)]
    pub last_socket_error: Option<String>,
    #[serde(default)]
    pub last_rebind_reason: Option<String>,
    #[serde(default)]
    pub last_rebind_error: Option<String>,
    #[serde(default)]
    pub last_rebind_at_micros: Option<u64>,
    #[serde(default)]
    pub rebind_count: u64,
    #[serde(default)]
    pub heartbeat_sent: u64,
    #[serde(default)]
    pub heartbeat_acked: u64,
    #[serde(default)]
    pub heartbeat_expired: u64,
    #[serde(default)]
    pub heartbeat_late_acks: u64,
    #[serde(default)]
    pub heartbeat_rebind_discarded: u64,
    #[serde(default)]
    pub pending_probes: usize,
    #[serde(default)]
    pub heartbeat_sample_count: usize,
    #[serde(default)]
    pub heartbeat_consecutive_misses: u32,
    #[serde(default)]
    pub heartbeat_consecutive_successes: u32,
    #[serde(default)]
    pub heartbeat_warming_up: bool,
    #[serde(default)]
    pub heartbeat_failed: bool,
}

impl PathHealthSnapshot {
    pub fn is_realtime_eligible(&self) -> bool {
        self.hard_demotion_reason().is_none()
    }

    pub fn hard_demotion_reason(&self) -> Option<&'static str> {
        if !self.interface_up {
            return Some("No carrier or interface is down.");
        }

        if self.in_cooldown {
            return Some("Path is in cooldown after recent failures.");
        }

        if self.heartbeat_failed {
            return Some("Path heartbeat failed after consecutive probe expirations.");
        }

        if self.send_failure_streak >= 2 {
            return Some("Repeated socket send failures.");
        }

        if self.stale_ack_ms.is_some_and(|age| age >= 5_000) {
            return Some("Heartbeat ACKs are stale.");
        }

        if self.queue_pressure >= 1.0 {
            return Some("Path send queue is saturated.");
        }

        None
    }

    pub fn score(&self) -> f64 {
        if !self.is_realtime_eligible() {
            return -1_000_000.0;
        }

        let rtt_penalty = self.rtt_ms.unwrap_or(500.0).min(2_000.0) * 2.0;
        let jitter_penalty = self.jitter_ms.unwrap_or(100.0).min(1_000.0) * 2.5;
        let loss_rate = if self.heartbeat_warming_up && !self.heartbeat_failed {
            0.0
        } else {
            self.loss_rate
        };
        let loss_penalty = loss_rate.clamp(0.0, 1.0) * 800.0;
        let late_penalty = self.late_rate.clamp(0.0, 1.0) * 1_000.0;
        let queue_penalty = f64::from(self.queue_depth.min(10_000)) * 0.1;
        let queue_pressure_penalty = self.queue_pressure.clamp(0.0, 1.0) * 400.0;
        let stale_ack_penalty = self
            .stale_ack_ms
            .map(|value| value.min(5_000) as f64 * 0.05)
            .unwrap_or_default();
        let send_failure_penalty = f64::from(self.send_failure_streak.min(10)) * 150.0;
        let duplicate_help_penalty =
            if self.duplicate_usefulness <= 0.05 && self.raw_inbound_throughput_bps > 250_000 {
                120.0
            } else {
                0.0
            };
        let collapse_penalty = self.throughput_collapse_score.clamp(0.0, 1.0) * 500.0;
        let throughput_bonus = if self.throughput_bps == 0 {
            0.0
        } else {
            (self.throughput_bps as f64).log10().min(9.0) * 10.0
        };

        1_000.0
            - rtt_penalty
            - jitter_penalty
            - loss_penalty
            - late_penalty
            - queue_penalty
            - queue_pressure_penalty
            - stale_ack_penalty
            - send_failure_penalty
            - duplicate_help_penalty
            - collapse_penalty
            + throughput_bonus
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PathRole {
    Anchor,
    Backup,
    Probe,
    Cooldown,
    Unavailable,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScoredPath {
    pub path: PathHealthSnapshot,
    pub score: f64,
    pub role: PathRole,
}

pub fn select_path_roles(paths: &[PathHealthSnapshot], max_backups: usize) -> Vec<ScoredPath> {
    let mut scored = score_paths(paths);
    assign_best_available_roles(&mut scored, max_backups);
    scored
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct RoleSelectionState {
    pub anchor_path_id: Option<u16>,
    pub backup_path_ids: Vec<u16>,
    pub anchor_candidate_path_id: Option<u16>,
    pub anchor_candidate_ticks: u8,
    pub backup_candidate_path_ids: Vec<u16>,
    pub backup_candidate_ticks: u8,
    pub schedule_change_count: u64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RoleSelectionConfig {
    pub anchor_switch_score_margin: f64,
    pub backup_switch_score_margin: f64,
    pub stable_ticks_required: u8,
}

impl Default for RoleSelectionConfig {
    fn default() -> Self {
        Self {
            anchor_switch_score_margin: 150.0,
            backup_switch_score_margin: 100.0,
            stable_ticks_required: 5,
        }
    }
}

pub fn select_path_roles_with_state(
    paths: &[PathHealthSnapshot],
    max_backups: usize,
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
) -> Vec<ScoredPath> {
    let mut scored = score_paths(paths);
    let previous_anchor = state.anchor_path_id;
    let previous_backups = state.backup_path_ids.clone();
    let best_anchor_id = scored
        .iter()
        .find(|path| is_role_eligible(path))
        .map(path_id);
    let anchor_id = choose_anchor(&scored, state, best_anchor_id, config);
    let backup_ids = choose_backups(&scored, state, anchor_id, max_backups, config);

    assign_hysteresis_roles(&mut scored, anchor_id, &backup_ids);

    if previous_anchor != anchor_id || previous_backups != backup_ids {
        state.schedule_change_count = state.schedule_change_count.saturating_add(1);
    }
    state.anchor_path_id = anchor_id;
    state.backup_path_ids = backup_ids;

    scored
}

fn score_paths(paths: &[PathHealthSnapshot]) -> Vec<ScoredPath> {
    let mut scored: Vec<ScoredPath> = paths
        .iter()
        .cloned()
        .map(|mut path| {
            path.demotion_reason = path
                .hard_demotion_reason()
                .map(std::string::ToString::to_string);
            let score = path.score();
            let role = if !path.interface_up {
                PathRole::Unavailable
            } else if path.demotion_reason.is_some() {
                PathRole::Cooldown
            } else {
                PathRole::Probe
            };
            ScoredPath { path, score, role }
        })
        .collect();

    scored.sort_by(|a, b| b.score.total_cmp(&a.score));
    scored
}

fn assign_best_available_roles(scored: &mut [ScoredPath], max_backups: usize) {
    let mut anchor_assigned = false;
    let mut backups_assigned = 0usize;
    for scored_path in scored {
        if !is_role_eligible(scored_path) {
            if scored_path.path.role_reason.is_none() {
                scored_path.path.role_reason = scored_path.path.demotion_reason.clone();
            }
            continue;
        }

        if !anchor_assigned {
            scored_path.role = PathRole::Anchor;
            scored_path.path.role_reason = Some("Best currently healthy path.".to_string());
            anchor_assigned = true;
        } else if backups_assigned < max_backups && scored_path.score > -100_000.0 {
            scored_path.role = PathRole::Backup;
            scored_path.path.role_reason =
                Some("Healthy backup selected for redundancy.".to_string());
            backups_assigned += 1;
        } else {
            scored_path.path.role_reason =
                Some("Healthy but currently kept as probe/standby.".to_string());
        }
    }
}

fn choose_anchor(
    scored: &[ScoredPath],
    state: &mut RoleSelectionState,
    best_anchor_id: Option<u16>,
    config: RoleSelectionConfig,
) -> Option<u16> {
    let Some(best_anchor_id) = best_anchor_id else {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return None;
    };

    let Some(current_anchor_id) = state.anchor_path_id else {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(best_anchor_id);
    };

    let Some(current_anchor) = scored
        .iter()
        .find(|path| path.path.path_id == current_anchor_id && is_role_eligible(path))
    else {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(best_anchor_id);
    };

    if current_anchor_id == best_anchor_id {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    }

    let best_score = score_for(scored, best_anchor_id).unwrap_or(f64::NEG_INFINITY);
    if best_score - current_anchor.score < config.anchor_switch_score_margin {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    }

    if state.anchor_candidate_path_id == Some(best_anchor_id) {
        state.anchor_candidate_ticks = state.anchor_candidate_ticks.saturating_add(1);
    } else {
        state.anchor_candidate_path_id = Some(best_anchor_id);
        state.anchor_candidate_ticks = 1;
    }

    if state.anchor_candidate_ticks >= config.stable_ticks_required {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        Some(best_anchor_id)
    } else {
        Some(current_anchor_id)
    }
}

fn choose_backups(
    scored: &[ScoredPath],
    state: &mut RoleSelectionState,
    anchor_id: Option<u16>,
    max_backups: usize,
    config: RoleSelectionConfig,
) -> Vec<u16> {
    if max_backups == 0 {
        state.backup_candidate_path_ids.clear();
        state.backup_candidate_ticks = 0;
        return Vec::new();
    }

    let target = scored
        .iter()
        .filter(|path| is_role_eligible(path) && Some(path.path.path_id) != anchor_id)
        .take(max_backups)
        .map(path_id)
        .collect::<Vec<_>>();

    let current = state
        .backup_path_ids
        .iter()
        .copied()
        .filter(|id| {
            Some(*id) != anchor_id
                && scored
                    .iter()
                    .any(|path| path.path.path_id == *id && is_role_eligible(path))
        })
        .take(max_backups)
        .collect::<Vec<_>>();

    if current.len() < max_backups {
        state.backup_candidate_path_ids.clear();
        state.backup_candidate_ticks = 0;
        return fill_backup_slots(scored, anchor_id, current, max_backups);
    }

    if current == target {
        state.backup_candidate_path_ids.clear();
        state.backup_candidate_ticks = 0;
        return current;
    }

    let weakest_current_score = current
        .iter()
        .filter_map(|id| score_for(scored, *id))
        .min_by(f64::total_cmp)
        .unwrap_or(f64::NEG_INFINITY);
    let best_new_score = target
        .iter()
        .filter(|id| !current.contains(id))
        .filter_map(|id| score_for(scored, *id))
        .max_by(f64::total_cmp)
        .unwrap_or(f64::NEG_INFINITY);

    if best_new_score - weakest_current_score < config.backup_switch_score_margin {
        state.backup_candidate_path_ids.clear();
        state.backup_candidate_ticks = 0;
        return current;
    }

    if state.backup_candidate_path_ids == target {
        state.backup_candidate_ticks = state.backup_candidate_ticks.saturating_add(1);
    } else {
        state.backup_candidate_path_ids = target.clone();
        state.backup_candidate_ticks = 1;
    }

    if state.backup_candidate_ticks >= config.stable_ticks_required {
        state.backup_candidate_path_ids.clear();
        state.backup_candidate_ticks = 0;
        target
    } else {
        current
    }
}

fn fill_backup_slots(
    scored: &[ScoredPath],
    anchor_id: Option<u16>,
    mut current: Vec<u16>,
    max_backups: usize,
) -> Vec<u16> {
    for path in scored {
        let id = path.path.path_id;
        if current.len() >= max_backups {
            break;
        }
        if Some(id) == anchor_id || current.contains(&id) || !is_role_eligible(path) {
            continue;
        }
        current.push(id);
    }
    current
}

fn assign_hysteresis_roles(scored: &mut [ScoredPath], anchor_id: Option<u16>, backup_ids: &[u16]) {
    for scored_path in scored {
        if !is_role_eligible(scored_path) {
            if scored_path.path.role_reason.is_none() {
                scored_path.path.role_reason = scored_path.path.demotion_reason.clone();
            }
            continue;
        }

        if Some(scored_path.path.path_id) == anchor_id {
            scored_path.role = PathRole::Anchor;
            scored_path.path.role_reason =
                Some("Selected as stable anchor by hysteresis scheduler.".to_string());
        } else if backup_ids.contains(&scored_path.path.path_id) {
            scored_path.role = PathRole::Backup;
            scored_path.path.role_reason = Some("Selected as stable redundant backup.".to_string());
        } else {
            scored_path.role = PathRole::Probe;
            scored_path.path.role_reason =
                Some("Healthy but currently kept as probe/standby.".to_string());
        }
    }
}

fn path_id(path: &ScoredPath) -> u16 {
    path.path.path_id
}

fn score_for(scored: &[ScoredPath], path_id: u16) -> Option<f64> {
    scored
        .iter()
        .find(|path| path.path.path_id == path_id)
        .map(|path| path.score)
}

fn is_role_eligible(path: &ScoredPath) -> bool {
    path.score.is_finite() && path.path.is_realtime_eligible()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn path(
        path_id: u16,
        name: &str,
        rtt_ms: f64,
        loss_rate: f64,
        late_rate: f64,
    ) -> PathHealthSnapshot {
        PathHealthSnapshot {
            path_id,
            name: name.to_string(),
            interface_name: Some(format!("wan{path_id}")),
            rtt_ms: Some(rtt_ms),
            jitter_ms: Some(5.0),
            loss_rate,
            late_rate,
            queue_depth: 0,
            outbound_throughput_bps: 5_000_000,
            inbound_throughput_bps: 5_000_000,
            duplicate_inbound_throughput_bps: 0,
            raw_inbound_throughput_bps: 5_000_000,
            throughput_bps: 10_000_000,
            interface_up: true,
            in_cooldown: false,
            send_failure_streak: 0,
            stale_ack_ms: None,
            queue_pressure: 0.0,
            duplicate_usefulness: 1.0,
            throughput_collapse_score: 0.0,
            demotion_reason: None,
            role_reason: None,
            socket_generation: 0,
            socket_ifindex: None,
            socket_bind_addr: None,
            last_socket_error: None,
            last_rebind_reason: None,
            last_rebind_error: None,
            last_rebind_at_micros: None,
            rebind_count: 0,
            heartbeat_sent: 0,
            heartbeat_acked: 0,
            heartbeat_expired: 0,
            heartbeat_late_acks: 0,
            heartbeat_rebind_discarded: 0,
            pending_probes: 0,
            heartbeat_sample_count: 0,
            heartbeat_consecutive_misses: 0,
            heartbeat_consecutive_successes: 0,
            heartbeat_warming_up: false,
            heartbeat_failed: false,
        }
    }

    #[test]
    fn healthiest_path_becomes_anchor() {
        let roles = select_path_roles(
            &[
                path(1, "fiber", 15.0, 0.0, 0.0),
                path(2, "bad-5g", 700.0, 0.2, 0.4),
                path(3, "backup", 80.0, 0.01, 0.02),
            ],
            1,
        );

        assert_eq!(roles[0].path.name, "fiber");
        assert_eq!(roles[0].role, PathRole::Anchor);
        assert_eq!(roles[1].path.name, "backup");
        assert_eq!(roles[1].role, PathRole::Backup);
        assert_ne!(
            roles
                .iter()
                .find(|path| path.path.name == "bad-5g")
                .unwrap()
                .role,
            PathRole::Anchor
        );
    }

    #[test]
    fn cooldown_path_cannot_be_anchor() {
        let mut stable = path(1, "fiber", 15.0, 0.0, 0.0);
        stable.in_cooldown = true;
        let roles = select_path_roles(&[stable, path(2, "cell", 80.0, 0.0, 0.0)], 1);

        assert_eq!(roles[0].path.name, "cell");
        assert_eq!(roles[0].role, PathRole::Anchor);
        assert_eq!(
            roles
                .iter()
                .find(|path| path.path.name == "fiber")
                .unwrap()
                .role,
            PathRole::Cooldown
        );
    }

    #[test]
    fn all_bad_paths_do_not_get_anchor() {
        let mut offline = path(1, "offline", 15.0, 0.0, 0.0);
        offline.interface_up = false;
        let mut full_loss = path(2, "heartbeat-failed", 40.0, 1.0, 0.0);
        full_loss.heartbeat_failed = true;

        let roles = select_path_roles(&[offline, full_loss], 1);

        assert!(roles.iter().all(|path| path.role != PathRole::Anchor));
        assert!(roles.iter().all(|path| path.role != PathRole::Backup));
    }

    #[test]
    fn heartbeat_failed_path_cannot_be_anchor() {
        let mut failed = path(1, "heartbeat-failed", 15.0, 1.0, 0.0);
        failed.heartbeat_failed = true;
        let roles = select_path_roles(&[failed, path(2, "stable", 50.0, 0.0, 0.0)], 1);

        assert_eq!(roles[0].path.name, "stable");
        assert_eq!(roles[0].role, PathRole::Anchor);
        assert_eq!(
            roles
                .iter()
                .find(|path| path.path.name == "heartbeat-failed")
                .unwrap()
                .role,
            PathRole::Cooldown
        );
    }

    #[test]
    fn warmup_loss_does_not_hard_demote_path() {
        let mut warming = path(1, "warming", 15.0, 1.0, 0.0);
        warming.heartbeat_warming_up = true;
        warming.heartbeat_sample_count = 3;
        let roles = select_path_roles(&[warming, path(2, "stable", 50.0, 0.0, 0.0)], 1);

        let warming = roles
            .iter()
            .find(|path| path.path.name == "warming")
            .unwrap();
        assert_ne!(warming.role, PathRole::Cooldown);
        assert!(warming.path.demotion_reason.is_none());
    }

    #[test]
    fn stale_ack_path_cannot_be_anchor() {
        let mut stale = path(1, "stale", 15.0, 0.0, 0.0);
        stale.stale_ack_ms = Some(6_000);
        let roles = select_path_roles(&[stale, path(2, "stable", 50.0, 0.0, 0.0)], 1);

        assert_eq!(roles[0].path.name, "stable");
        assert_eq!(roles[0].role, PathRole::Anchor);
        let stale = roles.iter().find(|path| path.path.name == "stale").unwrap();
        assert_eq!(stale.role, PathRole::Cooldown);
        assert!(stale
            .path
            .demotion_reason
            .as_deref()
            .is_some_and(|reason| reason.contains("stale")));
    }

    #[test]
    fn repeated_send_failures_cannot_be_anchor() {
        let mut failing = path(1, "failing", 15.0, 0.0, 0.0);
        failing.send_failure_streak = 2;
        let roles = select_path_roles(&[failing, path(2, "stable", 50.0, 0.0, 0.0)], 1);

        assert_eq!(roles[0].path.name, "stable");
        assert_eq!(roles[0].role, PathRole::Anchor);
        assert_eq!(
            roles
                .iter()
                .find(|path| path.path.name == "failing")
                .unwrap()
                .role,
            PathRole::Cooldown
        );
    }

    #[test]
    fn unavailable_scores_are_finite_for_json_status() {
        let mut offline = path(1, "offline", 15.0, 0.0, 0.0);
        offline.interface_up = false;

        assert!(offline.score().is_finite());
    }

    #[test]
    fn hysteresis_holds_anchor_until_candidate_is_stable() {
        let mut state = RoleSelectionState {
            anchor_path_id: Some(1),
            backup_path_ids: vec![3],
            ..RoleSelectionState::default()
        };
        let config = RoleSelectionConfig::default();

        for _ in 0..config.stable_ticks_required.saturating_sub(1) {
            let roles = select_path_roles_with_state(
                &[
                    path(1, "current", 100.0, 0.0, 0.0),
                    path(2, "better", 10.0, 0.0, 0.0),
                    path(3, "backup", 90.0, 0.0, 0.0),
                ],
                1,
                &mut state,
                config,
            );

            assert_eq!(
                roles
                    .iter()
                    .find(|path| path.role == PathRole::Anchor)
                    .unwrap()
                    .path
                    .path_id,
                1
            );
        }

        let roles = select_path_roles_with_state(
            &[
                path(1, "current", 100.0, 0.0, 0.0),
                path(2, "better", 10.0, 0.0, 0.0),
                path(3, "backup", 90.0, 0.0, 0.0),
            ],
            1,
            &mut state,
            config,
        );

        assert_eq!(
            roles
                .iter()
                .find(|path| path.role == PathRole::Anchor)
                .unwrap()
                .path
                .path_id,
            2
        );
    }

    #[test]
    fn hysteresis_replaces_hard_demoted_anchor_immediately() {
        let mut state = RoleSelectionState {
            anchor_path_id: Some(1),
            backup_path_ids: vec![2],
            ..RoleSelectionState::default()
        };
        let mut current = path(1, "current", 15.0, 0.0, 0.0);
        current.send_failure_streak = 2;

        let roles = select_path_roles_with_state(
            &[current, path(2, "backup", 70.0, 0.0, 0.0)],
            1,
            &mut state,
            RoleSelectionConfig::default(),
        );

        assert_eq!(
            roles
                .iter()
                .find(|path| path.role == PathRole::Anchor)
                .unwrap()
                .path
                .path_id,
            2
        );
    }
}
