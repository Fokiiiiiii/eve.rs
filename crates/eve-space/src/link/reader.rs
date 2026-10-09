use super::*;

impl std::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Empty => write!(f, "empty frame"),
            Self::UnknownKind(kind) => write!(f, "unknown message kind {kind}"),
            Self::Truncated => write!(f, "the frame ends before the message does"),
            Self::Trailing => write!(f, "bytes follow the end of the message"),
            Self::Utf8 => write!(f, "text is not UTF-8"),
            Self::BadValue(what) => write!(f, "bad {what}"),
        }
    }
}

impl<'a> Reader<'a> {
    pub(super) fn take(&mut self, count: usize) -> Result<&'a [u8], DecodeError> {
        if self.data.len() < count {
            return Err(DecodeError::Truncated);
        }
        let (head, rest) = self.data.split_at(count);
        self.data = rest;
        Ok(head)
    }

    pub(super) fn array<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        Ok(self.take(N)?.try_into().expect("took N bytes"))
    }

    pub(super) fn u8(&mut self) -> Result<u8, DecodeError> {
        Ok(self.array::<1>()?[0])
    }

    pub(super) fn u16(&mut self) -> Result<u16, DecodeError> {
        Ok(u16::from_le_bytes(self.array()?))
    }

    pub(super) fn u32(&mut self) -> Result<u32, DecodeError> {
        Ok(u32::from_le_bytes(self.array()?))
    }

    pub(super) fn i32(&mut self) -> Result<i32, DecodeError> {
        Ok(i32::from_le_bytes(self.array()?))
    }

    pub(super) fn u64(&mut self) -> Result<u64, DecodeError> {
        Ok(u64::from_le_bytes(self.array()?))
    }

    pub(super) fn i64(&mut self) -> Result<i64, DecodeError> {
        Ok(i64::from_le_bytes(self.array()?))
    }

    pub(super) fn f64(&mut self) -> Result<f64, DecodeError> {
        Ok(f64::from_le_bytes(self.array()?))
    }

    pub(super) fn text(&mut self) -> Result<String, DecodeError> {
        let length = self.u16()? as usize;
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| DecodeError::Utf8)
    }

    pub(super) fn bytes(&mut self) -> Result<Vec<u8>, DecodeError> {
        let length = self.u32()? as usize;
        Ok(self.take(length)?.to_vec())
    }

    pub(super) fn finish(&self) -> Result<(), DecodeError> {
        if self.data.is_empty() { Ok(()) } else { Err(DecodeError::Trailing) }
    }
}
