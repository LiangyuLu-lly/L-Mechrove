//! Slot-4 capture dump against FakeBroker. Never opens MQTT. Never uses Real.

mod common;

use common::{format_transcript, run_protocol_walk, transcript_path, write_transcript};

#[tokio::test]
async fn two_protocol_walks_write_byte_identical_sorted_transcripts() {
    // Given: two independent Fake backends
    // When: each performs the slot-4 protocol walk
    let first = format_transcript(&run_protocol_walk().await);
    let second = format_transcript(&run_protocol_walk().await);

    // Then: the canonical transcripts are byte-identical and tab-separated
    assert_eq!(first, second, "walk must be deterministic");
    assert!(
        first.lines().all(|line| line.contains('\t')),
        "each line must be topic<TAB>json:\n{first}"
    );
    let mut sorted = first.lines().collect::<Vec<_>>();
    sorted.sort();
    let actual: Vec<&str> = first.lines().collect();
    assert_eq!(actual, sorted, "lines must be sorted");

    let path = transcript_path();
    write_transcript(&path, &first);
    assert_eq!(
        std::fs::read_to_string(&path).expect("read dump"),
        first,
        "written transcript must match in-memory dump"
    );
}
