use serde::{Deserialize, Serialize};

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

        if self.loss_rate >= 1.0 {
            return Some("Path has 100% heartbeat loss.");
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
        let loss_penalty = self.loss_rate.clamp(0.0, 1.0) * 800.0;
        let late_penalty = self.late_rate.clamp(0.0, 1.0) * 1_000.0;
        let queue_penalty = f64::from(self.queue_depth.min(10_000)) * 0.1;
        let queue_pressure_penalty = self.queue_pressure.clamp(0.0, 1.0) * 400.0;
        let stale_ack_penalty = self
            .stale_ack_ms
            .map(|value| value.min(5_000) as f64 * 0.05)
            .unwrap_or_default();
        let send_failure_penalty = f64::from(self.send_failure_streak.min(10)) * 150.0;
        let duplicate_help_penalty = if self.duplicate_usefulness <= 0.05
            && self.raw_inbound_throughput_bps > 250_000
        {
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

        1_000.0 - rtt_penalty - jitter_penalty - loss_penalty - late_penalty - queue_penalty
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
    Probe,
    Cooldown,
    Unavailable,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScoredPath {
    pub path: PathHealthSnapshot,
    pub score: f64,
    pub role: PathRole,
}

pub fn select_path_roles(paths: &[PathHealthSnapshot], max_backups: usize) -> Vec<ScoredPath> {
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
            ScoredPath { path, score, role }
        })
        .collect();

    scored.sort_by(|a, b| b.score.total_cmp(&a.score));

    let mut anchor_assigned = false;
    let mut backups_assigned = 0usize;
    for scored_path in &mut scored {
        if !scored_path.score.is_finite() || !scored_path.path.is_realtime_eligible() {
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

    scored
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
        let mut full_loss = path(2, "full-loss", 40.0, 1.0, 0.0);
        full_loss.loss_rate = 1.0;

        let roles = select_path_roles(&[offline, full_loss], 1);

        assert!(roles.iter().all(|path| path.role != PathRole::Anchor));
        assert!(roles.iter().all(|path| path.role != PathRole::Backup));
    }

    #[test]
    fn full_loss_path_cannot_be_anchor() {
        let full_loss = path(1, "full-loss", 15.0, 1.0, 0.0);
        let roles = select_path_roles(&[full_loss, path(2, "stable", 50.0, 0.0, 0.0)], 1);

        assert_eq!(roles[0].path.name, "stable");
        assert_eq!(roles[0].role, PathRole::Anchor);
        assert_eq!(
            roles
                .iter()
                .find(|path| path.path.name == "full-loss")
                .unwrap()
                .role,
            PathRole::Cooldown
        );
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
}
