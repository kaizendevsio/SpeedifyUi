use anyhow::Result;
use clap::Parser;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::net::UdpSocket;
use xbond_core::{
    is_ipv4_packet, CanaryTun, FrameReceiver, PacketKind, ReceiveOutcome, XBondFrame, XBondHeader,
    XBondKey,
};

#[derive(Debug, Parser)]
#[command(name = "xbond-server")]
#[command(about = "XBond server prototype")]
struct Args {
    #[arg(long, default_value = "0.0.0.0:8444")]
    bind: String,

    #[arg(long, default_value = "XBOND_PSK")]
    key_env: String,

    #[arg(long, default_value_t = 120)]
    realtime_deadline_ms: u64,

    #[arg(long)]
    json_events: bool,

    #[arg(long)]
    tun_name: Option<String>,

    #[arg(long, default_value_t = 1400)]
    tun_mtu: u16,
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let key_text = std::env::var(&args.key_env)?;
    let key = XBondKey::from_passphrase(&key_text);
    let socket = UdpSocket::bind(&args.bind).await?;
    let mut receiver = FrameReceiver::new(args.realtime_deadline_ms * 1_000, 8192);
    let mut tun = match args.tun_name.as_deref() {
        Some(name) => Some(CanaryTun::open(name, args.tun_mtu)?),
        None => None,
    };
    let mut data_packets_received = 0u64;
    let mut data_packets_forwarded = 0u64;
    let mut non_ipv4_packets_dropped = 0u64;
    let mut buf = vec![0u8; 2048];

    if args.json_events {
        println!(
            "{}",
            serde_json::json!({
                "event": "listening",
                "bind": args.bind,
                "tun": tun.as_ref().map(|tun| tun.name()),
            })
        );
    } else {
        println!("xbond-server listening on {}", args.bind);
    }
    loop {
        let (len, peer) = socket.recv_from(&mut buf).await?;
        let Ok(frame) = XBondFrame::decode_sealed(&buf[..len], &key) else {
            continue;
        };

        let should_ack = matches!(
            frame.header.kind,
            PacketKind::Heartbeat | PacketKind::Control
        );
        let outcome = receiver.observe(&frame, now_micros());
        let ack_sent = should_ack
            && matches!(
                outcome,
                ReceiveOutcome::Accepted | ReceiveOutcome::Duplicate
            );
        if ack_sent {
            let reply = build_ack_frame(&frame);
            let encoded = reply.encode_sealed(&key)?;
            socket.send_to(&encoded, peer).await?;
        }

        if args.json_events {
            print_packet_event(
                event_name(outcome),
                outcome,
                &frame,
                &peer.to_string(),
                ack_sent,
                &receiver,
            );
        }

        let mut forwarded = false;
        let mut dropped_reason = None;
        if outcome == ReceiveOutcome::Accepted && is_data_like(frame.header.kind) {
            data_packets_received += 1;
            if is_ipv4_packet(&frame.payload) {
                if let Some(tun) = &mut tun {
                    tun.write_packet(&frame.payload)?;
                    data_packets_forwarded += 1;
                    forwarded = true;
                }
            } else {
                non_ipv4_packets_dropped += 1;
                dropped_reason = Some("payload is not an IPv4 packet");
            }
        }

        if args.json_events && is_data_like(frame.header.kind) {
            print_data_event(
                &frame,
                forwarded,
                dropped_reason,
                data_packets_received,
                data_packets_forwarded,
                non_ipv4_packets_dropped,
            );
        }

        if outcome != ReceiveOutcome::Accepted {
            continue;
        }
    }
}

fn build_ack_frame(frame: &XBondFrame) -> XBondFrame {
    XBondFrame::new(
        XBondHeader::new(
            frame.header.kind,
            frame.header.session_id,
            frame.header.sequence,
            frame.header.send_micros,
            0,
        ),
        b"ack".to_vec(),
    )
}

fn event_name(outcome: ReceiveOutcome) -> &'static str {
    match outcome {
        ReceiveOutcome::Accepted => "first-arrival",
        ReceiveOutcome::Duplicate => "duplicate-dropped",
        ReceiveOutcome::Expired => "late-dropped",
    }
}

fn is_data_like(kind: PacketKind) -> bool {
    matches!(kind, PacketKind::Data | PacketKind::Duplicate)
}

fn print_packet_event(
    event: &str,
    outcome: ReceiveOutcome,
    frame: &XBondFrame,
    peer: &str,
    ack_sent: bool,
    receiver: &FrameReceiver,
) {
    let stats = receiver.stats();
    println!(
        "{}",
        serde_json::json!({
            "event": event,
            "outcome": format!("{outcome:?}"),
            "session_id": frame.header.session_id,
            "sequence": frame.header.sequence,
            "path_id": frame.header.path_id,
            "packet_kind": frame.header.kind,
            "peer": peer,
            "ack_sent": ack_sent,
            "counters": {
                "first_arrivals": stats.accepted_packets,
                "duplicates_dropped": stats.duplicate_packets_dropped,
                "late_packets_dropped": stats.late_packets_dropped,
            }
        })
    );
}

fn print_data_event(
    frame: &XBondFrame,
    forwarded: bool,
    dropped_reason: Option<&str>,
    data_packets_received: u64,
    data_packets_forwarded: u64,
    non_ipv4_packets_dropped: u64,
) {
    println!(
        "{}",
        serde_json::json!({
            "event": "data-decapsulated",
            "session_id": frame.header.session_id,
            "sequence": frame.header.sequence,
            "path_id": frame.header.path_id,
            "packet_kind": frame.header.kind,
            "bytes": frame.payload.len(),
            "forwarded_to_tun": forwarded,
            "dropped_reason": dropped_reason,
            "counters": {
                "data_packets_received": data_packets_received,
                "data_packets_forwarded": data_packets_forwarded,
                "non_ipv4_packets_dropped": non_ipv4_packets_dropped,
            }
        })
    );
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
    fn ack_preserves_heartbeat_or_control_kind() {
        for kind in [PacketKind::Heartbeat, PacketKind::Control] {
            let frame = XBondFrame::new(XBondHeader::new(kind, 1, 2, 3, 4), b"hello".to_vec());
            let ack = build_ack_frame(&frame);

            assert_eq!(ack.header.kind, kind);
            assert_eq!(ack.header.sequence, frame.header.sequence);
            assert_eq!(ack.payload, b"ack");
        }
    }

    #[test]
    fn server_wire_path_uses_sealed_frames() {
        let key = XBondKey::from_passphrase("test-key");
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Heartbeat, 1, 2, 3, 4),
            b"heartbeat".to_vec(),
        );

        let encoded = frame.encode_sealed(&key).unwrap();
        assert!(XBondFrame::decode(&encoded).is_ok());
        assert!(!encoded.windows(9).any(|window| window == b"heartbeat"));
        assert_eq!(XBondFrame::decode_sealed(&encoded, &key).unwrap(), frame);
    }

    #[test]
    fn data_and_duplicate_are_tunnel_payload_kinds() {
        assert!(is_data_like(PacketKind::Data));
        assert!(is_data_like(PacketKind::Duplicate));
        assert!(!is_data_like(PacketKind::Heartbeat));
        assert!(!is_data_like(PacketKind::Fec));
    }
}
