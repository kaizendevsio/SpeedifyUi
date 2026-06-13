use thiserror::Error;

const FEC_V1_MAGIC: [u8; 4] = *b"XBF1";
const FEC_V1_HEADER_LEN: usize = 18;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct XorFecBlock {
    pub base_sequence: u64,
    pub first_len: usize,
    pub second_len: usize,
    pub parity: Vec<u8>,
}

impl XorFecBlock {
    pub fn encode(
        base_sequence: u64,
        first_payload: &[u8],
        second_payload: &[u8],
    ) -> Result<Vec<u8>, FecError> {
        let first_len = u16::try_from(first_payload.len())
            .map_err(|_| FecError::PayloadTooLarge(first_payload.len()))?;
        let second_len = u16::try_from(second_payload.len())
            .map_err(|_| FecError::PayloadTooLarge(second_payload.len()))?;
        let parity_len = first_payload.len().max(second_payload.len());
        let parity_len_u16 =
            u16::try_from(parity_len).map_err(|_| FecError::PayloadTooLarge(parity_len))?;

        let mut payload = Vec::with_capacity(FEC_V1_HEADER_LEN + parity_len);
        payload.extend_from_slice(&FEC_V1_MAGIC);
        payload.extend_from_slice(&base_sequence.to_be_bytes());
        payload.extend_from_slice(&first_len.to_be_bytes());
        payload.extend_from_slice(&second_len.to_be_bytes());
        payload.extend_from_slice(&parity_len_u16.to_be_bytes());
        for index in 0..parity_len {
            let first = first_payload.get(index).copied().unwrap_or_default();
            let second = second_payload.get(index).copied().unwrap_or_default();
            payload.push(first ^ second);
        }

        Ok(payload)
    }

    pub fn decode(payload: &[u8]) -> Result<Self, FecError> {
        if payload.len() < FEC_V1_HEADER_LEN {
            return Err(FecError::FrameTooShort(payload.len()));
        }
        if payload[0..4] != FEC_V1_MAGIC {
            return Err(FecError::BadMagic);
        }

        let base_sequence = u64::from_be_bytes(payload[4..12].try_into().unwrap());
        let first_len = u16::from_be_bytes(payload[12..14].try_into().unwrap()) as usize;
        let second_len = u16::from_be_bytes(payload[14..16].try_into().unwrap()) as usize;
        let parity_len = u16::from_be_bytes(payload[16..18].try_into().unwrap()) as usize;
        let expected_len = FEC_V1_HEADER_LEN + parity_len;
        if payload.len() != expected_len {
            return Err(FecError::LengthMismatch {
                expected: expected_len,
                actual: payload.len(),
            });
        }
        if parity_len < first_len.max(second_len) {
            return Err(FecError::ParityTooShort {
                parity_len,
                first_len,
                second_len,
            });
        }

        Ok(Self {
            base_sequence,
            first_len,
            second_len,
            parity: payload[FEC_V1_HEADER_LEN..].to_vec(),
        })
    }

    pub fn recover_missing(
        &self,
        known_sequence: u64,
        known_payload: &[u8],
    ) -> Option<(u64, Vec<u8>)> {
        let (missing_sequence, expected_known_len, missing_len) =
            if known_sequence == self.base_sequence {
                (self.base_sequence + 1, self.first_len, self.second_len)
            } else if known_sequence == self.base_sequence + 1 {
                (self.base_sequence, self.second_len, self.first_len)
            } else {
                return None;
            };

        if known_payload.len() != expected_known_len {
            return None;
        }

        let mut recovered = Vec::with_capacity(missing_len);
        for index in 0..missing_len {
            let known = known_payload.get(index).copied().unwrap_or_default();
            recovered.push(self.parity[index] ^ known);
        }

        Some((missing_sequence, recovered))
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum FecError {
    #[error("FEC frame too short: {0} bytes")]
    FrameTooShort(usize),
    #[error("bad FEC magic")]
    BadMagic,
    #[error("FEC frame length mismatch: expected {expected}, actual {actual}")]
    LengthMismatch { expected: usize, actual: usize },
    #[error(
        "FEC parity is too short: parity={parity_len}, first={first_len}, second={second_len}"
    )]
    ParityTooShort {
        parity_len: usize,
        first_len: usize,
        second_len: usize,
    },
    #[error("FEC payload too large: {0} bytes")]
    PayloadTooLarge(usize),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xor_fec_recovers_first_packet_from_second() {
        let first = b"hello stable path";
        let second = b"bad-link parity";
        let encoded = XorFecBlock::encode(11, first, second).unwrap();
        let block = XorFecBlock::decode(&encoded).unwrap();

        let recovered = block.recover_missing(12, second).unwrap();

        assert_eq!(recovered.0, 11);
        assert_eq!(recovered.1, first);
    }

    #[test]
    fn xor_fec_recovers_second_packet_from_first_with_different_lengths() {
        let first = b"short";
        let second = b"this packet is longer";
        let encoded = XorFecBlock::encode(20, first, second).unwrap();
        let block = XorFecBlock::decode(&encoded).unwrap();

        let recovered = block.recover_missing(20, first).unwrap();

        assert_eq!(recovered.0, 21);
        assert_eq!(recovered.1, second);
    }

    #[test]
    fn xor_fec_rejects_wrong_known_sequence() {
        let encoded = XorFecBlock::encode(20, b"first", b"second").unwrap();
        let block = XorFecBlock::decode(&encoded).unwrap();

        assert!(block.recover_missing(22, b"second").is_none());
    }
}
