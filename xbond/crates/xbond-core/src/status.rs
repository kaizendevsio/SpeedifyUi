use serde::{Deserialize, Serialize};

use crate::health::{PathRole, ScoredPath};
use crate::reorder::ReorderStats;
use crate::scheduler::{RecoveryStatus, RedundancyPolicy, ScheduleMode, SchedulePlan};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondStatus {
    pub enabled: bool,
    pub running: bool,
    pub mode: ScheduleMode,
    pub redundancy_policy: RedundancyPolicy,
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
    pub reorder: XBondReorderStatus,
    #[serde(default)]
    pub repair: XBondRepairStatus,
    #[serde(default)]
    pub server_recovery: XBondServerRecoveryStatus,
    #[serde(default)]
    pub server_health: XBondServerHealthStatus,
    pub process: XBondProcessStatus,
    pub recovery: RecoveryStatus,
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
            send_failure_streak: value.path.send_failure_streak,
            stale_ack_ms: value.path.stale_ack_ms,
            queue_pressure: value.path.queue_pressure,
            duplicate_usefulness: value.path.duplicate_usefulness,
            throughput_collapse_score: value.path.throughput_collapse_score,
            demotion_reason: value.path.demotion_reason,
            role_reason: value.path.role_reason,
            socket_generation: value.path.socket_generation,
            socket_ifindex: value.path.socket_ifindex,
            socket_bind_addr: value.path.socket_bind_addr,
            last_socket_error: value.path.last_socket_error,
            last_rebind_reason: value.path.last_rebind_reason,
            last_rebind_error: value.path.last_rebind_error,
            last_rebind_at_micros: value.path.last_rebind_at_micros,
            rebind_count: value.path.rebind_count,
            heartbeat_sent: value.path.heartbeat_sent,
            heartbeat_acked: value.path.heartbeat_acked,
            heartbeat_expired: value.path.heartbeat_expired,
            heartbeat_late_acks: value.path.heartbeat_late_acks,
            heartbeat_rebind_discarded: value.path.heartbeat_rebind_discarded,
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
    #[serde(default)]
    pub rtt_ms: Option<f64>,
    #[serde(default)]
    pub jitter_ms: Option<f64>,
    #[serde(default)]
    pub loss_rate: Option<f64>,
    #[serde(default)]
    pub success_rate: Option<f64>,
    #[serde(default)]
    pub pending_probes: usize,
    #[serde(default)]
    pub last_success_age_ms: Option<u64>,
    #[serde(default = "default_tunnel_health_status")]
    pub status: String,
    #[serde(default = "default_tunnel_health_reason")]
    pub reason: String,
}

impl Default for XBondTunnelStatus {
    fn default() -> Self {
        Self {
            state: "disabled".to_string(),
            device_name: None,
            mtu: None,
            message: "XBond tunnel is disabled by default.".to_string(),
            rtt_ms: None,
            jitter_ms: None,
            loss_rate: None,
            success_rate: None,
            pending_probes: 0,
            last_success_age_ms: None,
            status: default_tunnel_health_status(),
            reason: default_tunnel_health_reason(),
        }
    }
}

fn default_tunnel_health_status() -> String {
    "unknown".to_string()
}

fn default_tunnel_health_reason() -> String {
    "Tunnel health is unavailable.".to_string()
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
pub struct XBondReorderStatus {
    pub return_path: ReorderStats,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondRepairStatus {
    #[serde(default)]
    pub requests_sent: u64,
    #[serde(default)]
    pub requests_received: u64,
    #[serde(default)]
    pub frames_sent: u64,
    #[serde(default)]
    pub frames_delivered: u64,
    #[serde(default)]
    pub cache_misses: u64,
    #[serde(default)]
    pub late_frames: u64,
    #[serde(default)]
    pub queue_drops: u64,
    #[serde(default)]
    pub cache_entries: usize,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct XBondServerHealthTargetStatus {
    pub target: String,
    pub success: bool,
    #[serde(default)]
    pub connect_ms: Option<f64>,
    #[serde(default)]
    pub error: Option<String>,
    pub updated_at_micros: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct XBondServerHealthStatus {
    pub status: String,
    pub reason: String,
    pub success_rate: f64,
    #[serde(default)]
    pub avg_connect_ms: Option<f64>,
    #[serde(default)]
    pub max_connect_ms: Option<f64>,
    #[serde(default)]
    pub last_success_age_ms: Option<u64>,
    pub consecutive_failures: u32,
    pub updated_at_micros: u64,
    #[serde(default)]
    pub targets: Vec<XBondServerHealthTargetStatus>,
}

impl Default for XBondServerHealthStatus {
    fn default() -> Self {
        Self {
            status: "unknown".to_string(),
            reason: "Server egress health has not collected a sample yet.".to_string(),
            success_rate: 0.0,
            avg_connect_ms: None,
            max_connect_ms: None,
            last_success_age_ms: None,
            consecutive_failures: 0,
            updated_at_micros: 0,
            targets: Vec::new(),
        }
    }
}

impl XBondServerHealthStatus {
    pub fn disabled(targets: &[String], updated_at_micros: u64) -> Self {
        Self {
            status: "unknown".to_string(),
            reason: "Server egress health probes are disabled.".to_string(),
            updated_at_micros,
            targets: targets
                .iter()
                .map(|target| XBondServerHealthTargetStatus {
                    target: target.clone(),
                    success: false,
                    connect_ms: None,
                    error: Some("disabled".to_string()),
                    updated_at_micros,
                })
                .collect(),
            ..Self::default()
        }
    }

    pub fn classify(
        targets: Vec<XBondServerHealthTargetStatus>,
        consecutive_failures: u32,
        last_success_age_ms: Option<u64>,
        updated_at_micros: u64,
    ) -> Self {
        if targets.is_empty() {
            return Self {
                status: "unknown".to_string(),
                reason: "No server egress health targets are configured.".to_string(),
                consecutive_failures,
                updated_at_micros,
                ..Self::default()
            };
        }

        let success_count = targets.iter().filter(|target| target.success).count();
        let success_rate = success_count as f64 / targets.len() as f64;
        let mut connect_times = targets
            .iter()
            .filter_map(|target| target.connect_ms)
            .collect::<Vec<_>>();
        connect_times.sort_by(|left, right| left.total_cmp(right));
        let avg_connect_ms = (!connect_times.is_empty())
            .then(|| connect_times.iter().sum::<f64>() / connect_times.len() as f64);
        let max_connect_ms = connect_times.last().copied();

        let (status, reason) = if success_count == 0 && consecutive_failures >= 2 {
            (
                "down",
                format!("All {} server egress targets failed for {consecutive_failures} consecutive rounds.", targets.len()),
            )
        } else if success_count == 0 {
            (
                "degraded",
                "All server egress targets failed in the latest round.".to_string(),
            )
        } else if success_count == targets.len() && avg_connect_ms.is_some_and(|avg| avg < 100.0) {
            (
                "healthy",
                "All server egress targets are reachable with low connect latency.".to_string(),
            )
        } else if success_count == targets.len() {
            (
                "degraded",
                format!(
                    "Server egress targets are reachable, but average connect latency is {:.0} ms.",
                    avg_connect_ms.unwrap_or_default()
                ),
            )
        } else {
            (
                "degraded",
                format!(
                    "{success_count}/{} server egress targets are reachable.",
                    targets.len()
                ),
            )
        };

        Self {
            status: status.to_string(),
            reason,
            success_rate,
            avg_connect_ms,
            max_connect_ms,
            last_success_age_ms,
            consecutive_failures,
            updated_at_micros,
            targets,
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct XBondServerRecoveryStatus {
    #[serde(default)]
    pub reported: bool,
    #[serde(default)]
    pub recovery_active: bool,
    #[serde(default)]
    pub schedule_required: bool,
    #[serde(default)]
    pub schedule_generation: u64,
    #[serde(default)]
    pub schedule_age_ms: u64,
    #[serde(default)]
    pub ingress_reorder: XBondServerIngressReorderStatus,
    #[serde(default)]
    pub repair: XBondRepairStatus,
    #[serde(default)]
    pub server_health: XBondServerHealthStatus,
    #[serde(default)]
    pub updated_at_micros: u64,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondServerIngressReorderStatus {
    #[serde(default)]
    pub current_hold_ms: u64,
    #[serde(default)]
    pub normal_hold_ms: u64,
    #[serde(default)]
    pub recovery_min_hold_ms: u64,
    #[serde(default)]
    pub recovery_max_hold_ms: u64,
    #[serde(default)]
    pub adaptive_recovery_hold_enabled: bool,
    #[serde(default)]
    pub adaptive_calm_samples: u32,
    #[serde(default)]
    pub adaptive_last_adjustment_reason: String,
    #[serde(default)]
    pub capacity: usize,
    #[serde(default)]
    pub stats: ReorderStats,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondPacketPoolStatus {
    #[serde(default)]
    pub retained: usize,
    #[serde(default)]
    pub capacity: usize,
    #[serde(default)]
    pub fallback_allocations: u64,
    #[serde(default)]
    pub discarded: u64,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondRepairCacheStatus {
    #[serde(default)]
    pub entries: usize,
    #[serde(default)]
    pub accounted_bytes: usize,
    #[serde(default)]
    pub byte_capacity: usize,
    #[serde(default)]
    pub prune_runs: u64,
    #[serde(default)]
    pub last_pruned_at_micros: u64,
    #[serde(default)]
    pub last_pruned_entries: usize,
    #[serde(default)]
    pub last_pruned_accounted_bytes: usize,
    #[serde(default)]
    pub total_pruned_entries: u64,
    #[serde(default)]
    pub total_pruned_accounted_bytes: u64,
    #[serde(default)]
    pub quiescent: bool,
    #[serde(default)]
    pub quiescent_since_micros: Option<u64>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondSocketBufferStatus {
    #[serde(default)]
    pub scope: String,
    #[serde(default)]
    pub requested_receive_bytes: usize,
    #[serde(default)]
    pub requested_send_bytes: usize,
    #[serde(default)]
    pub effective_receive_bytes: usize,
    #[serde(default)]
    pub effective_send_bytes: usize,
    #[serde(default)]
    pub below_requested: bool,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondKernelNetworkStatus {
    #[serde(default)]
    pub udp_in_errors: u64,
    #[serde(default)]
    pub udp_rcvbuf_errors: u64,
    #[serde(default)]
    pub udp_sndbuf_errors: u64,
    #[serde(default)]
    pub udp_no_ports: u64,
    #[serde(default)]
    pub tunnel_rx_drops: u64,
    #[serde(default)]
    pub tunnel_tx_drops: u64,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct XBondSaturationStatus {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default)]
    pub queue_utilization: f64,
    #[serde(default)]
    pub oldest_age_ms: u64,
    #[serde(default)]
    pub recent_drain_packets_per_second: f64,
    #[serde(default)]
    pub pacing_delay_micros: u64,
    #[serde(default)]
    pub duplicate_suppressions: u64,
    #[serde(default)]
    pub fec_suppressions: u64,
    #[serde(default)]
    pub saturation_periods: u64,
    #[serde(default)]
    pub hard_duration_ms: u64,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct XBondStageTimingStatus {
    #[serde(default)]
    pub receive_micros_total: u64,
    #[serde(default)]
    pub receive_batches: u64,
    #[serde(default)]
    pub receive_datagrams: u64,
    #[serde(default)]
    pub receive_batch_peak: u64,
    #[serde(default)]
    pub decode_micros_total: u64,
    #[serde(default)]
    pub schedule_micros_total: u64,
    #[serde(default)]
    pub enqueue_micros_total: u64,
    #[serde(default)]
    pub tun_micros_total: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct XBondProcessStatus {
    #[serde(default)]
    pub process_cpu_percent: Option<f64>,
    #[serde(default)]
    pub rss_bytes: Option<u64>,
    #[serde(default)]
    pub encode_micros_total: u64,
    #[serde(default)]
    pub decode_micros_total: u64,
    #[serde(default)]
    pub encoded_frames: u64,
    #[serde(default)]
    pub decoded_frames: u64,
    #[serde(default)]
    pub tun_queue_capacity: usize,
    #[serde(default)]
    pub inbound_queue_capacity: usize,
    #[serde(default)]
    pub tun_queue_depth: usize,
    #[serde(default)]
    pub tun_write_queue_depth: usize,
    #[serde(default)]
    pub tun_write_queue_peak_depth: usize,
    #[serde(default)]
    pub inbound_queue_depth: usize,
    #[serde(default)]
    pub udp_socket_buffer_bytes: usize,
    #[serde(default)]
    pub udp_receive_batch_size: usize,
    #[serde(default)]
    pub socket_buffers: Vec<XBondSocketBufferStatus>,
    #[serde(default)]
    pub kernel_network: XBondKernelNetworkStatus,
    #[serde(default)]
    pub saturation: XBondSaturationStatus,
    #[serde(default)]
    pub stage_timings: XBondStageTimingStatus,
    #[serde(default)]
    pub tun_queue_drops: u64,
    #[serde(default)]
    pub inbound_queue_drops: u64,
    #[serde(default)]
    pub duplicate_send_skips: u64,
    #[serde(default)]
    pub fec_send_skips: u64,
    #[serde(default)]
    pub tun_write_packets: u64,
    #[serde(default)]
    pub tun_write_queue_micros_total: u64,
    #[serde(default)]
    pub tun_write_micros_total: u64,
    #[serde(default)]
    pub tun_packet_pool: XBondPacketPoolStatus,
    #[serde(default)]
    pub receive_payload_pool: XBondPacketPoolStatus,
    #[serde(default)]
    pub repair_cache: XBondRepairCacheStatus,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct XBondDiagnosticOverrideStatus {
    pub mode: ScheduleMode,
    pub redundancy_policy: RedundancyPolicy,
    pub expires_in_seconds: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct XBondRuntimeStatus {
    #[serde(default)]
    pub running: bool,
    #[serde(default)]
    pub mode: ScheduleMode,
    #[serde(default)]
    pub redundancy_policy: RedundancyPolicy,
    #[serde(default)]
    pub server_addr: String,
    #[serde(default)]
    pub tunnel: XBondTunnelStatus,
    #[serde(default)]
    pub anchor_path_id: Option<u16>,
    #[serde(default)]
    pub schedule: Option<SchedulePlan>,
    #[serde(default)]
    pub schedule_generation: u64,
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
    pub reorder: XBondReorderStatus,
    #[serde(default)]
    pub repair: XBondRepairStatus,
    #[serde(default)]
    pub server_recovery: XBondServerRecoveryStatus,
    #[serde(default)]
    pub server_health: XBondServerHealthStatus,
    #[serde(default)]
    pub process: XBondProcessStatus,
    #[serde(default)]
    pub recovery: RecoveryStatus,
    #[serde(default)]
    pub diagnostic_override: Option<XBondDiagnosticOverrideStatus>,
    #[serde(default)]
    pub schedule_change_count: u64,
    #[serde(default)]
    pub message: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tunnel_status_deserializes_legacy_json_without_health_fields() {
        let status = serde_json::from_str::<XBondTunnelStatus>(
            r#"{
                "state": "running",
                "device_name": "xbond0",
                "mtu": 1400,
                "message": "legacy"
            }"#,
        )
        .unwrap();

        assert_eq!(status.state, "running");
        assert_eq!(status.device_name.as_deref(), Some("xbond0"));
        assert_eq!(status.rtt_ms, None);
        assert_eq!(status.loss_rate, None);
        assert_eq!(status.status, "unknown");
        assert_eq!(status.reason, "Tunnel health is unavailable.");
    }

    #[test]
    fn tunnel_status_serializes_health_fields() {
        let status = XBondTunnelStatus {
            state: "running".to_string(),
            device_name: Some("xbond0".to_string()),
            mtu: Some(1400),
            message: "live".to_string(),
            rtt_ms: Some(72.0),
            jitter_ms: Some(4.0),
            loss_rate: Some(0.05),
            success_rate: Some(0.95),
            pending_probes: 1,
            last_success_age_ms: Some(250),
            status: "fair".to_string(),
            reason: "Tunnel heartbeat is fair.".to_string(),
        };

        let json = serde_json::to_value(status).unwrap();

        assert_eq!(json["rtt_ms"], 72.0);
        assert_eq!(json["jitter_ms"], 4.0);
        assert_eq!(json["loss_rate"], 0.05);
        assert_eq!(json["success_rate"], 0.95);
        assert_eq!(json["pending_probes"], 1);
        assert_eq!(json["last_success_age_ms"], 250);
        assert_eq!(json["status"], "fair");
    }

    #[test]
    fn server_health_classifies_healthy_targets() {
        let status = XBondServerHealthStatus::classify(
            vec![
                target("8.8.8.8:53", true, Some(12.0)),
                target("1.1.1.1:443", true, Some(18.0)),
            ],
            0,
            Some(0),
            1,
        );

        assert_eq!(status.status, "healthy");
        assert_eq!(status.success_rate, 1.0);
        assert_eq!(status.avg_connect_ms, Some(15.0));
        assert_eq!(status.max_connect_ms, Some(18.0));
    }

    #[test]
    fn server_health_classifies_partial_failure_as_degraded() {
        let status = XBondServerHealthStatus::classify(
            vec![
                target("8.8.8.8:53", true, Some(20.0)),
                target("1.1.1.1:443", false, None),
            ],
            0,
            Some(500),
            2,
        );

        assert_eq!(status.status, "degraded");
        assert_eq!(status.success_rate, 0.5);
        assert!(status.reason.contains("1/2"));
    }

    #[test]
    fn server_health_classifies_high_latency_as_degraded() {
        let status = XBondServerHealthStatus::classify(
            vec![
                target("8.8.8.8:53", true, Some(150.0)),
                target("1.1.1.1:443", true, Some(170.0)),
            ],
            0,
            Some(0),
            3,
        );

        assert_eq!(status.status, "degraded");
        assert_eq!(status.avg_connect_ms, Some(160.0));
    }

    #[test]
    fn server_health_classifies_repeated_full_failure_as_down() {
        let status = XBondServerHealthStatus::classify(
            vec![
                target("8.8.8.8:53", false, None),
                target("1.1.1.1:443", false, None),
            ],
            2,
            Some(30_000),
            4,
        );

        assert_eq!(status.status, "down");
        assert_eq!(status.success_rate, 0.0);
        assert_eq!(status.consecutive_failures, 2);
    }

    #[test]
    fn server_health_defaults_to_unknown_for_legacy_json() {
        let status = serde_json::from_str::<XBondRuntimeStatus>(
            r#"{
                "running": true,
                "mode": "anchor-duplicate-1",
                "redundancy_policy": "balanced"
            }"#,
        )
        .unwrap();

        assert_eq!(status.server_health.status, "unknown");
        assert_eq!(status.server_recovery.server_health.status, "unknown");
    }

    #[test]
    fn runtime_status_serializes_packet_pool_telemetry() {
        let status = XBondRuntimeStatus {
            process: XBondProcessStatus {
                tun_packet_pool: XBondPacketPoolStatus {
                    retained: 7,
                    capacity: 16,
                    fallback_allocations: 5,
                    discarded: 2,
                },
                receive_payload_pool: XBondPacketPoolStatus {
                    retained: 11,
                    capacity: 32,
                    fallback_allocations: 6,
                    discarded: 3,
                },
                repair_cache: XBondRepairCacheStatus {
                    entries: 0,
                    accounted_bytes: 0,
                    byte_capacity: 4_096,
                    prune_runs: 3,
                    last_pruned_at_micros: 900,
                    last_pruned_entries: 2,
                    last_pruned_accounted_bytes: 512,
                    total_pruned_entries: 7,
                    total_pruned_accounted_bytes: 1_536,
                    quiescent: true,
                    quiescent_since_micros: Some(900),
                },
                ..XBondProcessStatus::default()
            },
            ..XBondRuntimeStatus::default()
        };

        let value = serde_json::to_value(status).unwrap();

        assert_eq!(value["process"]["tun_packet_pool"]["retained"], 7);
        assert_eq!(value["process"]["tun_packet_pool"]["capacity"], 16);
        assert_eq!(
            value["process"]["tun_packet_pool"]["fallback_allocations"],
            5
        );
        assert_eq!(value["process"]["tun_packet_pool"]["discarded"], 2);
        assert_eq!(value["process"]["receive_payload_pool"]["retained"], 11);
        assert_eq!(value["process"]["receive_payload_pool"]["capacity"], 32);
        assert_eq!(
            value["process"]["receive_payload_pool"]["fallback_allocations"],
            6
        );
        assert_eq!(value["process"]["receive_payload_pool"]["discarded"], 3);
        assert_eq!(value["process"]["repair_cache"]["accounted_bytes"], 0);
        assert_eq!(value["process"]["repair_cache"]["last_pruned_entries"], 2);
        assert_eq!(value["process"]["repair_cache"]["quiescent"], true);
        assert_eq!(
            value["process"]["repair_cache"]["quiescent_since_micros"],
            900
        );
    }

    fn target(
        target: &str,
        success: bool,
        connect_ms: Option<f64>,
    ) -> XBondServerHealthTargetStatus {
        XBondServerHealthTargetStatus {
            target: target.to_string(),
            success,
            connect_ms,
            error: (!success).then(|| "timeout".to_string()),
            updated_at_micros: 1,
        }
    }
}
