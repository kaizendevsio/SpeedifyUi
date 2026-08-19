use serde::{Deserialize, Serialize};

use crate::anchor::{AnchorTrialConfig, FlapDampingConfig, ScoreSmoothingConfig, StabilityConfig};
use crate::health::RoleSelectionConfig;
use crate::scheduler::{RecoveryConfig, RedundancyPolicy, ScheduleMode};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ClientConfig {
    pub enabled: bool,
    pub server_addr: String,
    pub mode: ScheduleMode,
    #[serde(default)]
    pub redundancy_policy: RedundancyPolicy,
    pub max_active_backups: usize,
    pub realtime_deadline_ms: u64,
    #[serde(default = "default_interactive_packet_threshold_bytes")]
    pub interactive_packet_threshold_bytes: usize,
    #[serde(default = "default_duplicate_loss_threshold")]
    pub duplicate_loss_threshold: f64,
    #[serde(default = "default_backup_loss_disable_threshold")]
    pub backup_loss_disable_threshold: f64,
    #[serde(default = "default_reorder_hold_ms")]
    pub reorder_hold_ms: u64,
    #[serde(default = "default_tun_queue_capacity")]
    pub tun_queue_capacity: usize,
    #[serde(default = "default_inbound_queue_capacity")]
    pub inbound_queue_capacity: usize,
    #[serde(default = "default_udp_socket_buffer_bytes")]
    pub udp_socket_buffer_bytes: usize,
    #[serde(default = "default_udp_receive_batch_size")]
    pub udp_receive_batch_size: usize,
    #[serde(default = "default_heartbeat_interval_ms")]
    pub heartbeat_interval_ms: u64,
    #[serde(default = "default_heartbeat_health_window_samples")]
    pub heartbeat_health_window_samples: usize,
    #[serde(default = "default_heartbeat_min_quality_samples")]
    pub heartbeat_min_quality_samples: usize,
    #[serde(default = "default_heartbeat_failure_consecutive")]
    pub heartbeat_failure_consecutive: u32,
    #[serde(default = "default_heartbeat_recovery_consecutive")]
    pub heartbeat_recovery_consecutive: u32,
    #[serde(default)]
    pub silent_blackhole_probe_targets: Vec<String>,
    #[serde(default = "default_recovery_enabled")]
    pub recovery_enabled: bool,
    #[serde(default = "default_recovery_enter_degraded_ticks")]
    pub recovery_enter_degraded_ticks: u32,
    #[serde(default = "default_recovery_exit_clean_ticks")]
    pub recovery_exit_clean_ticks: u32,
    #[serde(default = "default_recovery_degraded_rtt_ms")]
    pub recovery_degraded_rtt_ms: f64,
    #[serde(default = "default_recovery_degraded_loss_threshold")]
    pub recovery_degraded_loss_threshold: f64,
    #[serde(default = "default_recovery_degraded_late_threshold")]
    pub recovery_degraded_late_threshold: f64,
    #[serde(default = "default_recovery_degraded_jitter_ms")]
    pub recovery_degraded_jitter_ms: f64,
    #[serde(default = "default_recovery_degraded_stale_ack_ms")]
    pub recovery_degraded_stale_ack_ms: u64,
    #[serde(default = "default_recovery_degraded_queue_pressure")]
    pub recovery_degraded_queue_pressure: f64,
    #[serde(default = "default_recovery_clean_rtt_ms")]
    pub recovery_clean_rtt_ms: f64,
    #[serde(default = "default_recovery_clean_loss_threshold")]
    pub recovery_clean_loss_threshold: f64,
    #[serde(default = "default_recovery_clean_late_threshold")]
    pub recovery_clean_late_threshold: f64,
    #[serde(default = "default_recovery_clean_jitter_ms")]
    pub recovery_clean_jitter_ms: f64,
    #[serde(default = "default_recovery_clean_stale_ack_ms")]
    pub recovery_clean_stale_ack_ms: u64,
    #[serde(default = "default_recovery_clean_queue_pressure")]
    pub recovery_clean_queue_pressure: f64,
    #[serde(default = "default_recovery_path_loss_exclude_threshold")]
    pub recovery_path_loss_exclude_threshold: f64,
    #[serde(default)]
    pub role_selection: RoleSelectionSettings,
    pub runtime_status_path: Option<String>,
    pub paths: Vec<PathConfig>,
}

impl Default for ClientConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            server_addr: "127.0.0.1:8444".to_string(),
            mode: ScheduleMode::AnchorDuplicate1,
            redundancy_policy: RedundancyPolicy::Balanced,
            max_active_backups: 1,
            realtime_deadline_ms: 500,
            interactive_packet_threshold_bytes: default_interactive_packet_threshold_bytes(),
            duplicate_loss_threshold: default_duplicate_loss_threshold(),
            backup_loss_disable_threshold: default_backup_loss_disable_threshold(),
            reorder_hold_ms: default_reorder_hold_ms(),
            tun_queue_capacity: default_tun_queue_capacity(),
            inbound_queue_capacity: default_inbound_queue_capacity(),
            udp_socket_buffer_bytes: default_udp_socket_buffer_bytes(),
            udp_receive_batch_size: default_udp_receive_batch_size(),
            heartbeat_interval_ms: default_heartbeat_interval_ms(),
            heartbeat_health_window_samples: default_heartbeat_health_window_samples(),
            heartbeat_min_quality_samples: default_heartbeat_min_quality_samples(),
            heartbeat_failure_consecutive: default_heartbeat_failure_consecutive(),
            heartbeat_recovery_consecutive: default_heartbeat_recovery_consecutive(),
            silent_blackhole_probe_targets: Vec::new(),
            recovery_enabled: default_recovery_enabled(),
            recovery_enter_degraded_ticks: default_recovery_enter_degraded_ticks(),
            recovery_exit_clean_ticks: default_recovery_exit_clean_ticks(),
            recovery_degraded_rtt_ms: default_recovery_degraded_rtt_ms(),
            recovery_degraded_loss_threshold: default_recovery_degraded_loss_threshold(),
            recovery_degraded_late_threshold: default_recovery_degraded_late_threshold(),
            recovery_degraded_jitter_ms: default_recovery_degraded_jitter_ms(),
            recovery_degraded_stale_ack_ms: default_recovery_degraded_stale_ack_ms(),
            recovery_degraded_queue_pressure: default_recovery_degraded_queue_pressure(),
            recovery_clean_rtt_ms: default_recovery_clean_rtt_ms(),
            recovery_clean_loss_threshold: default_recovery_clean_loss_threshold(),
            recovery_clean_late_threshold: default_recovery_clean_late_threshold(),
            recovery_clean_jitter_ms: default_recovery_clean_jitter_ms(),
            recovery_clean_stale_ack_ms: default_recovery_clean_stale_ack_ms(),
            recovery_clean_queue_pressure: default_recovery_clean_queue_pressure(),
            recovery_path_loss_exclude_threshold: default_recovery_path_loss_exclude_threshold(),
            role_selection: RoleSelectionSettings::default(),
            runtime_status_path: Some("/run/xbond/client-status.json".to_string()),
            paths: Vec::new(),
        }
    }
}

impl ClientConfig {
    pub fn recovery_config(&self) -> RecoveryConfig {
        RecoveryConfig {
            enabled: self.recovery_enabled,
            enter_degraded_ticks: self.recovery_enter_degraded_ticks.max(1),
            exit_clean_ticks: self.recovery_exit_clean_ticks.max(1),
            degraded_rtt_ms: self.recovery_degraded_rtt_ms.max(0.0),
            degraded_loss_threshold: self.recovery_degraded_loss_threshold.clamp(0.0, 1.0),
            degraded_late_threshold: self.recovery_degraded_late_threshold.clamp(0.0, 1.0),
            degraded_jitter_ms: self.recovery_degraded_jitter_ms.max(0.0),
            degraded_stale_ack_ms: self.recovery_degraded_stale_ack_ms,
            degraded_queue_pressure: self.recovery_degraded_queue_pressure.clamp(0.0, 1.0),
            clean_rtt_ms: self.recovery_clean_rtt_ms.max(0.0),
            clean_loss_threshold: self.recovery_clean_loss_threshold.clamp(0.0, 1.0),
            clean_late_threshold: self.recovery_clean_late_threshold.clamp(0.0, 1.0),
            clean_jitter_ms: self.recovery_clean_jitter_ms.max(0.0),
            clean_stale_ack_ms: self.recovery_clean_stale_ack_ms,
            clean_queue_pressure: self.recovery_clean_queue_pressure.clamp(0.0, 1.0),
            path_loss_exclude_threshold: self.recovery_path_loss_exclude_threshold.clamp(0.0, 1.0),
        }
    }

    pub fn role_selection_config(&self) -> RoleSelectionConfig {
        let settings = self.role_selection;
        let trial_ticks = settings.trial_ticks.max(1);
        RoleSelectionConfig {
            anchor_switch_score_margin: settings.anchor_switch_score_margin.max(0.0),
            backup_switch_score_margin: settings.backup_switch_score_margin.max(0.0),
            stable_ticks_required: settings.stable_ticks_required.max(1),
            latency_advantage_ms: settings.latency_advantage_ms.max(0.0),
            latency_stable_ticks: settings.latency_stable_ticks,
            stability: StabilityConfig {
                window_ticks: settings.stability_window_ticks.max(1),
                latency_deviation_penalty_per_ms: settings
                    .stability_latency_deviation_penalty_per_ms
                    .max(0.0),
                loss_deviation_penalty_weight: settings
                    .stability_loss_deviation_penalty_weight
                    .max(0.0),
                unproven_penalty: settings.stability_unproven_penalty.max(0.0),
                penalty_cap: settings.stability_penalty_cap.max(0.0),
            },
            smoothing: ScoreSmoothingConfig {
                alpha: settings.smoothing_alpha.clamp(0.01, 1.0),
                variance_penalty_weight: settings.variance_penalty_weight.max(0.0),
            },
            flap: FlapDampingConfig {
                penalty_demoted: settings.flap_penalty_demoted.max(0.0),
                penalty_trial_failed: settings.flap_penalty_trial_failed.max(0.0),
                suppress_threshold: settings.flap_suppress_threshold.max(0.0),
                penalty_cap: settings.flap_penalty_cap.max(0.0),
                half_life_secs: settings.flap_half_life_secs.max(1),
                transient_grace_ticks: settings.flap_transient_grace_ticks,
            },
            trial: AnchorTrialConfig {
                enabled: settings.trial_enabled,
                ticks: trial_ticks,
                success_ticks: settings.trial_success_ticks.min(trial_ticks),
                min_bytes: settings.trial_min_bytes,
                min_interval_ticks: settings.trial_min_interval_ticks,
                latency_margin_ms: settings.trial_latency_margin_ms.max(0.0),
            },
        }
    }
}

/// Operator-facing `[role_selection]` knobs. Every field is defaulted, so an existing
/// config file without the table keeps working and picks up the new sticky defaults.
#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct RoleSelectionSettings {
    #[serde(default = "default_anchor_switch_score_margin")]
    pub anchor_switch_score_margin: f64,
    #[serde(default = "default_backup_switch_score_margin")]
    pub backup_switch_score_margin: f64,
    #[serde(default = "default_stable_ticks_required")]
    pub stable_ticks_required: u8,
    #[serde(default = "default_latency_advantage_ms")]
    pub latency_advantage_ms: f64,
    #[serde(default = "default_latency_stable_ticks")]
    pub latency_stable_ticks: u32,
    #[serde(default = "default_trial_latency_margin_ms")]
    pub trial_latency_margin_ms: f64,
    #[serde(default = "default_stability_window_ticks")]
    pub stability_window_ticks: u32,
    #[serde(default = "default_stability_latency_deviation_penalty_per_ms")]
    pub stability_latency_deviation_penalty_per_ms: f64,
    #[serde(default = "default_stability_loss_deviation_penalty_weight")]
    pub stability_loss_deviation_penalty_weight: f64,
    #[serde(default = "default_stability_unproven_penalty")]
    pub stability_unproven_penalty: f64,
    #[serde(default = "default_stability_penalty_cap")]
    pub stability_penalty_cap: f64,
    #[serde(default = "default_smoothing_alpha")]
    pub smoothing_alpha: f64,
    #[serde(default = "default_variance_penalty_weight")]
    pub variance_penalty_weight: f64,
    #[serde(default = "default_flap_penalty_demoted")]
    pub flap_penalty_demoted: f64,
    #[serde(default = "default_flap_penalty_trial_failed")]
    pub flap_penalty_trial_failed: f64,
    #[serde(default = "default_flap_suppress_threshold")]
    pub flap_suppress_threshold: f64,
    #[serde(default = "default_flap_penalty_cap")]
    pub flap_penalty_cap: f64,
    #[serde(default = "default_flap_half_life_secs")]
    pub flap_half_life_secs: u64,
    #[serde(default = "default_flap_transient_grace_ticks")]
    pub flap_transient_grace_ticks: u32,
    #[serde(default = "default_trial_enabled")]
    pub trial_enabled: bool,
    #[serde(default = "default_trial_ticks")]
    pub trial_ticks: u32,
    #[serde(default = "default_trial_success_ticks")]
    pub trial_success_ticks: u32,
    #[serde(default = "default_trial_min_bytes")]
    pub trial_min_bytes: u64,
    #[serde(default = "default_trial_min_interval_ticks")]
    pub trial_min_interval_ticks: u32,
}

impl Default for RoleSelectionSettings {
    fn default() -> Self {
        Self {
            anchor_switch_score_margin: default_anchor_switch_score_margin(),
            backup_switch_score_margin: default_backup_switch_score_margin(),
            stable_ticks_required: default_stable_ticks_required(),
            latency_advantage_ms: default_latency_advantage_ms(),
            latency_stable_ticks: default_latency_stable_ticks(),
            trial_latency_margin_ms: default_trial_latency_margin_ms(),
            stability_window_ticks: default_stability_window_ticks(),
            stability_latency_deviation_penalty_per_ms:
                default_stability_latency_deviation_penalty_per_ms(),
            stability_loss_deviation_penalty_weight:
                default_stability_loss_deviation_penalty_weight(),
            stability_unproven_penalty: default_stability_unproven_penalty(),
            stability_penalty_cap: default_stability_penalty_cap(),
            smoothing_alpha: default_smoothing_alpha(),
            variance_penalty_weight: default_variance_penalty_weight(),
            flap_penalty_demoted: default_flap_penalty_demoted(),
            flap_penalty_trial_failed: default_flap_penalty_trial_failed(),
            flap_suppress_threshold: default_flap_suppress_threshold(),
            flap_penalty_cap: default_flap_penalty_cap(),
            flap_half_life_secs: default_flap_half_life_secs(),
            flap_transient_grace_ticks: default_flap_transient_grace_ticks(),
            trial_enabled: default_trial_enabled(),
            trial_ticks: default_trial_ticks(),
            trial_success_ticks: default_trial_success_ticks(),
            trial_min_bytes: default_trial_min_bytes(),
            trial_min_interval_ticks: default_trial_min_interval_ticks(),
        }
    }
}

fn default_anchor_switch_score_margin() -> f64 {
    200.0
}

fn default_backup_switch_score_margin() -> f64 {
    100.0
}

fn default_stable_ticks_required() -> u8 {
    10
}

fn default_latency_advantage_ms() -> f64 {
    15.0
}

fn default_latency_stable_ticks() -> u32 {
    30
}

fn default_trial_latency_margin_ms() -> f64 {
    10.0
}

fn default_stability_window_ticks() -> u32 {
    30
}

fn default_stability_latency_deviation_penalty_per_ms() -> f64 {
    3.0
}

fn default_stability_loss_deviation_penalty_weight() -> f64 {
    1_500.0
}

fn default_stability_unproven_penalty() -> f64 {
    60.0
}

fn default_stability_penalty_cap() -> f64 {
    250.0
}

fn default_smoothing_alpha() -> f64 {
    0.2
}

fn default_variance_penalty_weight() -> f64 {
    2.0
}

fn default_flap_penalty_demoted() -> f64 {
    1_000.0
}

fn default_flap_penalty_trial_failed() -> f64 {
    600.0
}

fn default_flap_suppress_threshold() -> f64 {
    500.0
}

fn default_flap_penalty_cap() -> f64 {
    4_000.0
}

fn default_flap_half_life_secs() -> u64 {
    300
}

fn default_flap_transient_grace_ticks() -> u32 {
    5
}

fn default_trial_enabled() -> bool {
    true
}

fn default_trial_ticks() -> u32 {
    20
}

fn default_trial_success_ticks() -> u32 {
    15
}

fn default_trial_min_bytes() -> u64 {
    5_000_000
}

fn default_trial_min_interval_ticks() -> u32 {
    60
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PathConfig {
    pub id: u16,
    pub name: String,
    pub interface_name: Option<String>,
    pub bind_addr: Option<String>,
    pub enabled: bool,
}

fn default_interactive_packet_threshold_bytes() -> usize {
    768
}

fn default_duplicate_loss_threshold() -> f64 {
    0.02
}

fn default_backup_loss_disable_threshold() -> f64 {
    0.35
}

fn default_reorder_hold_ms() -> u64 {
    25
}

pub fn default_tun_queue_capacity() -> usize {
    2048
}

pub fn default_inbound_queue_capacity() -> usize {
    4096
}

pub fn default_udp_socket_buffer_bytes() -> usize {
    8 * 1024 * 1024
}

pub fn default_udp_receive_batch_size() -> usize {
    32
}

pub fn default_heartbeat_interval_ms() -> u64 {
    200
}

pub fn default_heartbeat_health_window_samples() -> usize {
    100
}

pub fn default_heartbeat_min_quality_samples() -> usize {
    20
}

pub fn default_heartbeat_failure_consecutive() -> u32 {
    4
}

pub fn default_heartbeat_recovery_consecutive() -> u32 {
    15
}

fn default_recovery_enabled() -> bool {
    true
}

fn default_recovery_enter_degraded_ticks() -> u32 {
    3
}

fn default_recovery_exit_clean_ticks() -> u32 {
    20
}

fn default_recovery_degraded_rtt_ms() -> f64 {
    180.0
}

fn default_recovery_degraded_loss_threshold() -> f64 {
    0.08
}

fn default_recovery_degraded_late_threshold() -> f64 {
    0.03
}

fn default_recovery_degraded_jitter_ms() -> f64 {
    60.0
}

fn default_recovery_degraded_stale_ack_ms() -> u64 {
    1_500
}

fn default_recovery_degraded_queue_pressure() -> f64 {
    0.70
}

fn default_recovery_clean_rtt_ms() -> f64 {
    120.0
}

fn default_recovery_clean_loss_threshold() -> f64 {
    0.02
}

fn default_recovery_clean_late_threshold() -> f64 {
    0.01
}

fn default_recovery_clean_jitter_ms() -> f64 {
    40.0
}

fn default_recovery_clean_stale_ack_ms() -> u64 {
    1_000
}

fn default_recovery_clean_queue_pressure() -> f64 {
    0.50
}

fn default_recovery_path_loss_exclude_threshold() -> f64 {
    0.95
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_role_selection_table_uses_sticky_defaults() {
        let config = ClientConfig::default();
        let role_selection = config.role_selection_config();

        assert_eq!(role_selection.anchor_switch_score_margin, 200.0);
        assert_eq!(role_selection.stable_ticks_required, 10);
        assert!(role_selection.trial.enabled);
        assert_eq!(role_selection.trial.ticks, 20);
        assert_eq!(role_selection.latency_advantage_ms, 15.0);
        assert_eq!(role_selection.latency_stable_ticks, 30);
        assert_eq!(role_selection.trial.latency_margin_ms, 10.0);
    }

    #[test]
    fn latency_preference_keys_override_defaults() {
        let toml = r#"
enabled = true
server_addr = "127.0.0.1:8444"
mode = "anchor-duplicate-1"
max_active_backups = 1
realtime_deadline_ms = 500
paths = []

[role_selection]
latency_advantage_ms = 25.0
latency_stable_ticks = 45
trial_latency_margin_ms = 5.0
"#;

        let config = toml::from_str::<ClientConfig>(toml).unwrap();
        let role_selection = config.role_selection_config();

        assert_eq!(role_selection.latency_advantage_ms, 25.0);
        assert_eq!(role_selection.latency_stable_ticks, 45);
        assert_eq!(role_selection.trial.latency_margin_ms, 5.0);
        // latency_stable_ticks = 0 must stay 0: it is the documented off switch.
        let disabled = ClientConfig {
            role_selection: RoleSelectionSettings {
                latency_stable_ticks: 0,
                ..RoleSelectionSettings::default()
            },
            ..ClientConfig::default()
        }
        .role_selection_config();
        assert_eq!(disabled.latency_stable_ticks, 0);
    }

    #[test]
    fn role_selection_table_overrides_defaults() {
        let toml = r#"
enabled = true
server_addr = "127.0.0.1:8444"
mode = "anchor-duplicate-1"
max_active_backups = 1
realtime_deadline_ms = 500
paths = []

[role_selection]
anchor_switch_score_margin = 350.0
stable_ticks_required = 20
trial_enabled = false
trial_ticks = 45
flap_half_life_secs = 600
"#;

        let config = toml::from_str::<ClientConfig>(toml).unwrap();
        let role_selection = config.role_selection_config();

        assert_eq!(role_selection.anchor_switch_score_margin, 350.0);
        assert_eq!(role_selection.stable_ticks_required, 20);
        assert!(!role_selection.trial.enabled);
        assert_eq!(role_selection.trial.ticks, 45);
        assert_eq!(role_selection.flap.half_life_secs, 600);
        // Unspecified keys keep their defaults.
        assert_eq!(role_selection.smoothing.alpha, 0.2);
    }

    #[test]
    fn role_selection_values_are_clamped_to_sane_ranges() {
        let settings = RoleSelectionSettings {
            smoothing_alpha: 9.0,
            stable_ticks_required: 0,
            trial_success_ticks: 999,
            trial_ticks: 10,
            ..RoleSelectionSettings::default()
        };
        let config = ClientConfig {
            role_selection: settings,
            ..ClientConfig::default()
        }
        .role_selection_config();

        assert_eq!(config.smoothing.alpha, 1.0);
        assert_eq!(config.stable_ticks_required, 1);
        // success_ticks can never exceed the window length.
        assert_eq!(config.trial.effective_success_ticks(), 10);
    }
}
