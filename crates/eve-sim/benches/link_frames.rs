use std::hint::black_box;
use std::time::{Duration, Instant};

use eve_space::link::Message;

const BATCH_SIZE: usize = 64;
const ITERATIONS: usize = 100_000;
const SAMPLES: usize = 5;

fn measure(messages: &[Message], encode: impl Fn(&Message, &mut Vec<u8>)) -> (Duration, usize) {
    let started = Instant::now();
    let mut bytes = 0usize;
    for _ in 0..ITERATIONS {
        let mut batch = Vec::new();
        for message in messages {
            encode(message, &mut batch);
        }
        bytes = bytes.wrapping_add(black_box(batch.len()));
        black_box(batch);
    }
    (started.elapsed(), bytes / ITERATIONS)
}

fn median_ns(samples: &mut [u128]) -> u128 {
    samples.sort_unstable();
    samples[samples.len() / 2]
}

fn main() {
    let messages = (0..BATCH_SIZE).map(|_| Message::HelloAck { version: 1, poll_hz: 10 }).collect::<Vec<_>>();
    let mut old_samples = Vec::with_capacity(SAMPLES);
    let mut new_samples = Vec::with_capacity(SAMPLES);
    let mut old_bytes = 0;
    let mut new_bytes = 0;

    for _ in 0..SAMPLES {
        let (elapsed, bytes) = measure(&messages, |message, batch| {
            batch.extend_from_slice(&message.encode());
        });
        old_samples.push(elapsed.as_nanos() / ITERATIONS as u128);
        old_bytes = bytes;

        let (elapsed, bytes) = measure(&messages, |message, batch| {
            message.encode_into(batch);
        });
        new_samples.push(elapsed.as_nanos() / ITERATIONS as u128);
        new_bytes = bytes;
    }

    assert_eq!(old_bytes, new_bytes);
    println!(
        "path=Message::encode batch_size={BATCH_SIZE} iterations_per_sample={ITERATIONS} samples={SAMPLES} median_ns_per_batch={} bytes_per_batch={old_bytes}",
        median_ns(&mut old_samples),
    );
    println!(
        "path=Message::encode_into batch_size={BATCH_SIZE} iterations_per_sample={ITERATIONS} samples={SAMPLES} median_ns_per_batch={} bytes_per_batch={new_bytes}",
        median_ns(&mut new_samples),
    );
}
