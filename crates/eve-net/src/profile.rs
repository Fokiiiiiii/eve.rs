//! What the client checks about the server it logs in to: the packaged client build this server targets.
//! (The reference keeps the same values in `network/protocolProfile.js`; changing one in isolation
//! invalidates handshakes, object-cache CRCs and bound-object addresses.)

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
/// The `challenge_responsehash` the server sends in `CryptoServerHandshake`, a constant of the reference
/// server that the real client accepts. It is not the CRC of the (empty) challenge, and nothing else the
/// server sends depends on it; a headless client ignores it.
pub const CHALLENGE_RESPONSE_HASH: &str = "55087";
