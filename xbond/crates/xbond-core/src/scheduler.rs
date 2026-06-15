use serde::{Deserialize, Serialize};

use crate::health::{PathRole, ScoredPath};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ScheduleMode {
    AnchorOnly,
    #[serde(rename = "anchor-duplicate-1", alias = "anchor-duplicate1")]
    AnchorDuplicate1,
    AnchorFec,
    FullDuplicateDebug,
}

impl Default for ScheduleMode {
    fn default() -> Self {
        Self::AnchorDuplicate1
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

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct ScheduledTransmission {
    pub path_id: u16,
    pub packet_kind: crate::protocol::PacketKind,
}

pub fn build_transmission_plan(schedule: &SchedulePlan) -> Vec<ScheduledTransmission> {
    let mut transmissions = Vec::new();

    for path_id in &schedule.data_path_ids {
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Data,
        });
    }

    for path_id in &schedule.duplicate_path_ids {
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Duplicate,
        });
    }

    for path_id in &schedule.fec_path_ids {
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Fec,
        });
    }

    transmissions
}

#[cfg(test)]
mod tests {
    use crate::health::{select_path_roles, PathHealthSnapshot};
    use crate::protocol::PacketKind;

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
            outbound_throughput_bps: 2_500_000,
            inbound_throughput_bps: 2_500_000,
            duplicate_inbound_throughput_bps: 0,
            raw_inbound_throughput_bps: 2_500_000,
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

    #[test]
    fn duplicate_mode_serde_accepts_legacy_and_preferred_names() {
        #[derive(serde::Deserialize)]
        struct ModeWrapper {
            mode: ScheduleMode,
        }

        assert_eq!(
            toml::from_str::<ModeWrapper>("mode = \"anchor-duplicate-1\"")
                .unwrap()
                .mode,
            ScheduleMode::AnchorDuplicate1
        );
        assert_eq!(
            toml::from_str::<ModeWrapper>("mode = \"anchor-duplicate1\"")
                .unwrap()
                .mode,
            ScheduleMode::AnchorDuplicate1
        );
        assert_eq!(
            serde_json::to_string(&ScheduleMode::AnchorDuplicate1).unwrap(),
            "\"anchor-duplicate-1\""
        );
    }

    #[test]
    fn duplicate_transmission_plan_marks_anchor_data_and_backup_duplicate() {
        let plan = SchedulePlan {
            mode: ScheduleMode::AnchorDuplicate1,
            anchor_path_id: Some(1),
            data_path_ids: vec![1],
            duplicate_path_ids: vec![2],
            fec_path_ids: Vec::new(),
        };

        let transmissions = build_transmission_plan(&plan);

        assert_eq!(
            transmissions,
            vec![
                ScheduledTransmission {
                    path_id: 1,
                    packet_kind: PacketKind::Data,
                },
                ScheduledTransmission {
                    path_id: 2,
                    packet_kind: PacketKind::Duplicate,
                },
            ]
        );
    }

    #[test]
    fn fec_transmission_plan_is_explicit_about_fec_paths() {
        let plan = SchedulePlan {
            mode: ScheduleMode::AnchorFec,
            anchor_path_id: Some(1),
            data_path_ids: vec![1],
            duplicate_path_ids: Vec::new(),
            fec_path_ids: vec![2, 3],
        };

        let transmissions = build_transmission_plan(&plan);

        assert_eq!(transmissions[0].packet_kind, PacketKind::Data);
        assert_eq!(transmissions[1].packet_kind, PacketKind::Fec);
        assert_eq!(transmissions[2].packet_kind, PacketKind::Fec);
    }

    #[test]
    fn default_mode_is_two_link_duplicate() {
        assert_eq!(ScheduleMode::default(), ScheduleMode::AnchorDuplicate1);
    }
}
