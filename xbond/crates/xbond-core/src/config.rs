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
    #[serde(default = "default_tun_queue_capacity")]
    pub tun_queue_capacity: usize,
    #[serde(default = "default_inbound_queue_capacity")]
    pub inbound_queue_capacity: usize,
    #[serde(default = "default_udp_socket_buffer_bytes")]
    pub udp_socket_buffer_bytes: usize,
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

pub fn default_tun_queue_capacity() -> usize {
    2048
}

pub fn default_inbound_queue_capacity() -> usize {
    4096
}

pub fn default_udp_socket_buffer_bytes() -> usize {
    4 * 1024 * 1024
}
