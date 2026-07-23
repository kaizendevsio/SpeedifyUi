use std::collections::HashMap;
use std::fs;

use crate::status::XBondKernelNetworkStatus;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct LinuxKernelNetworkSnapshot {
    pub udp_in_errors: u64,
    pub udp_rcvbuf_errors: u64,
    pub udp_sndbuf_errors: u64,
    pub udp_no_ports: u64,
    pub tunnel_rx_drops: u64,
    pub tunnel_tx_drops: u64,
}

impl LinuxKernelNetworkSnapshot {
    pub fn delta(self, baseline: Self) -> XBondKernelNetworkStatus {
        XBondKernelNetworkStatus {
            udp_in_errors: self.udp_in_errors.saturating_sub(baseline.udp_in_errors),
            udp_rcvbuf_errors: self
                .udp_rcvbuf_errors
                .saturating_sub(baseline.udp_rcvbuf_errors),
            udp_sndbuf_errors: self
                .udp_sndbuf_errors
                .saturating_sub(baseline.udp_sndbuf_errors),
            udp_no_ports: self.udp_no_ports.saturating_sub(baseline.udp_no_ports),
            tunnel_rx_drops: self
                .tunnel_rx_drops
                .saturating_sub(baseline.tunnel_rx_drops),
            tunnel_tx_drops: self
                .tunnel_tx_drops
                .saturating_sub(baseline.tunnel_tx_drops),
        }
    }
}

pub fn read_linux_kernel_network_status(tunnel_name: Option<&str>) -> LinuxKernelNetworkSnapshot {
    let mut snapshot = LinuxKernelNetworkSnapshot::default();
    if let Ok(contents) = fs::read_to_string("/proc/net/snmp") {
        let mut lines = contents.lines();
        while let Some(headers) = lines.next() {
            if !headers.starts_with("Udp:") {
                continue;
            }
            let Some(values) = lines.next().filter(|line| line.starts_with("Udp:")) else {
                continue;
            };
            let map: HashMap<&str, u64> = headers
                .split_whitespace()
                .skip(1)
                .zip(values.split_whitespace().skip(1))
                .filter_map(|(key, value)| value.parse().ok().map(|value| (key, value)))
                .collect();
            snapshot.udp_in_errors = map.get("InErrors").copied().unwrap_or_default();
            snapshot.udp_rcvbuf_errors = map.get("RcvbufErrors").copied().unwrap_or_default();
            snapshot.udp_sndbuf_errors = map.get("SndbufErrors").copied().unwrap_or_default();
            snapshot.udp_no_ports = map.get("NoPorts").copied().unwrap_or_default();
            break;
        }
    }
    if let Some(name) = tunnel_name {
        snapshot.tunnel_rx_drops = read_interface_counter(name, "rx_dropped");
        snapshot.tunnel_tx_drops = read_interface_counter(name, "tx_dropped");
    }
    snapshot
}

fn read_interface_counter(interface: &str, counter: &str) -> u64 {
    if interface.is_empty()
        || !interface
            .bytes()
            .all(|value| value.is_ascii_alphanumeric() || matches!(value, b'-' | b'_' | b'.'))
    {
        return 0;
    }
    fs::read_to_string(format!("/sys/class/net/{interface}/statistics/{counter}"))
        .ok()
        .and_then(|value| value.trim().parse().ok())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::LinuxKernelNetworkSnapshot;

    #[test]
    fn kernel_network_deltas_saturate_after_counter_reset() {
        let current = LinuxKernelNetworkSnapshot {
            udp_rcvbuf_errors: 4,
            tunnel_tx_drops: 2,
            ..LinuxKernelNetworkSnapshot::default()
        };
        let baseline = LinuxKernelNetworkSnapshot {
            udp_rcvbuf_errors: 9,
            tunnel_tx_drops: 1,
            ..LinuxKernelNetworkSnapshot::default()
        };
        let delta = current.delta(baseline);
        assert_eq!(delta.udp_rcvbuf_errors, 0);
        assert_eq!(delta.tunnel_tx_drops, 1);
    }
}
