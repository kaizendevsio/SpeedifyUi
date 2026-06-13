use anyhow::Result;
use clap::Parser;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::net::UdpSocket;
use xbond_core::{FrameReceiver, PacketKind, ReceiveOutcome, XBondFrame, XBondHeader, XBondKey};

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
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let key_text = std::env::var(&args.key_env)?;
    let key = XBondKey::from_passphrase(&key_text);
    let socket = UdpSocket::bind(&args.bind).await?;
    let mut receiver = FrameReceiver::new(args.realtime_deadline_ms * 1_000, 8192);
    let mut buf = vec![0u8; 2048];

    println!("xbond-server listening on {}", args.bind);
    loop {
        let (len, peer) = socket.recv_from(&mut buf).await?;
        let Ok(frame) = XBondFrame::decode_sealed(&buf[..len], &key) else {
            continue;
        };

        if receiver.observe(&frame, now_micros()) != ReceiveOutcome::Accepted {
            continue;
        }

        if matches!(
            frame.header.kind,
            PacketKind::Heartbeat | PacketKind::Control
        ) {
            let reply = build_ack_frame(&frame);
            let encoded = reply.encode_sealed(&key)?;
            socket.send_to(&encoded, peer).await?;
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
}
