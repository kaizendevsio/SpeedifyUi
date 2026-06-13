use serde::{Deserialize, Serialize};

use crate::scheduler::ScheduleMode;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ClientConfig {
    pub enabled: bool,
    pub session_id: u64,
    pub server_addr: String,
    pub mode: ScheduleMode,
    pub max_active_backups: usize,
    pub realtime_deadline_ms: u64,
    pub runtime_status_path: Option<String>,
    pub paths: Vec<PathConfig>,
}

impl Default for ClientConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            session_id: 1,
            server_addr: "127.0.0.1:8444".to_string(),
            mode: ScheduleMode::AnchorFec,
            max_active_backups: 2,
            realtime_deadline_ms: 120,
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
