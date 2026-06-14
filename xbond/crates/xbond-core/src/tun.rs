use std::fs::File;
use std::io::{self, Read, Write};

#[derive(Debug)]
pub struct XBondTun {
    name: String,
    file: File,
}

impl XBondTun {
    pub fn open(name: &str, mtu: u16) -> io::Result<Self> {
        open_platform_tun(name, mtu)
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn try_clone(&self) -> io::Result<Self> {
        Ok(Self {
            name: self.name.clone(),
            file: self.file.try_clone()?,
        })
    }

    pub fn read_packet(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        self.file.read(buf)
    }

    pub fn write_packet(&mut self, packet: &[u8]) -> io::Result<()> {
        self.file.write_all(packet)
    }
}

#[cfg(target_os = "linux")]
fn open_platform_tun(name: &str, _mtu: u16) -> io::Result<XBondTun> {
    use std::ffi::CString;
    use std::os::fd::FromRawFd;

    const TUNSETIFF: libc::c_ulong = 0x4004_54ca;
    const IFF_TUN: libc::c_short = 0x0001;
    const IFF_NO_PI: libc::c_short = 0x1000;
    const IFNAMSIZ: usize = 16;

    if name.is_empty() || name.len() >= IFNAMSIZ {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "TUN device name must be 1-15 bytes",
        ));
    }

    let device = CString::new("/dev/net/tun").expect("static TUN path is valid");
    let fd = unsafe { libc::open(device.as_ptr(), libc::O_RDWR | libc::O_CLOEXEC) };
    if fd < 0 {
        let error = io::Error::last_os_error();
        return Err(if error.kind() == io::ErrorKind::PermissionDenied {
            io::Error::new(
                io::ErrorKind::PermissionDenied,
                "opening /dev/net/tun requires root or CAP_NET_ADMIN",
            )
        } else {
            error
        });
    }

    let mut ifr = [0u8; 40];
    ifr[..name.len()].copy_from_slice(name.as_bytes());
    let flags = (IFF_TUN | IFF_NO_PI).to_ne_bytes();
    ifr[IFNAMSIZ..IFNAMSIZ + flags.len()].copy_from_slice(&flags);

    let rc = unsafe { libc::ioctl(fd, TUNSETIFF, ifr.as_mut_ptr()) };
    if rc < 0 {
        let error = io::Error::last_os_error();
        unsafe {
            libc::close(fd);
        }
        return Err(if error.kind() == io::ErrorKind::PermissionDenied {
            io::Error::new(
                io::ErrorKind::PermissionDenied,
                "creating a TUN interface requires root or CAP_NET_ADMIN",
            )
        } else {
            error
        });
    }

    let file = unsafe { File::from_raw_fd(fd) };
    let actual_name = ifr
        .iter()
        .take_while(|value| **value != 0)
        .copied()
        .collect::<Vec<_>>();
    let actual_name = String::from_utf8_lossy(&actual_name).to_string();

    Ok(XBondTun {
        name: actual_name,
        file,
    })
}

#[cfg(not(target_os = "linux"))]
fn open_platform_tun(name: &str, _mtu: u16) -> io::Result<XBondTun> {
    let _ = name;
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "XBond TUN devices are only implemented on Linux",
    ))
}

pub fn is_ipv4_packet(packet: &[u8]) -> bool {
    packet.first().map(|byte| byte >> 4) == Some(4)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ipv4_validator_checks_version_nibble() {
        assert!(is_ipv4_packet(&[0x45, 0, 0, 20]));
        assert!(!is_ipv4_packet(&[0x60, 0, 0, 20]));
        assert!(!is_ipv4_packet(&[]));
    }
}
