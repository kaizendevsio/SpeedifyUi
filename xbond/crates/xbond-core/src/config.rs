use serde::{Deserialize, Serialize};

use crate::scheduler::{RecoveryConfig, RedundancyPolicy, ScheduleMode};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ClientConfig {
    pub enabled: bool,
    pub session_id: u64,
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
    #[serde(default = "default_recovery_enabled")]
    pub recovery_enabled: bool,
    #[serde(default = "default_recovery_enter_degraded_ticks")]
    pub recovery_enter_degraded_ticks: u32,
    #[serde(default = "default_recovery_exit_clean_ticks")]
    pub recovery_exit_clean_ticks: u32,
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
    pub runtime_status_path: Option<String>,
    pub paths: Vec<PathConfig>,
}

impl Default for ClientConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            session_id: 1,
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
            recovery_enabled: default_recovery_enabled(),
            recovery_enter_degraded_ticks: default_recovery_enter_degraded_ticks(),
            recovery_exit_clean_ticks: default_recovery_exit_clean_ticks(),
            recovery_degraded_loss_threshold: default_recovery_degraded_loss_threshold(),
            recovery_degraded_late_threshold: default_recovery_degraded_late_threshold(),
            recovery_degraded_jitter_ms: default_recovery_degraded_jitter_ms(),
            recovery_degraded_stale_ack_ms: default_recovery_degraded_stale_ack_ms(),
            recovery_degraded_queue_pressure: default_recovery_degraded_queue_pressure(),
            recovery_clean_loss_threshold: default_recovery_clean_loss_threshold(),
            recovery_clean_late_threshold: default_recovery_clean_late_threshold(),
            recovery_clean_jitter_ms: default_recovery_clean_jitter_ms(),
            recovery_clean_stale_ack_ms: default_recovery_clean_stale_ack_ms(),
            recovery_clean_queue_pressure: default_recovery_clean_queue_pressure(),
            recovery_path_loss_exclude_threshold: default_recovery_path_loss_exclude_threshold(),
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
            degraded_loss_threshold: self.recovery_degraded_loss_threshold.clamp(0.0, 1.0),
            degraded_late_threshold: self.recovery_degraded_late_threshold.clamp(0.0, 1.0),
            degraded_jitter_ms: self.recovery_degraded_jitter_ms.max(0.0),
            degraded_stale_ack_ms: self.recovery_degraded_stale_ack_ms,
            degraded_queue_pressure: self.recovery_degraded_queue_pressure.clamp(0.0, 1.0),
            clean_loss_threshold: self.recovery_clean_loss_threshold.clamp(0.0, 1.0),
            clean_late_threshold: self.recovery_clean_late_threshold.clamp(0.0, 1.0),
            clean_jitter_ms: self.recovery_clean_jitter_ms.max(0.0),
            clean_stale_ack_ms: self.recovery_clean_stale_ack_ms,
            clean_queue_pressure: self.recovery_clean_queue_pressure.clamp(0.0, 1.0),
            path_loss_exclude_threshold: self.recovery_path_loss_exclude_threshold.clamp(0.0, 1.0),
        }
    }
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
    4 * 1024 * 1024
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

fn default_recovery_degraded_loss_threshold() -> f64 {
    0.08
}

fn default_recovery_degraded_late_threshold() -> f64 {
    0.03
}

fn default_recovery_degraded_jitter_ms() -> f64 {
    80.0
}

fn default_recovery_degraded_stale_ack_ms() -> u64 {
    1_500
}

fn default_recovery_degraded_queue_pressure() -> f64 {
    0.70
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
