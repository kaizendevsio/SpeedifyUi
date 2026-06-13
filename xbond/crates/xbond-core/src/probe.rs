use serde::{Deserialize, Serialize};

use crate::scheduler::ScheduleMode;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProbeAggregate {
    pub mode: ScheduleMode,
    pub anchor_path_id: Option<u16>,
    pub duplicate_path_ids: Vec<u16>,
    pub started_at: u64,
    pub completed_at: u64,
    pub paths: Vec<ProbePathStats>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProbePathStats {
    pub path_id: u16,
    pub adapter: Option<String>,
    pub interface_name: Option<String>,
    pub bind: String,
    pub source: Option<String>,
    pub sent: u32,
    pub received: u32,
    pub acks: u32,
    pub first_arrivals: u32,
    pub duplicates_dropped: Option<u32>,
    pub loss_rate: f64,
    pub avg_rtt_ms: Option<f64>,
    pub route_verified: bool,
    pub route_verification: RouteVerification,

    #[serde(skip)]
    rtt_total_ms: f64,
}

impl ProbePathStats {
    pub fn new(
        path_id: u16,
        adapter: Option<String>,
        interface_name: Option<String>,
        bind: String,
        source: Option<String>,
        route_verification: RouteVerification,
    ) -> Self {
        Self {
            path_id,
            adapter,
            interface_name,
            bind,
            source,
            sent: 0,
            received: 0,
            acks: 0,
            first_arrivals: 0,
            duplicates_dropped: None,
            loss_rate: 0.0,
            avg_rtt_ms: None,
            route_verified: route_verification.verified,
            route_verification,
            rtt_total_ms: 0.0,
        }
    }

    pub fn record_sent(&mut self) {
        self.sent += 1;
        self.refresh_rates();
    }

    pub fn record_ack(&mut self, rtt_ms: f64, first_arrival: bool) {
        self.received += 1;
        self.acks += 1;
        if first_arrival {
            self.first_arrivals += 1;
        }

        if rtt_ms.is_finite() {
            self.rtt_total_ms += rtt_ms;
            self.avg_rtt_ms = Some(self.rtt_total_ms / f64::from(self.received));
        }
        self.refresh_rates();
    }

    pub fn set_duplicates_dropped(&mut self, value: u32) {
        self.duplicates_dropped = Some(value);
    }

    fn refresh_rates(&mut self) {
        self.loss_rate = if self.sent == 0 {
            0.0
        } else {
            f64::from(self.sent.saturating_sub(self.received)) / f64::from(self.sent)
        };
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RouteVerification {
    pub verified: bool,
    pub method: String,
    pub reason: String,
}

impl RouteVerification {
    pub fn verified(method: impl Into<String>, reason: impl Into<String>) -> Self {
        Self {
            verified: true,
            method: method.into(),
            reason: reason.into(),
        }
    }

    pub fn failed(method: impl Into<String>, reason: impl Into<String>) -> Self {
        Self {
            verified: false,
            method: method.into(),
            reason: reason.into(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn path_stats_track_loss_first_arrivals_and_average_rtt() {
        let mut stats = ProbePathStats::new(
            2,
            Some("Backup modem".to_string()),
            Some("wwan0".to_string()),
            "192.0.2.10:0".to_string(),
            Some("192.0.2.10".to_string()),
            RouteVerification::verified("test", "test route"),
        );

        stats.record_sent();
        stats.record_sent();
        stats.record_sent();
        stats.record_ack(30.0, true);
        stats.record_ack(45.0, false);

        assert_eq!(stats.sent, 3);
        assert_eq!(stats.received, 2);
        assert_eq!(stats.acks, 2);
        assert_eq!(stats.first_arrivals, 1);
        assert_eq!(stats.loss_rate, 1.0 / 3.0);
        assert_eq!(stats.avg_rtt_ms, Some(37.5));
        assert!(stats.route_verified);
    }

    #[test]
    fn route_verification_fails_closed() {
        let route = RouteVerification::failed("ip-route-get", "source address is unspecified");

        assert!(!route.verified);
        assert_eq!(route.method, "ip-route-get");
    }
}
