use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use tokio::net::UdpSocket;
use tokio::time;
use xbond_core::{
    build_schedule, select_path_roles, ClientConfig, PacketKind, PathHealthSnapshot, XBondFrame,
    XBondHeader, XBondKey, XBondPathStatus, XBondRuntimeStatus, XBondStatus,
};

#[derive(Debug, Parser)]
#[command(name = "xbond-client")]
#[command(about = "XBond client prototype")]
struct Args {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    Status {
        #[arg(long, default_value = "/etc/xbond/client.toml")]
        config: PathBuf,
        #[arg(long)]
        json: bool,
    },
    Plan {
        #[arg(long, default_value = "/etc/xbond/client.toml")]
        config: PathBuf,
    },
    Ping {
        #[arg(long)]
        server: String,
        #[arg(long, default_value = "0.0.0.0:0")]
        bind: String,
        #[arg(long, default_value_t = 1)]
        path_id: u16,
        #[arg(long)]
        session_id: Option<u64>,
        #[arg(long, default_value_t = 10)]
        count: u32,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
        #[arg(long, default_value_t = 1000)]
        timeout_ms: u64,
        #[arg(long, default_value = "XBOND_PSK")]
        key_env: String,
        #[arg(long)]
        json: bool,
    },
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    match args.command {
        Command::Status { config, json } => {
            let status = load_status(&config)?;
            if json {
                println!("{}", serde_json::to_string_pretty(&status)?);
            } else {
                print_human_status(&status);
            }
        }
        Command::Plan { config } => {
            let status = load_status(&config)?;
            println!("{}", serde_json::to_string_pretty(&status.schedule)?);
        }
        Command::Ping {
            server,
            bind,
            path_id,
            session_id,
            count,
            interval_ms,
            timeout_ms,
            key_env,
            json,
        } => {
            let result = run_ping(PingOptions {
                server,
                bind,
                path_id,
                session_id: session_id.unwrap_or_else(now_micros),
                count,
                interval_ms,
                timeout_ms,
                key_env,
            })
            .await?;
            if json {
                println!("{}", serde_json::to_string_pretty(&result.to_json())?);
            } else {
                print_ping_result(&result);
            }
        }
    }
    Ok(())
}

#[derive(Debug)]
struct PingOptions {
    server: String,
    bind: String,
    path_id: u16,
    session_id: u64,
    count: u32,
    interval_ms: u64,
    timeout_ms: u64,
    key_env: String,
}

#[derive(Debug, Clone)]
struct PingReply {
    sequence: u64,
    rtt_ms: f64,
}

#[derive(Debug)]
struct PingResult {
    server: String,
    bind: String,
    path_id: u16,
    session_id: u64,
    sent: u32,
    received: u32,
    replies: Vec<PingReply>,
}

impl PingResult {
    fn lost(&self) -> u32 {
        self.sent.saturating_sub(self.received)
    }

    fn loss_rate(&self) -> f64 {
        if self.sent == 0 {
            0.0
        } else {
            f64::from(self.lost()) / f64::from(self.sent)
        }
    }

    fn rtt_values(&self) -> Vec<f64> {
        self.replies.iter().map(|reply| reply.rtt_ms).collect()
    }

    fn min_rtt_ms(&self) -> Option<f64> {
        finite_min(&self.rtt_values())
    }

    fn avg_rtt_ms(&self) -> Option<f64> {
        let values = self.rtt_values();
        if values.is_empty() {
            None
        } else {
            Some(values.iter().sum::<f64>() / values.len() as f64)
        }
    }

    fn max_rtt_ms(&self) -> Option<f64> {
        finite_max(&self.rtt_values())
    }

    fn to_json(&self) -> serde_json::Value {
        serde_json::json!({
            "server": self.server,
            "bind": self.bind,
            "path_id": self.path_id,
            "session_id": self.session_id,
            "sent": self.sent,
            "received": self.received,
            "lost": self.lost(),
            "loss_rate": self.loss_rate(),
            "min_rtt_ms": self.min_rtt_ms(),
            "avg_rtt_ms": self.avg_rtt_ms(),
            "max_rtt_ms": self.max_rtt_ms(),
            "replies": self.replies.iter().map(|reply| {
                serde_json::json!({
                    "sequence": reply.sequence,
                    "rtt_ms": reply.rtt_ms,
                })
            }).collect::<Vec<_>>(),
        })
    }
}

async fn run_ping(options: PingOptions) -> Result<PingResult> {
    let key_text = std::env::var(&options.key_env)
        .with_context(|| format!("{} environment variable is required", options.key_env))?;
    let key = XBondKey::from_passphrase(&key_text);
    let socket = UdpSocket::bind(&options.bind)
        .await
        .with_context(|| format!("failed to bind {}", options.bind))?;
    socket
        .connect(&options.server)
        .await
        .with_context(|| format!("failed to connect UDP socket to {}", options.server))?;

    let mut replies = Vec::new();
    let mut buf = vec![0u8; 2048];
    for sequence in 1..=u64::from(options.count) {
        let frame = XBondFrame::new(
            XBondHeader::new(
                PacketKind::Heartbeat,
                options.session_id,
                sequence,
                now_micros(),
                options.path_id,
            ),
            b"ping".to_vec(),
        );
        socket.send(&frame.encode_sealed(&key)?).await?;

        let deadline = Duration::from_millis(options.timeout_ms);
        match time::timeout(deadline, socket.recv(&mut buf)).await {
            Ok(Ok(len)) => {
                if let Ok(reply) = XBondFrame::decode_sealed(&buf[..len], &key) {
                    if reply.header.kind == PacketKind::Heartbeat
                        && reply.header.session_id == options.session_id
                        && reply.header.sequence == sequence
                        && reply.payload == b"ack"
                    {
                        let elapsed =
                            now_micros().saturating_sub(reply.header.send_micros) as f64 / 1_000.0;
                        replies.push(PingReply {
                            sequence,
                            rtt_ms: elapsed,
                        });
                    }
                }
            }
            Ok(Err(error)) => return Err(error.into()),
            Err(_) => {}
        }

        if sequence < u64::from(options.count) && options.interval_ms > 0 {
            time::sleep(Duration::from_millis(options.interval_ms)).await;
        }
    }

    Ok(PingResult {
        server: options.server,
        bind: socket.local_addr()?.to_string(),
        path_id: options.path_id,
        session_id: options.session_id,
        sent: options.count,
        received: replies.len() as u32,
        replies,
    })
}

fn load_status(config_path: &PathBuf) -> Result<XBondStatus> {
    let config = read_config(config_path)?;
    let runtime = read_runtime_status(&config)?;
    let health = if runtime.paths.is_empty() {
        config_health(&config)
    } else {
        runtime.paths.clone()
    };
    let roles = select_path_roles(&health, config.max_active_backups);
    let schedule = build_schedule(config.mode, &roles);
    let paths = roles.into_iter().map(XBondPathStatus::from).collect();

    Ok(XBondStatus {
        enabled: config.enabled,
        running: runtime.running,
        mode: config.mode,
        server_addr: config.server_addr,
        anchor_path_id: schedule.anchor_path_id,
        schedule,
        paths,
        duplicate_packets_dropped: runtime.duplicate_packets_dropped,
        fec_packets_recovered: runtime.fec_packets_recovered,
        late_packets_dropped: runtime.late_packets_dropped,
        message: runtime.message.unwrap_or_else(|| {
            if config.runtime_status_path.is_some() {
                "XBond prototype status is config-derived until the runtime status file exists."
                    .to_string()
            } else {
                "XBond prototype status is config-derived; no runtime status path is configured."
                    .to_string()
            }
        }),
    })
}

fn read_config(config_path: &PathBuf) -> Result<ClientConfig> {
    if !config_path.exists() {
        return Ok(ClientConfig::default());
    }

    let text = std::fs::read_to_string(config_path)
        .with_context(|| format!("failed to read {}", config_path.display()))?;
    toml::from_str(&text).with_context(|| format!("failed to parse {}", config_path.display()))
}

fn read_runtime_status(config: &ClientConfig) -> Result<XBondRuntimeStatus> {
    let Some(path) = &config.runtime_status_path else {
        return Ok(XBondRuntimeStatus::default());
    };
    let runtime_path = PathBuf::from(path);
    if !runtime_path.exists() {
        return Ok(XBondRuntimeStatus::default());
    }

    let text = std::fs::read_to_string(&runtime_path)
        .with_context(|| format!("failed to read {}", runtime_path.display()))?;
    toml_or_json_runtime_status(&text)
        .with_context(|| format!("failed to parse {}", runtime_path.display()))
}

fn toml_or_json_runtime_status(text: &str) -> Result<XBondRuntimeStatus> {
    if text.trim_start().starts_with('{') {
        Ok(serde_json::from_str(text)?)
    } else {
        Ok(toml::from_str(text)?)
    }
}

fn config_health(config: &ClientConfig) -> Vec<PathHealthSnapshot> {
    config
        .paths
        .iter()
        .map(|path| PathHealthSnapshot {
            path_id: path.id,
            name: path.name.clone(),
            interface_name: path.interface_name.clone(),
            rtt_ms: None,
            jitter_ms: None,
            loss_rate: 0.0,
            late_rate: 0.0,
            queue_depth: 0,
            throughput_bps: 0,
            interface_up: path.enabled,
            in_cooldown: false,
        })
        .collect()
}

fn print_human_status(status: &XBondStatus) {
    println!("XBond enabled: {}", status.enabled);
    println!("XBond running: {}", status.running);
    println!("Mode: {:?}", status.mode);
    println!("Server: {}", status.server_addr);
    println!("Anchor path: {:?}", status.anchor_path_id);
    println!("{}", status.message);
    for path in &status.paths {
        println!(
            "- {} ({:?}) role={:?} score={:.1}",
            path.name, path.interface_name, path.role, path.score
        );
    }
}

fn print_ping_result(result: &PingResult) {
    println!("XBond ping server: {}", result.server);
    println!("Local bind: {}", result.bind);
    println!("Path: {}", result.path_id);
    println!(
        "Packets: sent={}, received={}, lost={}, loss={:.1}%",
        result.sent,
        result.received,
        result.lost(),
        result.loss_rate() * 100.0
    );
    if let (Some(min), Some(avg), Some(max)) = (
        result.min_rtt_ms(),
        result.avg_rtt_ms(),
        result.max_rtt_ms(),
    ) {
        println!("RTT: min={min:.1} ms avg={avg:.1} ms max={max:.1} ms");
    }
}

fn finite_min(values: &[f64]) -> Option<f64> {
    values
        .iter()
        .copied()
        .filter(|value| value.is_finite())
        .reduce(f64::min)
}

fn finite_max(values: &[f64]) -> Option<f64> {
    values
        .iter()
        .copied()
        .filter(|value| value.is_finite())
        .reduce(f64::max)
}

fn now_micros() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_micros().min(u128::from(u64::MAX)) as u64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runtime_status_json_parses_observed_counters_and_paths() {
        let runtime = toml_or_json_runtime_status(
            r#"{
                "running": true,
                "duplicate_packets_dropped": 7,
                "fec_packets_recovered": 2,
                "late_packets_dropped": 3,
                "message": "live",
                "paths": [
                    {
                        "path_id": 1,
                        "name": "fiber",
                        "interface_name": "eth0",
                        "rtt_ms": 12.0,
                        "jitter_ms": 1.0,
                        "loss_rate": 0.0,
                        "late_rate": 0.0,
                        "queue_depth": 0,
                        "throughput_bps": 1000000,
                        "interface_up": true,
                        "in_cooldown": false
                    }
                ]
            }"#,
        )
        .unwrap();

        assert!(runtime.running);
        assert_eq!(runtime.duplicate_packets_dropped, 7);
        assert_eq!(runtime.fec_packets_recovered, 2);
        assert_eq!(runtime.late_packets_dropped, 3);
        assert_eq!(runtime.paths[0].name, "fiber");
    }

    #[test]
    fn ping_result_reports_loss_and_rtt_summary() {
        let result = PingResult {
            server: "127.0.0.1:8444".to_string(),
            bind: "0.0.0.0:0".to_string(),
            path_id: 2,
            session_id: 99,
            sent: 4,
            received: 2,
            replies: vec![
                PingReply {
                    sequence: 1,
                    rtt_ms: 10.0,
                },
                PingReply {
                    sequence: 3,
                    rtt_ms: 20.0,
                },
            ],
        };

        assert_eq!(result.lost(), 2);
        assert_eq!(result.loss_rate(), 0.5);
        assert_eq!(result.min_rtt_ms(), Some(10.0));
        assert_eq!(result.avg_rtt_ms(), Some(15.0));
        assert_eq!(result.max_rtt_ms(), Some(20.0));
    }
}
