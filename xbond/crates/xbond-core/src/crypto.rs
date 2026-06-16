use chacha20poly1305::aead::{Aead, AeadInPlace, KeyInit, Payload};
use chacha20poly1305::{Key, Tag, XChaCha20Poly1305, XNonce};
use thiserror::Error;

use crate::protocol::{PacketKind, XBondHeader};

pub const TAG_LEN: usize = 16;

#[derive(Clone)]
pub struct XBondKey {
    cipher: XChaCha20Poly1305,
}

impl std::fmt::Debug for XBondKey {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.debug_struct("XBondKey").finish_non_exhaustive()
    }
}

impl XBondKey {
    pub fn from_passphrase(passphrase: &str) -> Self {
        let hash = blake3::hash(passphrase.as_bytes());
        Self {
            cipher: XChaCha20Poly1305::new(Key::from_slice(hash.as_bytes())),
        }
    }

    pub fn seal(&self, header: &XBondHeader, plaintext: &[u8]) -> Result<Vec<u8>, CryptoError> {
        let nonce = nonce_for(header);
        self.cipher
            .encrypt(
                &nonce,
                Payload {
                    msg: plaintext,
                    aad: &associated_data(header),
                },
            )
            .map_err(|_| CryptoError::SealFailed)
    }

    pub fn open(&self, header: &XBondHeader, ciphertext: &[u8]) -> Result<Vec<u8>, CryptoError> {
        let nonce = nonce_for(header);
        self.cipher
            .decrypt(
                &nonce,
                Payload {
                    msg: ciphertext,
                    aad: &associated_data(header),
                },
            )
            .map_err(|_| CryptoError::OpenFailed)
    }

    pub fn seal_in_place_detached(
        &self,
        header: &XBondHeader,
        plaintext: &mut [u8],
    ) -> Result<[u8; TAG_LEN], CryptoError> {
        let nonce = nonce_for(header);
        let aad = associated_data(header);
        let tag = self
            .cipher
            .encrypt_in_place_detached(&nonce, &aad, plaintext)
            .map_err(|_| CryptoError::SealFailed)?;
        let mut tag_bytes = [0u8; TAG_LEN];
        tag_bytes.copy_from_slice(tag.as_slice());
        Ok(tag_bytes)
    }

    pub fn open_in_place_detached(
        &self,
        header: &XBondHeader,
        ciphertext: &mut [u8],
        tag: &[u8],
    ) -> Result<(), CryptoError> {
        if tag.len() != TAG_LEN {
            return Err(CryptoError::OpenFailed);
        }

        let nonce = nonce_for(header);
        let aad = associated_data(header);
        self.cipher
            .decrypt_in_place_detached(&nonce, &aad, ciphertext, Tag::from_slice(tag))
            .map_err(|_| CryptoError::OpenFailed)
    }
}

fn nonce_for(header: &XBondHeader) -> XNonce {
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"xbond-frame-nonce-v1");
    hasher.update(&header.session_id.to_be_bytes());
    hasher.update(&header.sequence.to_be_bytes());
    hasher.update(&header.path_id.to_be_bytes());
    hasher.update(&[header.kind.as_u8(), header.flags]);
    let hash = hasher.finalize();
    let mut nonce = [0u8; 24];
    nonce.copy_from_slice(&hash.as_bytes()[..24]);
    XNonce::from(nonce)
}

fn associated_data(header: &XBondHeader) -> [u8; 28] {
    let mut data = [0u8; 28];
    data[0..8].copy_from_slice(&header.session_id.to_be_bytes());
    data[8..16].copy_from_slice(&header.sequence.to_be_bytes());
    data[16..24].copy_from_slice(&header.send_micros.to_be_bytes());
    data[24..26].copy_from_slice(&header.path_id.to_be_bytes());
    data[26] = match header.kind {
        PacketKind::Data => 1,
        PacketKind::Duplicate => 2,
        PacketKind::Fec => 3,
        PacketKind::Heartbeat => 4,
        PacketKind::Control => 5,
    };
    data[27] = header.flags;
    data
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum CryptoError {
    #[error("failed to seal frame")]
    SealFailed,
    #[error("failed to open frame")]
    OpenFailed,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::{PacketKind, XBondHeader};

    #[test]
    fn sealed_payload_round_trips() {
        let key = XBondKey::from_passphrase("test-key");
        let header = XBondHeader::new(PacketKind::Data, 99, 123, 500, 1);

        let sealed = key.seal(&header, b"payload").unwrap();
        assert_ne!(sealed, b"payload");

        let opened = key.open(&header, &sealed).unwrap();
        assert_eq!(opened, b"payload");
    }

    #[test]
    fn cached_cipher_keeps_wire_compatible_output() {
        let cached_key = XBondKey::from_passphrase("test-key");
        let independently_cached_key = XBondKey::from_passphrase("test-key");
        let header = XBondHeader::new(PacketKind::Duplicate, 99, 123, 500, 2);

        let sealed = cached_key.seal(&header, b"payload").unwrap();
        let sealed_from_independent_key =
            independently_cached_key.seal(&header, b"payload").unwrap();

        assert_eq!(sealed, sealed_from_independent_key);
        assert_eq!(
            independently_cached_key.open(&header, &sealed).unwrap(),
            b"payload"
        );
    }

    #[test]
    fn detached_in_place_round_trip_matches_seal_output() {
        let key = XBondKey::from_passphrase("test-key");
        let header = XBondHeader::new(PacketKind::Data, 99, 123, 500, 1);
        let sealed = key.seal(&header, b"payload").unwrap();
        let mut plaintext = b"payload".to_vec();

        let tag = key
            .seal_in_place_detached(&header, plaintext.as_mut_slice())
            .unwrap();
        plaintext.extend_from_slice(&tag);

        assert_eq!(plaintext, sealed);

        let split_at = plaintext.len() - TAG_LEN;
        let (ciphertext, tag) = plaintext.split_at_mut(split_at);
        key.open_in_place_detached(&header, ciphertext, tag)
            .unwrap();

        assert_eq!(ciphertext, b"payload");
    }

    #[test]
    fn wrong_header_rejects_ciphertext() {
        let key = XBondKey::from_passphrase("test-key");
        let header = XBondHeader::new(PacketKind::Data, 99, 123, 500, 1);
        let wrong_header = XBondHeader::new(PacketKind::Data, 99, 124, 500, 1);

        let sealed = key.seal(&header, b"payload").unwrap();

        assert_eq!(
            key.open(&wrong_header, &sealed),
            Err(CryptoError::OpenFailed)
        );
    }
}
