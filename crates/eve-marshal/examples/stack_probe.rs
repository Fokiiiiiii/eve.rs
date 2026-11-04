//! Finds the smallest thread stack that decodes a value nested MAX_DEPTH deep.
//!
//!   cargo run -q --release --example stack_probe -p eve-marshal
//!
//! The decoder is recursive, so its worst-case stack use is
//! `MAX_DEPTH * bytes-per-level`. Keep that comfortably under the smallest
//! stack a caller may run it on (2 MiB for the default test and tokio worker
//! threads).

use eve_marshal::{MAX_DEPTH, decode};

fn nested(shape: &str) -> Vec<u8> {
    let mut bytes = vec![0x7e, 0, 0, 0, 0];
    let depth = MAX_DEPTH - 1;
    match shape {
        "tuple" => bytes.extend(std::iter::repeat_n(0x25u8, depth)),
        "list" => bytes.extend(std::iter::repeat_n(0x27u8, depth)),
        // dict: size 1, then the value (nested), then a key - every level nests in the value slot.
        "dict" => {
            for _ in 0..depth {
                bytes.extend_from_slice(&[0x16, 1]);
            }
        }
        "object" => bytes.extend(std::iter::repeat_n(0x17u8, depth)),
        "packed" => bytes.extend(std::iter::repeat_n(0x2au8, depth)),
        "objectex" => bytes.extend(std::iter::repeat_n(0x22u8, depth)),
        _ => unreachable!(),
    }
    bytes.push(0x01);
    bytes
}

fn survives(stack: usize, bytes: &[u8]) -> bool {
    let bytes = bytes.to_vec();
    std::thread::Builder::new()
        .stack_size(stack)
        .spawn(move || {
            let _ = decode(&bytes);
        })
        .map(|handle| handle.join().is_ok())
        .unwrap_or(false)
}

fn main() {
    // A failed probe overflows the stack, which aborts the process; so each
    // probe runs in a child process (re-invoking this binary).
    let args: Vec<String> = std::env::args().collect();
    if args.len() == 4 && args[1] == "--child" {
        let stack: usize = args[2].parse().unwrap();
        let ok = survives(stack, &nested(&args[3]));
        std::process::exit(if ok { 0 } else { 1 });
    }

    let exe = std::env::current_exe().unwrap();
    for shape in ["tuple", "list", "dict", "object", "packed", "objectex"] {
        let (mut low, mut high) = (16 * 1024usize, 16 * 1024 * 1024usize);
        while high - low > 4 * 1024 {
            let mid = (low + high) / 2;
            let status = std::process::Command::new(&exe)
                .args(["--child", &mid.to_string(), shape])
                .stderr(std::process::Stdio::null())
                .status()
                .unwrap();
            if status.success() { high = mid } else { low = mid }
        }
        println!(
            "{shape:>6}: needs ~{:>5} KiB for depth {} (~{} bytes/level)",
            high / 1024,
            MAX_DEPTH - 1,
            high / (MAX_DEPTH - 1)
        );
    }
}
