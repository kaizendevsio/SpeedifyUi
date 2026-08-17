use serde::{Deserialize, Serialize};

use crate::anchor::{
    AnchorTrial, AnchorTrialConfig, AnchorTrialStatus, FlapDamping, FlapDampingConfig,
    PathScoreSmoothing, ScoreSmoothingConfig, TrialObservation, TrialOutcome,
};

use std::collections::BTreeMap;

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
    /// Carrying mirrored traffic to prove it can hold the anchor role.
    Trial,
    Probe,
    Cooldown,
    Unavailable,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScoredPath {
    pub path: PathHealthSnapshot,
    /// Raw score for this tick. Used for hard demotion and under-load trial comparison.
    pub score: f64,
    /// EWMA of `score`.
    #[serde(default)]
    pub smoothed_score: f64,
    /// `smoothed_score` minus the instability penalty. This is what role selection ranks on.
    #[serde(default)]
    pub effective_score: f64,
    #[serde(default)]
    pub flap_penalty: f64,
    /// Present only on the path currently under trial.
    #[serde(default)]
    pub trial: Option<AnchorTrialStatus>,
    pub role: PathRole,
}

pub fn select_path_roles(paths: &[PathHealthSnapshot], max_backups: usize) -> Vec<ScoredPath> {
    let mut scored = score_paths(paths);
    assign_best_available_roles(&mut scored, max_backups);
    scored
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RoleSelectionState {
    pub anchor_path_id: Option<u16>,
    pub backup_path_ids: Vec<u16>,
    pub anchor_candidate_path_id: Option<u16>,
    pub anchor_candidate_ticks: u8,
    pub backup_candidate_path_ids: Vec<u16>,
    pub backup_candidate_ticks: u8,
    pub schedule_change_count: u64,
    #[serde(default)]
    pub smoothing: BTreeMap<u16, PathScoreSmoothing>,
    #[serde(default)]
    pub flap_damping: FlapDamping,
    #[serde(default)]
    pub trial: Option<AnchorTrial>,
    #[serde(default)]
    pub last_trial_outcome: Option<TrialOutcome>,
    #[serde(default)]
    pub ticks_since_trial_end: u32,
    #[serde(default)]
    pub trial_count: u64,
}

impl Default for RoleSelectionState {
    fn default() -> Self {
        Self {
            anchor_path_id: None,
            backup_path_ids: Vec::new(),
            anchor_candidate_path_id: None,
            anchor_candidate_ticks: 0,
            backup_candidate_path_ids: Vec::new(),
            backup_candidate_ticks: 0,
            schedule_change_count: 0,
            smoothing: BTreeMap::new(),
            flap_damping: FlapDamping::default(),
            trial: None,
            last_trial_outcome: None,
            // Start "long since" so the first legitimate upgrade is not delayed.
            ticks_since_trial_end: u32::MAX,
            trial_count: 0,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RoleSelectionConfig {
    pub anchor_switch_score_margin: f64,
    pub backup_switch_score_margin: f64,
    pub stable_ticks_required: u8,
    pub smoothing: ScoreSmoothingConfig,
    pub flap: FlapDampingConfig,
    pub trial: AnchorTrialConfig,
}

impl Default for RoleSelectionConfig {
    fn default() -> Self {
        Self {
            // Raised from 150/5: five clean seconds is trivial for an intermittently
            // obstructed link to fake, which is what let the anchor flap.
            anchor_switch_score_margin: 200.0,
            backup_switch_score_margin: 100.0,
            stable_ticks_required: 10,
            smoothing: ScoreSmoothingConfig::default(),
            flap: FlapDampingConfig::default(),
            trial: AnchorTrialConfig::default(),
        }
    }
}

pub fn select_path_roles_with_state(
    paths: &[PathHealthSnapshot],
    max_backups: usize,
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    recovery_active: bool,
) -> Vec<ScoredPath> {
    let mut scored = score_paths(paths);
    let live_ids = scored
        .iter()
        .map(|path| path.path.path_id)
        .collect::<Vec<_>>();
    state.flap_damping.decay(config.flap.half_life_secs);
    state.flap_damping.retain_paths(&live_ids);
    apply_score_smoothing(&mut scored, state, config, &live_ids);

    let previous_anchor = state.anchor_path_id;
    let previous_backups = state.backup_path_ids.clone();

    let anchor_id = resolve_anchor(&scored, state, config, recovery_active);
    let backup_ids = choose_backups(&scored, state, anchor_id, max_backups, config);
    let trial_path_id = state.trial.as_ref().map(|trial| trial.path_id);

    assign_hysteresis_roles(
        &mut scored,
        anchor_id,
        &backup_ids,
        trial_path_id,
        state,
        config,
    );

    if previous_anchor != anchor_id {
        // Being displaced costs the outgoing anchor some trust, which is what stops
        // A -> B -> A ping-ponging.
        if let Some(outgoing) = previous_anchor {
            state.flap_damping.add(
                outgoing,
                config.flap.penalty_demoted,
                config.flap.penalty_cap,
            );
        }
    }
    if previous_anchor != anchor_id || previous_backups != backup_ids {
        state.schedule_change_count = state.schedule_change_count.saturating_add(1);
    }
    state.anchor_path_id = anchor_id;
    state.backup_path_ids = backup_ids;

    scored
}

/// Updates the EWMA filters, publishes the derived scores onto `scored`, and re-sorts by
/// effective score so every downstream `find`/`take` picks the most *dependable* path.
fn apply_score_smoothing(
    scored: &mut [ScoredPath],
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    live_ids: &[u16],
) {
    state
        .smoothing
        .retain(|path_id, _| live_ids.contains(path_id));

    for scored_path in scored.iter_mut() {
        let smoothing = state.smoothing.entry(scored_path.path.path_id).or_default();
        smoothing.observe(scored_path.score, config.smoothing.alpha);
        scored_path.smoothed_score = smoothing.smoothed;
        // A hard-demoted path keeps its sentinel score: a good history must never let a
        // dead interface outrank a live one.
        scored_path.effective_score = if scored_path.path.is_realtime_eligible() {
            smoothing.effective_score(config.smoothing.variance_penalty_weight)
        } else {
            scored_path.score
        };
        scored_path.flap_penalty = state.flap_damping.penalty(scored_path.path.path_id);
    }

    scored.sort_by(|a, b| b.effective_score.total_cmp(&a.effective_score));
}

/// Decides the anchor for this tick, advancing or starting a prove-it trial as needed.
fn resolve_anchor(
    scored: &[ScoredPath],
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    recovery_active: bool,
) -> Option<u16> {
    state.ticks_since_trial_end = state.ticks_since_trial_end.saturating_add(1);

    let best_promotable_id = scored
        .iter()
        .find(|path| {
            is_role_eligible(path)
                && !state
                    .flap_damping
                    .is_suppressed(path.path.path_id, config.flap.suppress_threshold)
        })
        .map(path_id);
    // Failover fallback: when every healthy path is suppressed we still need an anchor.
    let best_any_id = scored
        .iter()
        .find(|path| is_role_eligible(path))
        .map(path_id);

    let current_anchor = state
        .anchor_path_id
        .and_then(|id| scored.iter().find(|path| path.path.path_id == id))
        .filter(|path| is_role_eligible(path));

    // The anchor is gone: fail over now and abandon any trial (the trial path did nothing
    // wrong, so no penalty).
    let Some(current_anchor) = current_anchor else {
        end_trial(state, TrialOutcome::Cancelled);
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return best_promotable_id.or(best_any_id);
    };
    let current_anchor_id = current_anchor.path.path_id;

    if let Some(trial) = state.trial.clone() {
        if recovery_active {
            end_trial(state, TrialOutcome::Cancelled);
            return Some(current_anchor_id);
        }
        return Some(advance_trial(scored, state, config, trial, current_anchor));
    }

    let Some(best_promotable_id) = best_promotable_id else {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    };

    if current_anchor_id == best_promotable_id {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    }

    let best_score = effective_score_for(scored, best_promotable_id).unwrap_or(f64::NEG_INFINITY);
    if best_score - current_anchor.effective_score < config.anchor_switch_score_margin {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    }

    if state.anchor_candidate_path_id == Some(best_promotable_id) {
        state.anchor_candidate_ticks = state.anchor_candidate_ticks.saturating_add(1);
    } else {
        state.anchor_candidate_path_id = Some(best_promotable_id);
        state.anchor_candidate_ticks = 1;
    }

    if state.anchor_candidate_ticks < config.stable_ticks_required {
        return Some(current_anchor_id);
    }

    state.anchor_candidate_path_id = None;
    state.anchor_candidate_ticks = 0;

    // Recovery duplicates everything, so a switch there is already masked and needs no trial.
    if !config.trial.enabled || recovery_active {
        return Some(best_promotable_id);
    }

    if state.ticks_since_trial_end < config.trial.min_interval_ticks {
        return Some(current_anchor_id);
    }

    state.trial = Some(AnchorTrial::new(best_promotable_id));
    state.trial_count = state.trial_count.saturating_add(1);
    Some(current_anchor_id)
}

/// Feeds one tick of evidence into the running trial and applies its verdict.
fn advance_trial(
    scored: &[ScoredPath],
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    mut trial: AnchorTrial,
    current_anchor: &ScoredPath,
) -> u16 {
    let current_anchor_id = current_anchor.path.path_id;
    let trial_path = scored.iter().find(|path| path.path.path_id == trial.path_id);
    let observation = TrialObservation {
        eligible: trial_path.is_some_and(is_role_eligible),
        // Raw scores, because both paths are carrying the same mirrored load right now.
        kept_up_with_anchor: trial_path.is_some_and(|path| path.score >= current_anchor.score),
        mirrored_bytes_this_tick: trial_path
            .map(|path| path.path.outbound_throughput_bps / 8)
            .unwrap_or_default(),
    };

    let outcome = trial.observe(observation, config.trial);
    match outcome {
        TrialOutcome::Running => {
            state.trial = Some(trial);
            current_anchor_id
        }
        TrialOutcome::Promoted => {
            let promoted = trial.path_id;
            end_trial(state, outcome);
            promoted
        }
        TrialOutcome::FailedUnderLoad => {
            state.flap_damping.add(
                trial.path_id,
                config.flap.penalty_trial_failed,
                config.flap.penalty_cap,
            );
            end_trial(state, outcome);
            current_anchor_id
        }
        TrialOutcome::Inconclusive | TrialOutcome::Cancelled => {
            end_trial(state, outcome);
            current_anchor_id
        }
    }
}

fn end_trial(state: &mut RoleSelectionState, outcome: TrialOutcome) {
    if state.trial.is_none() {
        return;
    }
    state.trial = None;
    state.last_trial_outcome = Some(outcome);
    state.ticks_since_trial_end = 0;
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
            ScoredPath {
                path,
                score,
                smoothed_score: score,
                effective_score: score,
                flap_penalty: 0.0,
                trial: None,
                role,
            }
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
        .filter_map(|id| effective_score_for(scored, *id))
        .min_by(f64::total_cmp)
        .unwrap_or(f64::NEG_INFINITY);
    let best_new_score = target
        .iter()
        .filter(|id| !current.contains(id))
        .filter_map(|id| effective_score_for(scored, *id))
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

fn assign_hysteresis_roles(
    scored: &mut [ScoredPath],
    anchor_id: Option<u16>,
    backup_ids: &[u16],
    trial_path_id: Option<u16>,
    state: &RoleSelectionState,
    config: RoleSelectionConfig,
) {
    for scored_path in scored {
        if !is_role_eligible(scored_path) {
            if scored_path.path.role_reason.is_none() {
                scored_path.path.role_reason = scored_path.path.demotion_reason.clone();
            }
            continue;
        }

        let id = scored_path.path.path_id;
        // Suppression never removes backup duty, so the explanation has to travel with
        // whatever role the path ends up holding or the operator would never see it.
        let suppression_note = if state
            .flap_damping
            .is_suppressed(id, config.flap.suppress_threshold)
        {
            Some(format!(
                " Suppressed as an anchor candidate (penalty {:.0}, eligible again in ~{}).",
                state.flap_damping.penalty(id),
                format_suppression_eta(state.flap_damping.seconds_until_clear(
                    id,
                    config.flap.suppress_threshold,
                    config.flap.half_life_secs,
                ))
            ))
        } else {
            None
        };

        if Some(id) == anchor_id {
            scored_path.role = PathRole::Anchor;
            scored_path.path.role_reason =
                Some("Selected as stable anchor by hysteresis scheduler.".to_string());
        } else if Some(id) == trial_path_id {
            let status = state.trial.as_ref().map(|trial| trial.status(config.trial));
            scored_path.role = PathRole::Trial;
            scored_path.path.role_reason = Some(match &status {
                Some(status) => format!(
                    "On trial as anchor candidate: {}/{} ticks, {}/{} clean under load.",
                    status.ticks,
                    status.required_ticks,
                    status.success_ticks,
                    status.required_success_ticks
                ),
                None => "On trial as anchor candidate.".to_string(),
            });
            scored_path.trial = status;
        } else if backup_ids.contains(&id) {
            scored_path.role = PathRole::Backup;
            scored_path.path.role_reason = Some(format!(
                "Selected as stable redundant backup.{}",
                suppression_note.unwrap_or_default()
            ));
        } else {
            scored_path.role = PathRole::Probe;
            scored_path.path.role_reason = Some(format!(
                "Healthy but currently kept as probe/standby.{}",
                suppression_note.unwrap_or_default()
            ));
        }
    }
}

fn format_suppression_eta(seconds: u64) -> String {
    if seconds >= 60 {
        format!("{}m", seconds.div_ceil(60))
    } else {
        format!("{seconds}s")
    }
}

fn path_id(path: &ScoredPath) -> u16 {
    path.path.path_id
}

fn effective_score_for(scored: &[ScoredPath], path_id: u16) -> Option<f64> {
    scored
        .iter()
        .find(|path| path.path.path_id == path_id)
        .map(|path| path.effective_score)
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
    fn idle_standby_without_collapse_penalty_can_outrank_higher_rtt_active_path() {
        let mut wifi = path(1, "wifi", 45.0, 0.0, 0.0);
        wifi.outbound_throughput_bps = 0;
        wifi.inbound_throughput_bps = 0;
        wifi.raw_inbound_throughput_bps = 0;
        wifi.throughput_bps = 0;
        wifi.throughput_collapse_score = 0.0;
        let mut cellular = path(2, "cellular", 150.0, 0.0, 0.0);
        cellular.outbound_throughput_bps = 64_000;
        cellular.inbound_throughput_bps = 16_000;
        cellular.raw_inbound_throughput_bps = 16_000;
        cellular.throughput_bps = 80_000;

        let roles = select_path_roles(&[cellular, wifi], 1);

        assert_eq!(roles[0].path.name, "wifi");
        assert_eq!(roles[0].role, PathRole::Anchor);
    }

    #[test]
    fn active_payload_collapse_penalty_can_demote_otherwise_good_path() {
        let mut collapsed = path(1, "collapsed", 35.0, 0.0, 0.0);
        collapsed.throughput_bps = 80_000;
        collapsed.throughput_collapse_score = 1.0;
        let mut stable = path(2, "stable", 90.0, 0.0, 0.0);
        stable.throughput_bps = 800_000;

        let roles = select_path_roles(&[collapsed, stable], 1);

        assert_eq!(roles[0].path.name, "stable");
        assert_eq!(roles[0].role, PathRole::Anchor);
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
                    path(1, "current", 150.0, 0.0, 0.0),
                    path(2, "better", 10.0, 0.0, 0.0),
                    path(3, "backup", 90.0, 0.0, 0.0),
                ],
                1,
                &mut state,
                config,
                false,
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
                path(1, "current", 150.0, 0.0, 0.0),
                path(2, "better", 10.0, 0.0, 0.0),
                path(3, "backup", 90.0, 0.0, 0.0),
            ],
            1,
            &mut state,
            config,
            false,
        );

        // Passing hysteresis now buys a trial, not the anchor role.
        assert_eq!(
            roles
                .iter()
                .find(|path| path.role == PathRole::Anchor)
                .unwrap()
                .path
                .path_id,
            1
        );
        assert_eq!(
            roles
                .iter()
                .find(|path| path.role == PathRole::Trial)
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
            false,
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
    fn trial_role_serialises_as_kebab_case() {
        assert_eq!(serde_json::to_string(&PathRole::Trial).unwrap(), "\"trial\"");
    }

    #[test]
    fn scored_paths_expose_smoothed_and_effective_scores() {
        let mut state = RoleSelectionState::default();
        let roles = select_path_roles_with_state(
            &[path(1, "fiber", 15.0, 0.0, 0.0)],
            1,
            &mut state,
            RoleSelectionConfig::default(),
            false,
        );

        let fiber = &roles[0];
        // First tick seeds the filter, so all three views agree.
        assert_eq!(fiber.smoothed_score, fiber.score);
        assert_eq!(fiber.effective_score, fiber.score);
        assert_eq!(fiber.flap_penalty, 0.0);
    }

    #[test]
    fn hard_demoted_path_keeps_its_sentinel_effective_score() {
        let mut down = path(1, "down", 15.0, 0.0, 0.0);
        down.interface_up = false;
        let mut state = RoleSelectionState::default();

        // Build a good history first, then fail the interface: smoothing must not rescue it.
        for _ in 0..10 {
            select_path_roles_with_state(
                &[
                    path(1, "down", 15.0, 0.0, 0.0),
                    path(2, "other", 50.0, 0.0, 0.0),
                ],
                1,
                &mut state,
                RoleSelectionConfig::default(),
                false,
            );
        }
        let roles = select_path_roles_with_state(
            &[down, path(2, "other", 50.0, 0.0, 0.0)],
            1,
            &mut state,
            RoleSelectionConfig::default(),
            false,
        );

        let failed = roles.iter().find(|p| p.path.path_id == 1).unwrap();
        assert_eq!(failed.effective_score, -1_000_000.0);
        assert_eq!(
            roles
                .iter()
                .find(|p| p.role == PathRole::Anchor)
                .unwrap()
                .path
                .path_id,
            2
        );
    }

    fn tick(
        state: &mut RoleSelectionState,
        paths: &[PathHealthSnapshot],
        config: RoleSelectionConfig,
    ) -> Vec<ScoredPath> {
        select_path_roles_with_state(paths, 1, state, config, false)
    }

    fn anchor_of(roles: &[ScoredPath]) -> Option<u16> {
        roles
            .iter()
            .find(|path| path.role == PathRole::Anchor)
            .map(|path| path.path.path_id)
    }

    fn trial_of(roles: &[ScoredPath]) -> Option<u16> {
        roles
            .iter()
            .find(|path| path.role == PathRole::Trial)
            .map(|path| path.path.path_id)
    }

    /// Fresh state that already holds `anchor_path_id`, so tests exercise *switching*
    /// rather than the first-tick cold start (which always takes the best path outright).
    fn state_anchored_on(path_id: u16) -> RoleSelectionState {
        RoleSelectionState {
            anchor_path_id: Some(path_id),
            ..RoleSelectionState::default()
        }
    }

    #[test]
    fn clearing_hysteresis_starts_a_trial_instead_of_switching() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 150.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];

        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 1 {
            roles = tick(&mut state, &paths, config);
            assert_eq!(anchor_of(&roles), Some(1), "the anchor must not move");
        }

        assert_eq!(trial_of(&roles), Some(2));
        assert!(state.trial.is_some());
    }

    #[test]
    fn trial_that_stays_clean_under_load_wins_the_anchor() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let mut better = path(2, "better", 10.0, 0.0, 0.0);
        better.outbound_throughput_bps = 40_000_000;
        let paths = [path(1, "fiber", 150.0, 0.0, 0.0), better];

        let mut roles = Vec::new();
        for _ in 0..u32::from(config.stable_ticks_required) + config.trial.ticks + 2 {
            roles = tick(&mut state, &paths, config);
        }

        assert_eq!(anchor_of(&roles), Some(2));
        assert!(state.trial.is_none());
        assert_eq!(state.last_trial_outcome, Some(TrialOutcome::Promoted));
    }

    #[test]
    fn obstructed_candidate_fails_its_trial_and_gets_suppressed() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let anchor = path(1, "fiber", 150.0, 0.0, 0.0);
        let mut idle_starlink = path(2, "starlink", 10.0, 0.0, 0.0);
        idle_starlink.outbound_throughput_bps = 0;
        // Under mirrored load the obstruction shows up: heavy loss and latency. The
        // throughput figure matters â€” it is what makes the verdict FailedUnderLoad rather
        // than Inconclusive.
        let mut loaded_starlink = path(2, "starlink", 400.0, 0.30, 0.10);
        loaded_starlink.outbound_throughput_bps = 40_000_000;

        // Idle phase: Starlink looks great and earns a trial.
        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 1 {
            roles = tick(&mut state, &[anchor.clone(), idle_starlink.clone()], config);
        }
        assert_eq!(trial_of(&roles), Some(2));

        // Loaded phase: it degrades and the trial aborts.
        for _ in 0..config.trial.ticks + 1 {
            roles = tick(&mut state, &[anchor.clone(), loaded_starlink.clone()], config);
        }

        assert_eq!(anchor_of(&roles), Some(1), "anchor must never have moved");
        assert_eq!(state.last_trial_outcome, Some(TrialOutcome::FailedUnderLoad));
        assert!(state
            .flap_damping
            .is_suppressed(2, config.flap.suppress_threshold));
    }

    #[test]
    fn suppressed_path_cannot_start_another_trial() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        state
            .flap_damping
            .add(2, config.flap.penalty_demoted, config.flap.penalty_cap);
        let paths = [
            path(1, "fiber", 150.0, 0.0, 0.0),
            path(2, "suppressed", 10.0, 0.0, 0.0),
        ];

        let mut roles = Vec::new();
        for _ in 0..u32::from(config.stable_ticks_required) + config.trial.ticks + 5 {
            roles = tick(&mut state, &paths, config);
        }

        assert_eq!(anchor_of(&roles), Some(1));
        assert_eq!(trial_of(&roles), None);
        assert!(state.trial.is_none());
    }

    #[test]
    fn suppressed_path_still_takes_over_when_the_anchor_dies() {
        // Suppression gates upgrades only. Failover must never be blocked.
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        state
            .flap_damping
            .add(2, config.flap.penalty_cap, config.flap.penalty_cap);
        let mut dead = path(1, "fiber", 150.0, 0.0, 0.0);
        dead.interface_up = false;

        let roles = tick(
            &mut state,
            &[dead, path(2, "suppressed", 10.0, 0.0, 0.0)],
            config,
        );

        assert_eq!(anchor_of(&roles), Some(2));
    }

    #[test]
    fn losing_the_anchor_role_charges_a_flap_penalty() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let mut dead = path(1, "fiber", 150.0, 0.0, 0.0);
        dead.interface_up = false;

        tick(&mut state, &[dead, path(2, "other", 10.0, 0.0, 0.0)], config);

        assert_eq!(state.flap_damping.penalty(1), config.flap.penalty_demoted);
    }

    #[test]
    fn recovery_mode_promotes_directly_without_a_trial() {
        // Everything is duplicated during recovery, so a switch is already masked.
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 150.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];

        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 1 {
            roles = select_path_roles_with_state(&paths, 1, &mut state, config, true);
        }

        assert_eq!(anchor_of(&roles), Some(2));
        assert_eq!(trial_of(&roles), None);
    }

    #[test]
    fn recovery_cancels_an_in_flight_trial_without_penalty() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 150.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];
        for _ in 0..config.stable_ticks_required + 1 {
            tick(&mut state, &paths, config);
        }
        assert!(state.trial.is_some());

        select_path_roles_with_state(&paths, 1, &mut state, config, true);

        assert!(state.trial.is_none());
        assert_eq!(state.last_trial_outcome, Some(TrialOutcome::Cancelled));
        assert_eq!(state.flap_damping.penalty(2), 0.0);
    }

    #[test]
    fn trial_spacing_delays_the_next_trial() {
        let config = RoleSelectionConfig::default();
        let mut state = RoleSelectionState {
            ticks_since_trial_end: 0,
            ..state_anchored_on(1)
        };
        let paths = [
            path(1, "fiber", 150.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];

        for _ in 0..config.stable_ticks_required + 1 {
            tick(&mut state, &paths, config);
        }
        assert!(state.trial.is_none(), "spacing window must block the trial");

        for _ in 0..config.trial.min_interval_ticks {
            tick(&mut state, &paths, config);
        }
        assert!(state.trial.is_some());
    }

    /// Suppression only blocks *promotion*. The path keeps carrying redundant traffic, so
    /// the explanation has to ride along with whatever role it ends up holding.
    #[test]
    fn suppressed_path_keeps_backup_duty_and_explains_itself() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        state
            .flap_damping
            .add(2, config.flap.penalty_demoted, config.flap.penalty_cap);

        let roles = tick(
            &mut state,
            &[
                path(1, "fiber", 150.0, 0.0, 0.0),
                path(2, "suppressed", 10.0, 0.0, 0.0),
            ],
            config,
        );

        let suppressed = roles.iter().find(|p| p.path.path_id == 2).unwrap();
        assert_eq!(suppressed.role, PathRole::Backup);
        let reason = suppressed.path.role_reason.as_deref().unwrap();
        assert!(reason.contains("Suppressed"), "unexpected reason: {reason}");
        assert!(reason.contains("eligible again"), "unexpected reason: {reason}");
    }

    #[test]
    fn unsuppressed_path_reason_has_no_suppression_note() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);

        let roles = tick(
            &mut state,
            &[
                path(1, "fiber", 150.0, 0.0, 0.0),
                path(2, "clean", 10.0, 0.0, 0.0),
            ],
            config,
        );

        let backup = roles.iter().find(|p| p.path.path_id == 2).unwrap();
        assert_eq!(
            backup.path.role_reason.as_deref(),
            Some("Selected as stable redundant backup.")
        );
    }

    #[test]
    fn trial_path_reports_progress_in_its_role_reason() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 150.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];
        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 2 {
            roles = tick(&mut state, &paths, config);
        }

        let trial = roles.iter().find(|p| p.role == PathRole::Trial).unwrap();
        assert!(trial.trial.is_some());
        let reason = trial.path.role_reason.as_deref().unwrap();
        assert!(reason.contains("trial"), "unexpected reason: {reason}");
    }

    /// A loaded fiber line: bufferbloat inflates RTT and jitter, the queue is under
    /// pressure, and a few packets miss their deadline. This is the anchor.
    fn loaded_fiber() -> PathHealthSnapshot {
        let mut fiber = path(1, "globe-fiber", 60.0, 0.0, 0.04);
        fiber.jitter_ms = Some(35.0);
        fiber.queue_pressure = 0.35;
        fiber
    }

    /// The same dish while it is only passing heartbeats: nothing is loaded, so nothing
    /// looks wrong. This is the measurement asymmetry the design exists to defeat.
    fn idle_starlink() -> PathHealthSnapshot {
        let mut starlink = path(2, "starlink", 12.0, 0.0, 0.0);
        starlink.jitter_ms = Some(4.0);
        starlink.outbound_throughput_bps = 0;
        starlink.inbound_throughput_bps = 0;
        starlink.raw_inbound_throughput_bps = 0;
        starlink.throughput_bps = 0;
        starlink
    }

    /// The dish carrying mirrored trial traffic while the roof obstruction bites.
    fn obstructed_starlink() -> PathHealthSnapshot {
        let mut starlink = path(2, "starlink", 520.0, 0.35, 0.12);
        starlink.jitter_ms = Some(180.0);
        starlink.outbound_throughput_bps = 40_000_000;
        starlink
    }

    /// The dish carrying mirrored trial traffic with a clear view of the sky.
    fn clear_loaded_starlink() -> PathHealthSnapshot {
        let mut starlink = path(2, "starlink", 55.0, 0.005, 0.0);
        starlink.jitter_ms = Some(20.0);
        starlink.outbound_throughput_bps = 40_000_000;
        starlink
    }

    /// The reported production failure: a stable fiber anchor and a Starlink dish that
    /// looks pristine while idle and falls apart the moment it carries real traffic.
    /// Obstruction cycles are shorter than the trial window, so every trial must fail and
    /// the anchor must never move.
    #[test]
    fn obstruction_cycles_never_move_the_anchor() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);

        // Assert the premise, so a scoring change surfaces here rather than as a
        // confusing downstream failure.
        let premise = score_paths(&[loaded_fiber(), idle_starlink()]);
        let fiber_score = premise.iter().find(|p| p.path.path_id == 1).unwrap().score;
        let idle_score = premise.iter().find(|p| p.path.path_id == 2).unwrap().score;
        assert!(
            idle_score - fiber_score >= config.anchor_switch_score_margin,
            "fixture premise broken: idle starlink {idle_score} vs loaded fiber \
             {fiber_score} must differ by at least {}",
            config.anchor_switch_score_margin
        );

        let mut anchor_changes = 0u32;
        let mut previous_anchor = None;

        // 30 minutes of 10-second obstruction cycles. A 20-tick trial therefore always
        // spans more obstructed ticks than it is allowed to fail.
        for tick_index in 0..1_800u32 {
            let obstructed_now = (tick_index / 10) % 2 == 1;
            // Load only appears on the dish while it is on trial, because that is the
            // only time the scheduler mirrors traffic onto it.
            let starlink = match (state.trial.is_some(), obstructed_now) {
                (true, true) => obstructed_starlink(),
                (true, false) => clear_loaded_starlink(),
                (false, _) => idle_starlink(),
            };
            let roles = tick(&mut state, &[loaded_fiber(), starlink], config);

            let anchor = anchor_of(&roles);
            if previous_anchor.is_some() && previous_anchor != anchor {
                anchor_changes += 1;
            }
            previous_anchor = anchor;
        }

        assert_eq!(previous_anchor, Some(1), "fiber must still hold the anchor");
        assert_eq!(anchor_changes, 0, "the anchor must never have moved");
        assert!(
            state.trial_count >= 2,
            "the candidate should have been retried, saw {} trials",
            state.trial_count
        );
        // Without damping this would be roughly one trial per obstruction cycle (~90).
        assert!(
            state.trial_count <= 12,
            "flap damping should throttle retries, saw {} trials",
            state.trial_count
        );
    }

    /// The mirror image: a genuinely good path must still be able to win the anchor.
    #[test]
    fn a_genuinely_better_path_is_promoted_within_a_bounded_number_of_ticks() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let mut fast = path(2, "new-fiber", 12.0, 0.0, 0.0);
        fast.outbound_throughput_bps = 40_000_000;
        let paths = [path(1, "old-dsl", 140.0, 0.0, 0.0), fast];

        let mut promoted_after = None;
        for tick_index in 0..200u32 {
            let roles = tick(&mut state, &paths, config);
            if anchor_of(&roles) == Some(2) {
                // One-based: this is the count of ticks that ran.
                promoted_after = Some(tick_index + 1);
                break;
            }
        }

        let promoted_after = promoted_after.expect("a clearly better path must eventually win");
        let earliest = u32::from(config.stable_ticks_required) + config.trial.ticks;
        assert!(
            promoted_after >= earliest,
            "promoted too eagerly after {promoted_after} ticks, expected at least {earliest}"
        );
        assert!(
            promoted_after <= earliest + 5,
            "promotion took too long: {promoted_after} ticks"
        );
    }
}
