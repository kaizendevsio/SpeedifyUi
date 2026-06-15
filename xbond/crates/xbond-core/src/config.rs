use serde::{Deserialize, Serialize};

use crate::scheduler::{RedundancyPolicy, ScheduleMode};

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
            runtime_status_path: Some("/run/xbond/client-status.json".to_string()),
            paths: Vec::new(),
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
