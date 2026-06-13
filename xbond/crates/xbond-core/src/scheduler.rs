use serde::{Deserialize, Serialize};

use crate::health::{PathRole, ScoredPath};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ScheduleMode {
    AnchorOnly,
    AnchorDuplicate1,
    AnchorFec,
    FullDuplicateDebug,
}

impl Default for ScheduleMode {
    fn default() -> Self {
        Self::AnchorFec
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

    match mode {
        ScheduleMode::AnchorOnly => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: Vec::new(),
            fec_path_ids: Vec::new(),
        },
        ScheduleMode::AnchorDuplicate1 => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: backups.into_iter().take(1).collect(),
            fec_path_ids: Vec::new(),
        },
        ScheduleMode::AnchorFec => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: Vec::new(),
            fec_path_ids: backups,
        },
        ScheduleMode::FullDuplicateDebug => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: backups,
            fec_path_ids: Vec::new(),
        },
    }
}

#[cfg(test)]
mod tests {
    use crate::health::{select_path_roles, PathHealthSnapshot};

    use super::*;

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
            throughput_bps: 5_000_000,
            interface_up: true,
            in_cooldown: false,
        }
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
}
