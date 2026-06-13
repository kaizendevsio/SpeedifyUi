use serde::{Deserialize, Serialize};

use crate::health::{PathRole, ScoredPath};
use crate::scheduler::{ScheduleMode, SchedulePlan};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondStatus {
    pub enabled: bool,
    pub running: bool,
    pub mode: ScheduleMode,
    pub server_addr: String,
    pub anchor_path_id: Option<u16>,
    pub schedule: SchedulePlan,
    pub paths: Vec<XBondPathStatus>,
    pub duplicate_packets_dropped: u64,
    pub fec_packets_recovered: u64,
    pub late_packets_dropped: u64,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondPathStatus {
    pub path_id: u16,
    pub name: String,
    pub interface_name: Option<String>,
    pub role: PathRole,
    pub score: f64,
    pub rtt_ms: Option<f64>,
    pub jitter_ms: Option<f64>,
    pub loss_rate: f64,
    pub late_rate: f64,
    pub queue_depth: u32,
    pub throughput_bps: u64,
    pub interface_up: bool,
    pub in_cooldown: bool,
}

impl From<ScoredPath> for XBondPathStatus {
    fn from(value: ScoredPath) -> Self {
        Self {
            path_id: value.path.path_id,
            name: value.path.name,
            interface_name: value.path.interface_name,
            role: value.role,
            score: value.score,
            rtt_ms: value.path.rtt_ms,
            jitter_ms: value.path.jitter_ms,
            loss_rate: value.path.loss_rate,
            late_rate: value.path.late_rate,
            queue_depth: value.path.queue_depth,
            throughput_bps: value.path.throughput_bps,
            interface_up: value.path.interface_up,
            in_cooldown: value.path.in_cooldown,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct XBondRuntimeStatus {
    #[serde(default)]
    pub running: bool,
    #[serde(default)]
    pub paths: Vec<crate::health::PathHealthSnapshot>,
    #[serde(default)]
    pub duplicate_packets_dropped: u64,
    #[serde(default)]
    pub fec_packets_recovered: u64,
    #[serde(default)]
    pub late_packets_dropped: u64,
    #[serde(default)]
    pub message: Option<String>,
}
