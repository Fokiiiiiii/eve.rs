//! Keep the client protocol values in sync; they affect handshakes, cache CRCs and bound-object addresses.

/// The client's "birthday": the release the protocol belongs to.
pub const BIRTHDAY: i64 = 170_472;
/// MachoNet protocol version.
pub const MACHO_VERSION: i64 = 496;
/// The client version and build this server targets.
pub const BOOT_VERSION: f64 = 24.01;
pub const BOOT_BUILD: i64 = 3_396_210;
pub const PROJECT_VERSION: &str = "V24.01@ccp";
pub const PROJECT_CODENAME: &str = "eve.rs";
pub const PROJECT_REGION: &str = "ccp";
/// The proxy node the front answers as (0xFFAA), the default node id.
pub const PROXY_NODE_ID: u32 = 0xFFAA;
/// The client-expected `challenge_responsehash`; it is not the CRC of the empty challenge.
pub const CHALLENGE_RESPONSE_HASH: &str = "55087";
