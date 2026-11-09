use crate::error::{MarshalError, Result};

const NO_ARGUMENT: &[u8] = &[
    0x28, 0x29, 0x30, 0x31, 0x32, 0x4e, 0x51, 0x52, 0x5d, 0x61, 0x62, 0x64, 0x65, 0x6c, 0x6f, 0x73, 0x74,
    0x75, 0x7d, 0x81, 0x85, 0x86, 0x87, 0x88, 0x89,
];

fn fixed_argument_size(opcode: u8) -> Option<usize> {
    Some(match opcode {
        0x47 => 8,
        0x4a => 4,
        0x4b => 1,
        0x4d => 2,
        0x68 => 1,
        0x6a => 4,
        0x71 => 1,
        0x72 => 4,
        0x80 => 1,
        0x82 => 1,
        0x83 => 2,
        0x84 => 4,
        _ => return None,
    })
}

fn line_argument_count(opcode: u8) -> Option<usize> {
    Some(match opcode {
        0x46 | 0x49 | 0x4c | 0x50 | 0x53 | 0x56 | 0x67 | 0x70 => 1,
        0x63 | 0x69 => 2,
        _ => return None,
    })
}

fn counted_length_size(opcode: u8) -> Option<usize> {
    Some(match opcode {
        0x54 | 0x58 => 4,
        0x55 | 0x8a => 1,
        0x8b => 4,
        _ => return None,
    })
}

/// Find Python's pickle STOP opcode while skipping opcode arguments that can
/// contain arbitrary bytes, including 0x2e.
pub(crate) fn find_end(bytes: &[u8], start: usize, end: usize) -> Result<usize> {
    let mut position = start;
    while position < end {
        let opcode = bytes[position];
        position += 1;
        if opcode == 0x2e {
            return Ok(position);
        }
        if NO_ARGUMENT.contains(&opcode) {
            continue;
        }
        if let Some(length) = fixed_argument_size(opcode) {
            position = position
                .checked_add(length)
                .filter(|next| *next <= end)
                .ok_or(MarshalError::UnexpectedEnd)?;
            continue;
        }
        if let Some(lines) = line_argument_count(opcode) {
            for _ in 0..lines {
                let line = bytes[position..end]
                    .iter()
                    .position(|byte| *byte == b'\n')
                    .ok_or(MarshalError::UnexpectedEnd)?;
                position += line + 1;
            }
            continue;
        }
        if let Some(width) = counted_length_size(opcode) {
            if width > end - position {
                return Err(MarshalError::UnexpectedEnd);
            }
            let length = if width == 1 {
                usize::from(bytes[position])
            } else {
                u32::from_le_bytes(bytes[position..position + 4].try_into().expect("four bytes")) as usize
            };
            let content_start = position + width;
            position = content_start
                .checked_add(length)
                .filter(|next| *next <= end)
                .ok_or(MarshalError::UnexpectedEnd)?;
            continue;
        }
        return Err(MarshalError::UnknownOpcode { opcode, position: position - 1 });
    }
    Err(MarshalError::UnexpectedEnd)
}
