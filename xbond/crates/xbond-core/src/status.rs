use serde::{Deserialize, Serialize};

use crate::health::{PathRole, ScoredPath};
use crate::scheduler::{ScheduleMode, SchedulePlan};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondStatus {
    pub enabled: bool,
    pub running: bool,
    pub mode: ScheduleMode,
    pub server_addr: String,
    pub tunnel: XBondTunnelStatus,
    pub anchor_path_id: Option<u16>,
    pub schedule: SchedulePlan,
    pub paths: Vec<XBondPathStatus>,
    pub data_packets_sent: u64,
    pub duplicate_packets_sent: u64,
    pub duplicate_packets_dropped: u64,
    pub data_packets_received: u64,
    pub data_bytes_sent: u64,
    pub data_bytes_received: u64,
    pub outbound_throughput_bps: u64,
    pub inbound_throughput_bps: u64,
    pub fec_packets_sent: u64,
    pub fec_packets_recovered: u64,
    pub fec_packets_skipped: u64,
    pub fec: XBondFecStatus,
    pub late_packets_dropped: u64,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondPathStatus {
    pub path_id: u16,
    pub name: String,
    pub interface_name: Option<String>,
    pub bind_addr: Option<String>,
    pub bind_device: Option<String>,
    pub path_isolation: PathIsolationStatus,
    pub role: PathRole,
    pub score: f64,
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
}

impl From<ScoredPath> for XBondPathStatus {
    fn from(value: ScoredPath) -> Self {
        Self {
            path_id: value.path.path_id,
            name: value.path.name,
            interface_name: value.path.interface_name,
            bind_addr: None,
            bind_device: None,
            path_isolation: PathIsolationStatus::default(),
            role: value.role,
            score: value.score,
            rtt_ms: value.path.rtt_ms,
            jitter_ms: value.path.jitter_ms,
            loss_rate: value.path.loss_rate,
            late_rate: value.path.late_rate,
            queue_depth: value.path.queue_depth,
            outbound_throughput_bps: value.path.outbound_throughput_bps,
            inbound_throughput_bps: value.path.inbound_throughput_bps,
            duplicate_inbound_throughput_bps: value.path.duplicate_inbound_throughput_bps,
            raw_inbound_throughput_bps: value.path.raw_inbound_throughput_bps,
            throughput_bps: value.path.throughput_bps,
            interface_up: value.path.interface_up,
            in_cooldown: value.path.in_cooldown,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PathIsolationStatus {
    pub requested: bool,
    pub active: bool,
    pub method: String,
    pub message: String,
}

impl Default for PathIsolationStatus {
    fn default() -> Self {
        Self {
            requested: false,
            active: false,
            method: "none".to_string(),
            message: "No bind-device isolation requested.".to_string(),
        }
    }
}

impl PathIsolationStatus {
    pub fn requested(method: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            requested: true,
            active: false,
            method: method.into(),
            message: message.into(),
        }
    }

    pub fn active(method: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            requested: true,
            active: true,
            method: method.into(),
            message: message.into(),
        }
    }

    pub fn failed(method: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            requested: true,
            active: false,
            method: method.into(),
            message: message.into(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondTunnelStatus {
    pub state: String,
    pub device_name: Option<String>,
    pub mtu: Option<u16>,
    pub message: String,
}

impl Default for XBondTunnelStatus {
    fn default() -> Self {
        Self {
            state: "disabled".to_string(),
            device_name: None,
            mtu: None,
            message: "XBond tunnel is disabled by default.".to_string(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondFecStatus {
    pub configured: bool,
    pub production_ready: bool,
    pub message: String,
}

impl Default for XBondFecStatus {
    fn default() -> Self {
        Self {
            configured: false,
            production_ready: false,
            message: "FEC is not configured for the current schedule.".to_string(),
        }
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct XBondRuntimeStatus {
    #[serde(default)]
    pub running: bool,
    #[serde(default)]
    pub mode: ScheduleMode,
    #[serde(default)]
    pub server_addr: String,
    #[serde(default)]
    pub tunnel: XBondTunnelStatus,
    #[serde(default)]
    pub paths: Vec<crate::health::PathHealthSnapshot>,
    #[serde(default)]
    pub data_packets_sent: u64,
    #[serde(default)]
    pub duplicate_packets_sent: u64,
    #[serde(default)]
    pub duplicate_packets_dropped: u64,
    #[serde(default)]
    pub data_packets_received: u64,
    #[serde(default)]
    pub data_bytes_sent: u64,
    #[serde(default)]
    pub data_bytes_received: u64,
    #[serde(default)]
    pub outbound_throughput_bps: u64,
    #[serde(default)]
    pub inbound_throughput_bps: u64,
    #[serde(default)]
    pub fec_packets_sent: u64,
    #[serde(default)]
    pub fec_packets_recovered: u64,
    #[serde(default)]
    pub fec_packets_skipped: u64,
    #[serde(default)]
    pub fec: XBondFecStatus,
    #[serde(default)]
    pub late_packets_dropped: u64,
    #[serde(default)]
    pub message: Option<String>,
}
