use serde::{Deserialize, Serialize};

use std::collections::BTreeMap;

use crate::health::{PathHealthSnapshot, PathRole, ScoredPath};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum RedundancyPolicy {
    Reliable,
    Balanced,
    Fast,
    Diagnostic,
}

impl Default for RedundancyPolicy {
    fn default() -> Self {
        Self::Balanced
    }
}

impl std::str::FromStr for RedundancyPolicy {
    type Err = String;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        match value.trim().to_ascii_lowercase().as_str() {
            "reliable" => Ok(Self::Reliable),
            "balanced" => Ok(Self::Balanced),
            "fast" => Ok(Self::Fast),
            "diagnostic" => Ok(Self::Diagnostic),
            other => Err(format!("unknown redundancy policy: {other}")),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct RedundancyPolicyConfig {
    pub interactive_packet_threshold_bytes: usize,
    pub duplicate_loss_threshold: f64,
    pub backup_loss_disable_threshold: f64,
}

impl Default for RedundancyPolicyConfig {
    fn default() -> Self {
        Self {
            interactive_packet_threshold_bytes: 768,
            duplicate_loss_threshold: 0.02,
            backup_loss_disable_threshold: 0.35,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct RecoveryConfig {
    pub enabled: bool,
    pub enter_degraded_ticks: u32,
    pub exit_clean_ticks: u32,
    pub degraded_rtt_ms: f64,
    pub degraded_loss_threshold: f64,
    pub degraded_late_threshold: f64,
    pub degraded_jitter_ms: f64,
    pub degraded_stale_ack_ms: u64,
    pub degraded_queue_pressure: f64,
    pub clean_rtt_ms: f64,
    pub clean_loss_threshold: f64,
    pub clean_late_threshold: f64,
    pub clean_jitter_ms: f64,
    pub clean_stale_ack_ms: u64,
    pub clean_queue_pressure: f64,
    pub path_loss_exclude_threshold: f64,
}

impl Default for RecoveryConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            enter_degraded_ticks: 3,
            exit_clean_ticks: 20,
            degraded_rtt_ms: 180.0,
            degraded_loss_threshold: 0.08,
            degraded_late_threshold: 0.03,
            degraded_jitter_ms: 60.0,
            degraded_stale_ack_ms: 1_500,
            degraded_queue_pressure: 0.70,
            clean_rtt_ms: 120.0,
            clean_loss_threshold: 0.02,
            clean_late_threshold: 0.01,
            clean_jitter_ms: 40.0,
            clean_stale_ack_ms: 1_000,
            clean_queue_pressure: 0.50,
            path_loss_exclude_threshold: 0.95,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RecoveryStatus {
    pub active: bool,
    pub reason: String,
    pub eligible_path_ids: Vec<u16>,
    pub degraded_ticks: u32,
    pub clean_ticks: u32,
    #[serde(default)]
    pub soft_loss_ticks: u32,
}

impl Default for RecoveryStatus {
    fn default() -> Self {
        Self {
            active: false,
            reason: "Recovery redundancy is inactive.".to_string(),
            eligible_path_ids: Vec::new(),
            degraded_ticks: 0,
            clean_ticks: 0,
            soft_loss_ticks: 0,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RecoveryState {
    pub active: bool,
    pub degraded_ticks: u32,
    pub clean_ticks: u32,
    pub reason: String,
    pub soft_loss_ticks: u32,
    soft_loss_expired_by_path: BTreeMap<u16, u64>,
}

impl Default for RecoveryState {
    fn default() -> Self {
        Self {
            active: false,
            degraded_ticks: 0,
            clean_ticks: 0,
            reason: "Recovery redundancy is inactive.".to_string(),
            soft_loss_ticks: 0,
            soft_loss_expired_by_path: BTreeMap::new(),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ScheduleMode {
    AnchorOnly,
    #[serde(rename = "anchor-duplicate-1", alias = "anchor-duplicate1")]
    AnchorDuplicate1,
    AnchorFec,
    FullDuplicateDebug,
}

impl Default for ScheduleMode {
    fn default() -> Self {
        Self::AnchorDuplicate1
    }
}

impl std::str::FromStr for ScheduleMode {
    type Err = String;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        match value.trim().to_ascii_lowercase().as_str() {
            "anchor-only" | "anchoronly" => Ok(Self::AnchorOnly),
            "anchor-duplicate-1" | "anchorduplicate1" => Ok(Self::AnchorDuplicate1),
            "anchor-fec" | "anchorfec" => Ok(Self::AnchorFec),
            "full-duplicate-debug" | "fullduplicatedebug" => Ok(Self::FullDuplicateDebug),
            other => Err(format!("unknown schedule mode: {other}")),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SchedulePlan {
    pub mode: ScheduleMode,
    pub anchor_path_id: Option<u16>,
    pub data_path_ids: Vec<u16>,
    pub duplicate_path_ids: Vec<u16>,
    pub fec_path_ids: Vec<u16>,
    /// Anchor candidate under load test. Mirrored as duplicates in every policy so the
    /// candidate is judged on real traffic. Defaulted for wire compatibility.
    #[serde(default)]
    pub trial_path_ids: Vec<u16>,
}

pub fn build_schedule(mode: ScheduleMode, roles: &[ScoredPath]) -> SchedulePlan {
    let anchor = roles
        .iter()
        .find(|path| path.role == PathRole::Anchor)
        .map(|path| path.path.path_id);
    let backups: Vec<u16> = roles
        .iter()
        .filter(|path| path.role == PathRole::Backup)
        .map(|path| path.path.path_id)
        .collect();
    let trials: Vec<u16> = roles
        .iter()
        .filter(|path| path.role == PathRole::Trial)
        .map(|path| path.path.path_id)
        .collect();

    match mode {
        ScheduleMode::AnchorOnly => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: Vec::new(),
            fec_path_ids: Vec::new(),
            trial_path_ids: trials,
        },
        ScheduleMode::AnchorDuplicate1 => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: backups.into_iter().take(1).collect(),
            fec_path_ids: Vec::new(),
            trial_path_ids: trials,
        },
        ScheduleMode::AnchorFec => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: Vec::new(),
            fec_path_ids: backups,
            trial_path_ids: trials,
        },
        ScheduleMode::FullDuplicateDebug => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: backups,
            fec_path_ids: Vec::new(),
            trial_path_ids: trials,
        },
    }
}

pub fn update_recovery_state(
    state: &mut RecoveryState,
    policy: RedundancyPolicy,
    paths: &[PathHealthSnapshot],
    config: RecoveryConfig,
) -> RecoveryStatus {
    let eligible_paths = paths
        .iter()
        .filter(|path| is_recovery_path_eligible(path, config))
        .collect::<Vec<_>>();
    let eligible_path_ids = eligible_paths
        .iter()
        .map(|path| path.path_id)
        .collect::<Vec<_>>();

    if !config.enabled {
        *state = RecoveryState {
            reason: "Recovery redundancy is disabled by config.".to_string(),
            ..RecoveryState::default()
        };
        return recovery_status(state, eligible_path_ids);
    }

    if !matches!(
        policy,
        RedundancyPolicy::Reliable | RedundancyPolicy::Balanced
    ) {
        *state = RecoveryState {
            reason: "Recovery redundancy is inactive for the selected policy.".to_string(),
            ..RecoveryState::default()
        };
        return recovery_status(state, eligible_path_ids);
    }

    if eligible_paths.len() < 2 {
        *state = RecoveryState {
            reason: "Recovery redundancy needs at least two usable paths.".to_string(),
            ..RecoveryState::default()
        };
        return recovery_status(state, eligible_path_ids);
    }

    let all_degraded = eligible_paths
        .iter()
        .all(|path| is_recovery_path_degraded(path, config));
    let soft_loss_only = all_degraded
        && eligible_paths
            .iter()
            .all(|path| is_soft_heartbeat_loss_only(path, config));
    let soft_loss_has_new_expiry = soft_loss_only
        && eligible_paths.iter().any(|path| {
            path.heartbeat_expired
                > state
                    .soft_loss_expired_by_path
                    .get(&path.path_id)
                    .copied()
                    .unwrap_or_default()
        });
    let any_clean = eligible_paths
        .iter()
        .any(|path| is_recovery_path_clean(path, config));

    if state.active {
        state.soft_loss_ticks = 0;
        if any_clean {
            state.clean_ticks = state.clean_ticks.saturating_add(1);
            state.reason = format!(
                "Recovery active; waiting for clean path hysteresis ({}/{}).",
                state.clean_ticks, config.exit_clean_ticks
            );
            if state.clean_ticks >= config.exit_clean_ticks {
                state.active = false;
                state.degraded_ticks = 0;
                state.clean_ticks = 0;
                state.reason =
                    "Recovery redundancy exited after a clean path stayed healthy.".to_string();
            }
        } else {
            state.clean_ticks = 0;
            state.reason = "Recovery active because all usable paths remain degraded.".to_string();
        }
    } else if soft_loss_only && soft_loss_has_new_expiry {
        state.soft_loss_ticks = state.soft_loss_ticks.saturating_add(1);
        state.clean_ticks = 0;
        state.degraded_ticks = 0;
        state.reason = format!(
            "Heartbeat loss persists across scheduler rounds ({}/2).",
            state.soft_loss_ticks
        );
        if state.soft_loss_ticks >= 2 {
            state.active = true;
            state.soft_loss_ticks = 0;
            state.reason =
                "Recovery active after heartbeat loss persisted for two scheduler rounds."
                    .to_string();
        }
    } else if soft_loss_only {
        state.soft_loss_ticks = 0;
        state.degraded_ticks = 0;
        state.clean_ticks = 0;
        state.reason =
            "Recovery inactive; the retained heartbeat-loss window has no new misses.".to_string();
    } else if all_degraded {
        state.soft_loss_ticks = 0;
        state.degraded_ticks = state.degraded_ticks.saturating_add(1);
        state.clean_ticks = 0;
        state.reason = format!(
            "All usable paths are degraded ({}/{}).",
            state.degraded_ticks, config.enter_degraded_ticks
        );
        if state.degraded_ticks >= config.enter_degraded_ticks {
            state.active = true;
            state.clean_ticks = 0;
            state.reason =
                "Recovery active; duplicating all traffic across usable paths.".to_string();
        }
    } else {
        state.soft_loss_ticks = 0;
        state.degraded_ticks = 0;
        state.clean_ticks = 0;
        state.reason = "Recovery inactive; at least one usable path is healthy.".to_string();
    }

    state.soft_loss_expired_by_path = paths
        .iter()
        .map(|path| (path.path_id, path.heartbeat_expired))
        .collect();

    recovery_status(state, eligible_path_ids)
}

pub fn expand_schedule_for_recovery(
    schedule: &SchedulePlan,
    roles: &[ScoredPath],
    config: RecoveryConfig,
) -> SchedulePlan {
    let Some(anchor_path_id) = schedule.anchor_path_id else {
        return schedule.clone();
    };

    let duplicate_path_ids = recovery_duplicate_path_ids(anchor_path_id, roles, config);

    SchedulePlan {
        mode: schedule.mode,
        anchor_path_id: schedule.anchor_path_id,
        data_path_ids: schedule.data_path_ids.clone(),
        duplicate_path_ids,
        fec_path_ids: Vec::new(),
        trial_path_ids: schedule.trial_path_ids.clone(),
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RecoveryScheduleStabilityState {
    pub stable_anchor_path_id: Option<u16>,
    pub stable_duplicate_path_ids: Vec<u16>,
    pub ticks_remaining: u32,
}

pub fn stabilize_recovery_schedule(
    state: &mut RecoveryScheduleStabilityState,
    schedule: &SchedulePlan,
    roles: &[ScoredPath],
    recovery_status: &RecoveryStatus,
    config: RecoveryConfig,
    hold_ticks: u32,
) -> SchedulePlan {
    if !recovery_status.active {
        *state = RecoveryScheduleStabilityState::default();
        return schedule.clone();
    }

    let Some(anchor_path_id) = schedule.anchor_path_id else {
        *state = RecoveryScheduleStabilityState::default();
        return schedule.clone();
    };

    let eligible = recovery_duplicate_path_ids(anchor_path_id, roles, config);

    let stable_ids_still_eligible = !state.stable_duplicate_path_ids.is_empty()
        && state
            .stable_duplicate_path_ids
            .iter()
            .all(|path_id| eligible.contains(path_id));
    let anchor_changed = state.stable_anchor_path_id != Some(anchor_path_id);
    let should_refresh = anchor_changed
        || state.ticks_remaining == 0
        || !stable_ids_still_eligible
        || state
            .stable_duplicate_path_ids
            .iter()
            .any(|path_id| hard_demoted_path(roles, *path_id));

    if should_refresh {
        state.stable_anchor_path_id = Some(anchor_path_id);
        state.stable_duplicate_path_ids = schedule
            .duplicate_path_ids
            .iter()
            .copied()
            .filter(|path_id| eligible.contains(path_id))
            .collect();
        state.ticks_remaining = hold_ticks;
    } else {
        state.ticks_remaining = state.ticks_remaining.saturating_sub(1);
    }

    SchedulePlan {
        mode: schedule.mode,
        anchor_path_id: schedule.anchor_path_id,
        data_path_ids: schedule.data_path_ids.clone(),
        duplicate_path_ids: state.stable_duplicate_path_ids.clone(),
        fec_path_ids: schedule.fec_path_ids.clone(),
        trial_path_ids: schedule.trial_path_ids.clone(),
    }
}

fn hard_demoted_path(roles: &[ScoredPath], path_id: u16) -> bool {
    roles
        .iter()
        .find(|role| role.path.path_id == path_id)
        .is_none_or(|role| role.path.hard_demotion_reason().is_some())
}

fn recovery_status(state: &RecoveryState, eligible_path_ids: Vec<u16>) -> RecoveryStatus {
    RecoveryStatus {
        active: state.active,
        reason: state.reason.clone(),
        eligible_path_ids,
        degraded_ticks: state.degraded_ticks,
        clean_ticks: state.clean_ticks,
        soft_loss_ticks: state.soft_loss_ticks,
    }
}

fn is_recovery_path_eligible(path: &PathHealthSnapshot, config: RecoveryConfig) -> bool {
    path.hard_demotion_reason().is_none()
        && effective_heartbeat_loss_rate(path) < config.path_loss_exclude_threshold.clamp(0.0, 1.0)
}

fn recovery_duplicate_path_ids(
    anchor_path_id: u16,
    roles: &[ScoredPath],
    config: RecoveryConfig,
) -> Vec<u16> {
    let candidates = roles
        .iter()
        .filter(|path| path.path.path_id != anchor_path_id)
        .filter(|path| is_recovery_path_eligible(&path.path, config))
        .collect::<Vec<_>>();

    let preferred = candidates
        .iter()
        .filter(|path| !is_harmful_recovery_duplicate(&path.path, config))
        .map(|path| path.path.path_id)
        .collect::<Vec<_>>();

    if preferred.is_empty() {
        let useful = candidates.iter().copied().max_by(|left, right| {
            left.path
                .duplicate_usefulness
                .total_cmp(&right.path.duplicate_usefulness)
                .then_with(|| {
                    left.path
                        .duplicate_inbound_throughput_bps
                        .cmp(&right.path.duplicate_inbound_throughput_bps)
                })
        });
        useful
            .filter(|path| {
                path.path.duplicate_usefulness > 0.0
                    || path.path.duplicate_inbound_throughput_bps > 0
            })
            .map(|path| vec![path.path.path_id])
            .unwrap_or_default()
    } else {
        preferred
    }
}

fn is_harmful_recovery_duplicate(path: &PathHealthSnapshot, config: RecoveryConfig) -> bool {
    path.hard_demotion_reason().is_some()
        || effective_heartbeat_loss_rate(path) >= config.path_loss_exclude_threshold.clamp(0.0, 1.0)
        || path
            .stale_ack_ms
            .is_some_and(|age| age >= config.degraded_stale_ack_ms.saturating_mul(2).max(3_000))
        || path.send_failure_streak >= 2
        || path.queue_pressure >= 0.95
}

fn is_recovery_path_degraded(path: &PathHealthSnapshot, config: RecoveryConfig) -> bool {
    path.rtt_ms.is_none()
        && path
            .stale_ack_ms
            .is_some_and(|age| age >= config.degraded_stale_ack_ms)
        || path.rtt_ms.is_some_and(|rtt| rtt >= config.degraded_rtt_ms)
        || effective_heartbeat_loss_rate(path) >= config.degraded_loss_threshold
        || path.late_rate >= config.degraded_late_threshold
        || path
            .jitter_ms
            .is_some_and(|jitter| jitter >= config.degraded_jitter_ms)
        || path
            .stale_ack_ms
            .is_some_and(|age| age >= config.degraded_stale_ack_ms)
        || path.send_failure_streak >= 1
        || path.queue_pressure >= config.degraded_queue_pressure
}

fn is_soft_heartbeat_loss_only(path: &PathHealthSnapshot, config: RecoveryConfig) -> bool {
    effective_heartbeat_loss_rate(path) >= config.degraded_loss_threshold
        && path.rtt_ms.is_none_or(|rtt| rtt < config.degraded_rtt_ms)
        && path.late_rate < config.degraded_late_threshold
        && path
            .jitter_ms
            .is_none_or(|jitter| jitter < config.degraded_jitter_ms)
        && path
            .stale_ack_ms
            .is_none_or(|age| age < config.degraded_stale_ack_ms)
        && path.send_failure_streak == 0
        && path.queue_pressure < config.degraded_queue_pressure
}

fn is_recovery_path_clean(path: &PathHealthSnapshot, config: RecoveryConfig) -> bool {
    path.rtt_ms.is_some_and(|rtt| rtt < config.clean_rtt_ms)
        && effective_heartbeat_loss_rate(path) < config.clean_loss_threshold
        && path.late_rate < config.clean_late_threshold
        && path
            .jitter_ms
            .is_none_or(|jitter| jitter < config.clean_jitter_ms)
        && path
            .stale_ack_ms
            .is_none_or(|age| age < config.clean_stale_ack_ms)
        && path.send_failure_streak == 0
        && path.queue_pressure < config.clean_queue_pressure
}

fn effective_heartbeat_loss_rate(path: &PathHealthSnapshot) -> f64 {
    if path.heartbeat_warming_up && !path.heartbeat_failed {
        0.0
    } else {
        path.loss_rate
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct ScheduledTransmission {
    pub path_id: u16,
    pub packet_kind: crate::protocol::PacketKind,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PacketTransmissionPlans {
    pub small: Vec<ScheduledTransmission>,
    pub bulk: Vec<ScheduledTransmission>,
}

impl PacketTransmissionPlans {
    pub fn for_packet_len(
        &self,
        packet_len: usize,
        interactive_packet_threshold_bytes: usize,
    ) -> &[ScheduledTransmission] {
        if packet_len <= interactive_packet_threshold_bytes {
            &self.small
        } else {
            &self.bulk
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScheduleControlMessage {
    #[serde(default)]
    pub schedule_generation: u64,
    pub schedule: SchedulePlan,
    #[serde(default)]
    pub redundancy_policy: RedundancyPolicy,
    #[serde(default)]
    pub policy_config: RedundancyPolicyConfig,
    #[serde(default)]
    pub paths: Vec<PathHealthSnapshot>,
    #[serde(default)]
    pub recovery_active: bool,
}

pub fn build_transmission_plan(schedule: &SchedulePlan) -> Vec<ScheduledTransmission> {
    let mut transmissions = Vec::new();

    for path_id in &schedule.data_path_ids {
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Data,
        });
    }

    for path_id in &schedule.duplicate_path_ids {
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Duplicate,
        });
    }

    for path_id in &schedule.trial_path_ids {
        if schedule.data_path_ids.contains(path_id)
            || schedule.duplicate_path_ids.contains(path_id)
            || schedule.fec_path_ids.contains(path_id)
        {
            continue;
        }
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Duplicate,
        });
    }

    for path_id in &schedule.fec_path_ids {
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Fec,
        });
    }

    transmissions
}

/// Trial mirroring is unconditional across policies, but a trial path that just lost
/// carrier must not be handed packets.
fn healthy_trial_path_ids(schedule: &SchedulePlan, paths: &[PathHealthSnapshot]) -> Vec<u16> {
    schedule
        .trial_path_ids
        .iter()
        .copied()
        .filter(|path_id| {
            paths
                .iter()
                .find(|path| path.path_id == *path_id)
                .is_some_and(|path| path.interface_up && !path.in_cooldown)
        })
        .collect()
}

pub fn build_transmission_plan_for_packet(
    schedule: &SchedulePlan,
    policy: RedundancyPolicy,
    packet_len: usize,
    paths: &[PathHealthSnapshot],
    policy_config: RedundancyPolicyConfig,
) -> Vec<ScheduledTransmission> {
    if matches!(
        policy,
        RedundancyPolicy::Reliable | RedundancyPolicy::Diagnostic
    ) || matches!(schedule.mode, ScheduleMode::FullDuplicateDebug)
    {
        return build_transmission_plan(schedule);
    }

    if matches!(schedule.mode, ScheduleMode::AnchorOnly) {
        return build_transmission_plan(schedule);
    }

    let mut plan = SchedulePlan {
        mode: schedule.mode,
        anchor_path_id: schedule.anchor_path_id,
        data_path_ids: schedule.data_path_ids.clone(),
        duplicate_path_ids: Vec::new(),
        fec_path_ids: Vec::new(),
        trial_path_ids: healthy_trial_path_ids(schedule, paths),
    };

    let anchor_loss = schedule
        .anchor_path_id
        .and_then(|anchor_id| paths.iter().find(|path| path.path_id == anchor_id))
        .map(|path| path.loss_rate.clamp(0.0, 1.0))
        .unwrap_or(0.0);

    let small_packet = packet_len <= policy_config.interactive_packet_threshold_bytes;
    let anchor_degraded = anchor_loss >= policy_config.duplicate_loss_threshold;

    let should_duplicate = match policy {
        RedundancyPolicy::Reliable | RedundancyPolicy::Diagnostic => true,
        RedundancyPolicy::Balanced => small_packet || anchor_degraded,
        RedundancyPolicy::Fast => anchor_degraded,
    };

    let healthy_backup_ids = schedule
        .duplicate_path_ids
        .iter()
        .chain(schedule.fec_path_ids.iter())
        .copied()
        .filter(|path_id| {
            paths
                .iter()
                .find(|path| path.path_id == *path_id)
                .is_some_and(|path| {
                    path.interface_up
                        && !path.in_cooldown
                        && path.loss_rate < policy_config.backup_loss_disable_threshold
                })
        })
        .collect::<Vec<_>>();

    if should_duplicate {
        plan.duplicate_path_ids = healthy_backup_ids;
    } else if matches!(schedule.mode, ScheduleMode::AnchorFec) {
        plan.fec_path_ids = healthy_backup_ids;
    }

    build_transmission_plan(&plan)
}

pub fn precompute_transmission_plans(
    schedule: &SchedulePlan,
    policy: RedundancyPolicy,
    paths: &[PathHealthSnapshot],
    policy_config: RedundancyPolicyConfig,
) -> PacketTransmissionPlans {
    PacketTransmissionPlans {
        small: build_transmission_plan_for_packet(
            schedule,
            policy,
            policy_config.interactive_packet_threshold_bytes,
            paths,
            policy_config,
        ),
        bulk: build_transmission_plan_for_packet(
            schedule,
            policy,
            policy_config
                .interactive_packet_threshold_bytes
                .saturating_add(1),
            paths,
            policy_config,
        ),
    }
}

#[cfg(test)]
mod tests {
    use crate::health::{select_path_roles, PathHealthSnapshot, PathRole};
    use crate::protocol::PacketKind;

    use super::*;

    fn trial_schedule() -> SchedulePlan {
        SchedulePlan {
            mode: ScheduleMode::AnchorDuplicate1,
            anchor_path_id: Some(1),
            data_path_ids: vec![1],
            duplicate_path_ids: vec![2],
            fec_path_ids: Vec::new(),
            trial_path_ids: vec![3],
        }
    }

    #[test]
    fn build_schedule_collects_trial_paths_from_roles() {
        let mut roles = select_path_roles(&[path(1, 20.0, 0.0), path(2, 60.0, 0.0)], 1);
        let trial_index = roles
            .iter()
            .position(|role| role.path.path_id == 2)
            .expect("path 2 must be scored");
        roles[trial_index].role = PathRole::Trial;

        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);

        assert_eq!(plan.anchor_path_id, Some(1));
        assert_eq!(plan.data_path_ids, vec![1]);
        assert_eq!(plan.trial_path_ids, vec![2]);
        assert!(
            plan.duplicate_path_ids.is_empty(),
            "a trial path is not a backup"
        );
    }

    #[test]
    fn trial_path_is_mirrored_as_duplicate() {
        let transmissions = build_transmission_plan(&trial_schedule());

        assert_eq!(
            transmissions,
            vec![
                ScheduledTransmission {
                    path_id: 1,
                    packet_kind: PacketKind::Data
                },
                ScheduledTransmission {
                    path_id: 2,
                    packet_kind: PacketKind::Duplicate
                },
                ScheduledTransmission {
                    path_id: 3,
                    packet_kind: PacketKind::Duplicate
                },
            ]
        );
    }

    #[test]
    fn trial_path_is_never_mirrored_twice() {
        let mut plan = trial_schedule();
        plan.trial_path_ids = vec![2, 3];

        let transmissions = build_transmission_plan(&plan);

        assert_eq!(
            transmissions
                .iter()
                .filter(|transmission| transmission.path_id == 2)
                .count(),
            1
        );
    }

    #[test]
    fn fast_policy_mirrors_bulk_onto_the_trial_path_even_when_healthy() {
        // This is the whole point of the trial: fast/balanced normally send bulk on the
        // anchor alone, so without this the candidate would never see real load.
        let health = vec![path(1, 20.0, 0.0), path(2, 60.0, 0.0), path(3, 30.0, 0.0)];

        let transmissions = build_transmission_plan_for_packet(
            &trial_schedule(),
            RedundancyPolicy::Fast,
            1_200,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert_eq!(
            transmissions,
            vec![
                ScheduledTransmission {
                    path_id: 1,
                    packet_kind: PacketKind::Data
                },
                ScheduledTransmission {
                    path_id: 3,
                    packet_kind: PacketKind::Duplicate
                },
            ]
        );
    }

    #[test]
    fn balanced_policy_mirrors_small_packets_onto_the_trial_path() {
        let health = vec![path(1, 20.0, 0.0), path(2, 60.0, 0.0), path(3, 30.0, 0.0)];

        let plans = precompute_transmission_plans(
            &trial_schedule(),
            RedundancyPolicy::Balanced,
            &health,
            RedundancyPolicyConfig::default(),
        );

        for plan in [&plans.small, &plans.bulk] {
            assert!(
                plan.iter().any(|transmission| transmission.path_id == 3
                    && transmission.packet_kind == PacketKind::Duplicate),
                "trial path missing from plan: {plan:?}"
            );
        }
    }

    #[test]
    fn down_trial_path_is_not_mirrored() {
        let mut trial_path = path(3, 30.0, 0.0);
        trial_path.interface_up = false;
        let health = vec![path(1, 20.0, 0.0), path(2, 60.0, 0.0), trial_path];

        let transmissions = build_transmission_plan_for_packet(
            &trial_schedule(),
            RedundancyPolicy::Fast,
            1_200,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert!(transmissions
            .iter()
            .all(|transmission| transmission.path_id != 3));
    }

    #[test]
    fn schedule_plan_without_trial_ids_still_deserialises() {
        let json = r#"{
            "mode": "anchor-duplicate-1",
            "anchor_path_id": 1,
            "data_path_ids": [1],
            "duplicate_path_ids": [2],
            "fec_path_ids": []
        }"#;

        let plan = serde_json::from_str::<SchedulePlan>(json).unwrap();

        assert!(plan.trial_path_ids.is_empty());
    }

    fn path(path_id: u16, rtt_ms: f64, late_rate: f64) -> PathHealthSnapshot {
        PathHealthSnapshot {
            path_id,
            name: format!("path-{path_id}"),
            interface_name: None,
            rtt_ms: Some(rtt_ms),
            jitter_ms: Some(5.0),
            loss_rate: 0.0,
            late_rate,
            queue_depth: 0,
            outbound_throughput_bps: 2_500_000,
            inbound_throughput_bps: 2_500_000,
            duplicate_inbound_throughput_bps: 0,
            raw_inbound_throughput_bps: 2_500_000,
            throughput_bps: 5_000_000,
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

    fn path_with_loss(path_id: u16, rtt_ms: f64, loss_rate: f64) -> PathHealthSnapshot {
        let mut path = path(path_id, rtt_ms, 0.0);
        path.loss_rate = loss_rate;
        path
    }

    fn path_with_jitter(path_id: u16, rtt_ms: f64, jitter_ms: f64) -> PathHealthSnapshot {
        let mut path = path(path_id, rtt_ms, 0.0);
        path.jitter_ms = Some(jitter_ms);
        path
    }

    fn path_with_queue_pressure(
        path_id: u16,
        rtt_ms: f64,
        queue_pressure: f64,
    ) -> PathHealthSnapshot {
        let mut path = path_with_loss(path_id, rtt_ms, 0.10);
        path.queue_pressure = queue_pressure;
        path
    }

    fn path_with_stale_ack(path_id: u16, rtt_ms: f64, stale_ack_ms: u64) -> PathHealthSnapshot {
        let mut path = path_with_loss(path_id, rtt_ms, 0.10);
        path.stale_ack_ms = Some(stale_ack_ms);
        path
    }

    #[test]
    fn anchor_fec_never_puts_bad_path_in_data_path() {
        let roles = select_path_roles(&[path(1, 20.0, 0.0), path(2, 900.0, 0.8)], 1);
        let plan = build_schedule(ScheduleMode::AnchorFec, &roles);

        assert_eq!(plan.anchor_path_id, Some(1));
        assert_eq!(plan.data_path_ids, vec![1]);
        assert!(!plan.data_path_ids.contains(&2));
    }

    #[test]
    fn duplicate_mode_keeps_anchor_as_immediate_data_path() {
        let roles = select_path_roles(&[path(1, 20.0, 0.0), path(2, 60.0, 0.0)], 1);
        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);

        assert_eq!(plan.data_path_ids, vec![1]);
        assert_eq!(plan.duplicate_path_ids, vec![2]);
    }

    #[test]
    fn duplicate_mode_serde_accepts_legacy_and_preferred_names() {
        #[derive(serde::Deserialize)]
        struct ModeWrapper {
            mode: ScheduleMode,
        }

        assert_eq!(
            toml::from_str::<ModeWrapper>("mode = \"anchor-duplicate-1\"")
                .unwrap()
                .mode,
            ScheduleMode::AnchorDuplicate1
        );
        assert_eq!(
            toml::from_str::<ModeWrapper>("mode = \"anchor-duplicate1\"")
                .unwrap()
                .mode,
            ScheduleMode::AnchorDuplicate1
        );
        assert_eq!(
            serde_json::to_string(&ScheduleMode::AnchorDuplicate1).unwrap(),
            "\"anchor-duplicate-1\""
        );
    }

    #[test]
    fn duplicate_transmission_plan_marks_anchor_data_and_backup_duplicate() {
        let plan = SchedulePlan {
            mode: ScheduleMode::AnchorDuplicate1,
            anchor_path_id: Some(1),
            data_path_ids: vec![1],
            duplicate_path_ids: vec![2],
            fec_path_ids: Vec::new(),
            trial_path_ids: Vec::new(),
        };

        let transmissions = build_transmission_plan(&plan);

        assert_eq!(
            transmissions,
            vec![
                ScheduledTransmission {
                    path_id: 1,
                    packet_kind: PacketKind::Data,
                },
                ScheduledTransmission {
                    path_id: 2,
                    packet_kind: PacketKind::Duplicate,
                },
            ]
        );
    }

    #[test]
    fn fec_transmission_plan_is_explicit_about_fec_paths() {
        let plan = SchedulePlan {
            mode: ScheduleMode::AnchorFec,
            anchor_path_id: Some(1),
            data_path_ids: vec![1],
            duplicate_path_ids: Vec::new(),
            fec_path_ids: vec![2, 3],
            trial_path_ids: Vec::new(),
        };

        let transmissions = build_transmission_plan(&plan);

        assert_eq!(transmissions[0].packet_kind, PacketKind::Data);
        assert_eq!(transmissions[1].packet_kind, PacketKind::Fec);
        assert_eq!(transmissions[2].packet_kind, PacketKind::Fec);
    }

    #[test]
    fn default_mode_is_two_link_duplicate() {
        assert_eq!(ScheduleMode::default(), ScheduleMode::AnchorDuplicate1);
    }

    #[test]
    fn balanced_policy_keeps_duplicate_for_small_packets() {
        let roles = select_path_roles(&[path(1, 20.0, 0.0), path(2, 60.0, 0.0)], 1);
        let health = roles
            .iter()
            .map(|role| role.path.clone())
            .collect::<Vec<_>>();
        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);

        let transmissions = build_transmission_plan_for_packet(
            &plan,
            RedundancyPolicy::Balanced,
            180,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert_eq!(
            transmissions
                .iter()
                .map(|transmission| transmission.packet_kind)
                .collect::<Vec<_>>(),
            vec![PacketKind::Data, PacketKind::Duplicate]
        );
    }

    #[test]
    fn balanced_policy_uses_anchor_only_for_healthy_bulk_packets() {
        let roles = select_path_roles(&[path(1, 20.0, 0.0), path(2, 60.0, 0.0)], 1);
        let health = roles
            .iter()
            .map(|role| role.path.clone())
            .collect::<Vec<_>>();
        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);

        let transmissions = build_transmission_plan_for_packet(
            &plan,
            RedundancyPolicy::Balanced,
            1_200,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert_eq!(
            transmissions
                .iter()
                .map(|transmission| transmission.packet_kind)
                .collect::<Vec<_>>(),
            vec![PacketKind::Data]
        );
    }

    #[test]
    fn balanced_policy_duplicates_bulk_when_anchor_has_loss() {
        let roles = select_path_roles(&[path_with_loss(1, 20.0, 0.03), path(2, 60.0, 0.0)], 1);
        let health = roles
            .iter()
            .map(|role| role.path.clone())
            .collect::<Vec<_>>();
        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);

        let transmissions = build_transmission_plan_for_packet(
            &plan,
            RedundancyPolicy::Balanced,
            1_200,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert!(transmissions
            .iter()
            .any(|transmission| transmission.packet_kind == PacketKind::Duplicate));
    }

    #[test]
    fn balanced_policy_skips_bad_backup_duplicates() {
        let roles = select_path_roles(
            &[path_with_loss(1, 20.0, 0.03), path_with_loss(2, 60.0, 0.5)],
            1,
        );
        let health = roles
            .iter()
            .map(|role| role.path.clone())
            .collect::<Vec<_>>();
        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);

        let transmissions = build_transmission_plan_for_packet(
            &plan,
            RedundancyPolicy::Balanced,
            1_200,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert_eq!(
            transmissions
                .iter()
                .map(|transmission| transmission.packet_kind)
                .collect::<Vec<_>>(),
            vec![PacketKind::Data]
        );
    }

    #[test]
    fn precomputed_plans_match_packet_specific_builder() {
        let roles = select_path_roles(&[path(1, 20.0, 0.0), path(2, 60.0, 0.0)], 1);
        let health = roles
            .iter()
            .map(|role| role.path.clone())
            .collect::<Vec<_>>();
        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let policy_config = RedundancyPolicyConfig::default();

        let precomputed = precompute_transmission_plans(
            &plan,
            RedundancyPolicy::Balanced,
            &health,
            policy_config,
        );

        assert_eq!(
            precomputed.for_packet_len(180, policy_config.interactive_packet_threshold_bytes),
            build_transmission_plan_for_packet(
                &plan,
                RedundancyPolicy::Balanced,
                180,
                &health,
                policy_config,
            )
        );
        assert_eq!(
            precomputed.for_packet_len(1_200, policy_config.interactive_packet_threshold_bytes),
            build_transmission_plan_for_packet(
                &plan,
                RedundancyPolicy::Balanced,
                1_200,
                &health,
                policy_config,
            )
        );
    }

    #[test]
    fn recovery_enters_after_all_usable_paths_are_degraded() {
        let config = RecoveryConfig::default();
        let health = vec![
            path_with_loss(1, 40.0, 0.10),
            path_with_jitter(2, 90.0, 90.0),
        ];
        let mut state = RecoveryState::default();

        for _ in 0..config.enter_degraded_ticks.saturating_sub(1) {
            let status =
                update_recovery_state(&mut state, RedundancyPolicy::Balanced, &health, config);
            assert!(!status.active);
        }

        let status = update_recovery_state(&mut state, RedundancyPolicy::Balanced, &health, config);
        assert!(status.active);
        assert_eq!(status.eligible_path_ids, vec![1, 2]);
    }

    #[test]
    fn recovery_stays_inactive_when_one_usable_path_is_clean() {
        let config = RecoveryConfig::default();
        let health = vec![path(1, 25.0, 0.0), path_with_loss(2, 90.0, 0.20)];
        let mut state = RecoveryState::default();

        for _ in 0..config.enter_degraded_ticks + 2 {
            let status =
                update_recovery_state(&mut state, RedundancyPolicy::Reliable, &health, config);
            assert!(!status.active);
        }
    }

    #[test]
    fn recovery_enters_when_all_usable_paths_have_degraded_rtt() {
        let config = RecoveryConfig::default();
        let health = vec![path(1, 190.0, 0.0), path(2, 240.0, 0.0)];
        let mut state = RecoveryState::default();

        for _ in 0..config.enter_degraded_ticks {
            update_recovery_state(&mut state, RedundancyPolicy::Balanced, &health, config);
        }

        assert!(state.active);
    }

    #[test]
    fn recovery_does_not_exit_while_a_candidate_clean_path_has_high_rtt() {
        let config = RecoveryConfig::default();
        let health = vec![path(1, 130.0, 0.0), path(2, 190.0, 0.0)];
        let mut state = RecoveryState {
            active: true,
            ..RecoveryState::default()
        };

        for _ in 0..config.exit_clean_ticks + 1 {
            update_recovery_state(&mut state, RedundancyPolicy::Balanced, &health, config);
        }

        assert!(state.active);
        assert_eq!(state.clean_ticks, 0);
    }

    #[test]
    fn fast_policy_does_not_enter_recovery() {
        let config = RecoveryConfig::default();
        let health = vec![path_with_loss(1, 40.0, 0.20), path_with_loss(2, 90.0, 0.20)];
        let mut state = RecoveryState::default();

        for _ in 0..config.enter_degraded_ticks + 2 {
            let status = update_recovery_state(&mut state, RedundancyPolicy::Fast, &health, config);
            assert!(!status.active);
        }
    }

    #[test]
    fn recovery_expands_schedule_and_duplicates_bulk() {
        let config = RecoveryConfig::default();
        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                path_with_loss(2, 90.0, 0.12),
                path_with_loss(3, 100.0, 0.14),
            ],
            1,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);
        let health = roles
            .iter()
            .map(|role| role.path.clone())
            .collect::<Vec<_>>();
        let plans = precompute_transmission_plans(
            &expanded,
            RedundancyPolicy::Reliable,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert_eq!(expanded.data_path_ids.len(), 1);
        assert_eq!(expanded.duplicate_path_ids.len(), 2);
        assert_eq!(
            plans
                .bulk
                .iter()
                .map(|transmission| transmission.packet_kind)
                .collect::<Vec<_>>(),
            vec![
                PacketKind::Data,
                PacketKind::Duplicate,
                PacketKind::Duplicate
            ]
        );
    }

    #[test]
    fn recovery_excludes_nearly_dead_or_hard_failed_paths() {
        let config = RecoveryConfig::default();
        let mut repeated_failure = path_with_loss(3, 120.0, 0.10);
        repeated_failure.send_failure_streak = 2;
        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                path_with_loss(2, 90.0, 0.96),
                repeated_failure,
                path_with_loss(4, 100.0, 0.14),
            ],
            1,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);

        assert!(!expanded.duplicate_path_ids.contains(&2));
        assert!(!expanded.duplicate_path_ids.contains(&3));
        assert_eq!(expanded.duplicate_path_ids, vec![4]);
    }

    #[test]
    fn recovery_prunes_harmful_duplicate_when_healthier_backup_exists() {
        let config = RecoveryConfig::default();
        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                path_with_queue_pressure(2, 90.0, 0.99),
                path_with_loss(3, 100.0, 0.14),
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);

        assert_eq!(expanded.duplicate_path_ids, vec![3]);
    }

    #[test]
    fn recovery_keeps_idle_probe_with_high_collapse_score() {
        let config = RecoveryConfig::default();
        let mut idle_probe = path_with_loss(3, 100.0, 0.14);
        idle_probe.throughput_collapse_score = 1.0;
        idle_probe.outbound_throughput_bps = 0;
        idle_probe.inbound_throughput_bps = 0;
        idle_probe.duplicate_inbound_throughput_bps = 0;
        idle_probe.raw_inbound_throughput_bps = 0;
        idle_probe.throughput_bps = 0;

        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                path_with_loss(2, 90.0, 0.12),
                idle_probe,
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);

        assert_eq!(expanded.duplicate_path_ids, vec![2, 3]);
    }

    #[test]
    fn recovery_keeps_live_path_when_only_aggregate_throughput_has_collapsed() {
        let config = RecoveryConfig::default();
        let mut active_collapsed = path_with_loss(2, 90.0, 0.12);
        active_collapsed.throughput_collapse_score = 1.0;
        active_collapsed.throughput_bps = 1_000_000;

        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                active_collapsed,
                path_with_loss(3, 100.0, 0.14),
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);

        assert_eq!(expanded.duplicate_path_ids, vec![3, 2]);
    }

    #[test]
    fn recovery_uses_anchor_only_when_all_duplicates_are_harmful() {
        let config = RecoveryConfig::default();
        let mut pressured = path_with_queue_pressure(2, 90.0, 0.99);
        pressured.duplicate_usefulness = 0.0;
        pressured.duplicate_inbound_throughput_bps = 0;
        let mut stale = path_with_stale_ack(3, 100.0, 4_000);
        stale.duplicate_usefulness = 0.0;
        stale.duplicate_inbound_throughput_bps = 0;
        let roles = select_path_roles(&[path_with_loss(1, 40.0, 0.10), pressured, stale], 2);
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);

        assert!(expanded.duplicate_path_ids.is_empty());
    }

    #[test]
    fn recovery_keeps_a_harmful_looking_backup_when_recent_duplicates_helped() {
        let config = RecoveryConfig::default();
        let mut useful_backup = path_with_queue_pressure(2, 90.0, 0.99);
        useful_backup.duplicate_usefulness = 0.25;
        useful_backup.duplicate_inbound_throughput_bps = 80_000;
        let mut stale_and_unhelpful = path_with_stale_ack(3, 100.0, 4_000);
        stale_and_unhelpful.duplicate_usefulness = 0.0;
        stale_and_unhelpful.duplicate_inbound_throughput_bps = 0;
        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                useful_backup,
                stale_and_unhelpful,
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);

        assert_eq!(expanded.duplicate_path_ids, vec![2]);
    }

    #[test]
    fn unmeasured_path_is_not_clean_and_cannot_force_recovery_exit() {
        let config = RecoveryConfig {
            exit_clean_ticks: 1,
            ..RecoveryConfig::default()
        };
        let mut state = RecoveryState {
            active: true,
            ..RecoveryState::default()
        };
        let mut unmeasured = path(2, 80.0, 0.0);
        unmeasured.rtt_ms = None;
        unmeasured.stale_ack_ms = Some(100);
        let degraded = path_with_loss(1, 220.0, 0.20);

        let status = update_recovery_state(
            &mut state,
            RedundancyPolicy::Balanced,
            &[degraded, unmeasured],
            config,
        );

        assert!(status.active);
        assert_eq!(status.clean_ticks, 0);
    }

    #[test]
    fn unmeasured_path_becomes_degraded_after_ack_grace_expires() {
        let config = RecoveryConfig {
            enter_degraded_ticks: 1,
            ..RecoveryConfig::default()
        };
        let mut state = RecoveryState::default();
        let mut unmeasured = path(2, 80.0, 0.0);
        unmeasured.rtt_ms = None;
        unmeasured.stale_ack_ms = Some(config.degraded_stale_ack_ms);
        let degraded = path_with_loss(1, 220.0, 0.20);

        let status = update_recovery_state(
            &mut state,
            RedundancyPolicy::Balanced,
            &[degraded, unmeasured],
            config,
        );

        assert!(status.active);
    }

    #[test]
    fn one_soft_heartbeat_loss_round_does_not_enter_recovery() {
        let config = RecoveryConfig::default();
        let mut state = RecoveryState::default();
        let mut paths = [
            path_with_loss(1, 50.0, 0.125),
            path_with_loss(2, 70.0, 0.125),
        ];
        paths[0].heartbeat_expired = 1;
        paths[1].heartbeat_expired = 1;

        let first = update_recovery_state(&mut state, RedundancyPolicy::Balanced, &paths, config);
        let second = update_recovery_state(&mut state, RedundancyPolicy::Balanced, &paths, config);

        assert!(!first.active);
        assert_eq!(first.soft_loss_ticks, 1);
        assert!(!second.active);
        assert_eq!(second.soft_loss_ticks, 0);
        assert!(second.reason.contains("no new misses"));
    }

    #[test]
    fn two_soft_heartbeat_loss_rounds_enter_recovery() {
        let config = RecoveryConfig::default();
        let mut state = RecoveryState::default();
        let mut paths = [
            path_with_loss(1, 50.0, 0.125),
            path_with_loss(2, 70.0, 0.125),
        ];
        paths[0].heartbeat_expired = 1;
        paths[1].heartbeat_expired = 1;

        let first = update_recovery_state(&mut state, RedundancyPolicy::Balanced, &paths, config);
        paths[0].heartbeat_expired = 2;
        paths[1].heartbeat_expired = 2;
        let second = update_recovery_state(&mut state, RedundancyPolicy::Balanced, &paths, config);

        assert!(!first.active);
        assert!(second.active);
        assert!(second.reason.contains("persisted"));
    }

    #[test]
    fn hard_path_failure_is_demoted_immediately() {
        let mut failed = path(1, 40.0, 0.0);
        failed.interface_up = false;

        assert_eq!(
            failed.hard_demotion_reason(),
            Some("No carrier or interface is down.")
        );
        assert!(!failed.is_realtime_eligible());
        assert_eq!(failed.score(), -1_000_000.0);
    }

    #[test]
    fn recovery_keeps_a_harmful_backup_only_when_duplicates_still_help() {
        let config = RecoveryConfig::default();
        let mut useful_but_pressured = path_with_queue_pressure(2, 90.0, 0.99);
        useful_but_pressured.duplicate_usefulness = 0.4;
        useful_but_pressured.duplicate_inbound_throughput_bps = 100_000;
        let mut stale_and_unhelpful = path_with_stale_ack(3, 100.0, 4_000);
        stale_and_unhelpful.duplicate_usefulness = 0.0;
        stale_and_unhelpful.duplicate_inbound_throughput_bps = 0;
        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                useful_but_pressured,
                stale_and_unhelpful,
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);

        assert_eq!(expanded.duplicate_path_ids, vec![2]);
    }

    #[test]
    fn schedule_control_message_defaults_legacy_recovery_flag_to_false() {
        let json = r#"{
            "schedule": {
                "mode": "anchor-duplicate-1",
                "anchor_path_id": 1,
                "data_path_ids": [1],
                "duplicate_path_ids": [2],
                "fec_path_ids": []
            },
            "redundancy_policy": "balanced",
            "paths": []
        }"#;

        let message = serde_json::from_str::<ScheduleControlMessage>(json).unwrap();

        assert!(!message.recovery_active);
    }

    #[test]
    fn schedule_control_message_accepts_recovery_active_flag() {
        let json = r#"{
            "schedule": {
                "mode": "anchor-duplicate-1",
                "anchor_path_id": 1,
                "data_path_ids": [1],
                "duplicate_path_ids": [2],
                "fec_path_ids": []
            },
            "recovery_active": true
        }"#;

        let message = serde_json::from_str::<ScheduleControlMessage>(json).unwrap();

        assert!(message.recovery_active);
    }

    #[test]
    fn recovery_schedule_hysteresis_keeps_duplicate_order_across_minor_score_changes() {
        let config = RecoveryConfig::default();
        let recovery_status = RecoveryStatus {
            active: true,
            eligible_path_ids: vec![1, 2, 3],
            ..RecoveryStatus::default()
        };
        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                path_with_loss(2, 90.0, 0.12),
                path_with_loss(3, 100.0, 0.14),
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);
        let mut state = RecoveryScheduleStabilityState::default();

        let stable =
            stabilize_recovery_schedule(&mut state, &expanded, &roles, &recovery_status, config, 5);
        assert_eq!(stable.duplicate_path_ids, vec![2, 3]);

        let roles_with_minor_reorder = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                path_with_loss(2, 95.0, 0.12),
                path_with_loss(3, 80.0, 0.14),
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles_with_minor_reorder);
        let expanded = expand_schedule_for_recovery(&base, &roles_with_minor_reorder, config);

        let stable = stabilize_recovery_schedule(
            &mut state,
            &expanded,
            &roles_with_minor_reorder,
            &recovery_status,
            config,
            5,
        );

        assert_eq!(expanded.duplicate_path_ids, vec![3, 2]);
        assert_eq!(stable.duplicate_path_ids, vec![2, 3]);
    }

    #[test]
    fn recovery_schedule_hysteresis_refreshes_immediately_on_hard_demotion() {
        let config = RecoveryConfig::default();
        let recovery_status = RecoveryStatus {
            active: true,
            eligible_path_ids: vec![1, 2, 3],
            ..RecoveryStatus::default()
        };
        let roles = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                path_with_loss(2, 90.0, 0.12),
                path_with_loss(3, 100.0, 0.14),
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);
        let expanded = expand_schedule_for_recovery(&base, &roles, config);
        let mut state = RecoveryScheduleStabilityState::default();
        let stable =
            stabilize_recovery_schedule(&mut state, &expanded, &roles, &recovery_status, config, 5);
        assert_eq!(stable.duplicate_path_ids, vec![2, 3]);

        let mut hard_failed = path_with_loss(2, 90.0, 0.12);
        hard_failed.send_failure_streak = 2;
        let roles_with_failure = select_path_roles(
            &[
                path_with_loss(1, 40.0, 0.10),
                hard_failed,
                path_with_loss(3, 100.0, 0.14),
            ],
            2,
        );
        let base = build_schedule(ScheduleMode::AnchorDuplicate1, &roles_with_failure);
        let expanded = expand_schedule_for_recovery(&base, &roles_with_failure, config);

        let stable = stabilize_recovery_schedule(
            &mut state,
            &expanded,
            &roles_with_failure,
            &recovery_status,
            config,
            5,
        );

        assert_eq!(stable.duplicate_path_ids, vec![3]);
    }

    #[test]
    fn recovery_exits_after_clean_path_hysteresis() {
        let config = RecoveryConfig::default();
        let mut degraded = vec![path_with_loss(1, 40.0, 0.10), path_with_loss(2, 90.0, 0.20)];
        let clean = vec![path(1, 25.0, 0.0), path_with_loss(2, 90.0, 0.20)];
        let mut state = RecoveryState::default();

        for round in 0..config.enter_degraded_ticks {
            degraded[0].heartbeat_expired = u64::from(round) + 1;
            degraded[1].heartbeat_expired = u64::from(round) + 1;
            update_recovery_state(&mut state, RedundancyPolicy::Balanced, &degraded, config);
        }
        assert!(state.active);

        for _ in 0..config.exit_clean_ticks.saturating_sub(1) {
            let status =
                update_recovery_state(&mut state, RedundancyPolicy::Balanced, &clean, config);
            assert!(status.active);
        }

        let status = update_recovery_state(&mut state, RedundancyPolicy::Balanced, &clean, config);
        assert!(!status.active);
    }
}
