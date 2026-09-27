use serde::{Deserialize, Serialize};
use std::fmt;

/// The error a failed call rejects with in the frontend.
///
/// It serializes as `{ "message": ..., "type": ... }`; the generated TypeScript runtime turns
/// `type` into `Error.name`. Failures inside .NET use the exception type name, failures of the
/// plugin or host use a short identifier such as `HostNotConfigured`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BridgeError {
  pub message: String,
  #[serde(rename = "type", default)]
  pub kind: String,
}

impl BridgeError {
  pub fn new(kind: impl Into<String>, message: impl Into<String>) -> Self {
    Self {
      kind: kind.into(),
      message: message.into(),
    }
  }
}

impl fmt::Display for BridgeError {
  fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
    write!(f, "{}: {}", self.kind, self.message)
  }
}

impl std::error::Error for BridgeError {}
