use std::sync::Arc;

use crate::error::BridgeError;

/// Completes one call. Must be invoked exactly once with the serialized `BridgeResponse`
/// (`{ "callId": ..., "result": ... }` or `{ "callId": ..., "error": { "message", "type" } }`).
pub type Completion = Box<dyn FnOnce(String) + Send + 'static>;

/// Delivers an event pushed from .NET to the frontend: the target webview label
/// (`None` broadcasts to all webviews) and the serialized `{ "event": ..., "data": ... }` message.
pub type EventSink = Arc<dyn Fn(Option<String>, String) + Send + Sync + 'static>;

/// The connection to the .NET side of the bridge.
///
/// This is the seam between the plugin and however .NET is hosted (in-process via hostfxr, or a
/// sidecar process). The plugin owns the Tauri-facing protocol; an implementation only moves
/// strings to and from `BridgeDispatcher` on the .NET side.
pub trait DotNetHost: Send + Sync + 'static {
  /// Called once at plugin setup, before any call. Brings the .NET side up; the host keeps `events`
  /// and uses it to push events to the frontend.
  ///
  /// A host whose startup fails should remember the error and fail its calls with it, so the
  /// frontend gets a rejection instead of a hang.
  fn start(&self, events: EventSink) -> Result<(), BridgeError>;

  /// Starts processing a call and returns immediately; `on_done` is invoked when .NET has
  /// produced the response, from whatever thread that happens on.
  ///
  /// `window_label` is the label of the calling webview.
  fn call(&self, window_label: &str, request_json: String, on_done: Completion);

  /// Requests cancellation of an in-flight call. Unknown or completed call ids are ignored.
  fn cancel(&self, call_id: &str);

  /// Called once when the app exits (Tauri's `RunEvent::Exit`), on the thread that is ending the event loop.
  /// The host should tell the .NET side to stop taking calls and to release what it holds (close databases, flush
  /// files), and wait for that for a bounded time: the process ends right after, and nothing on the .NET side is
  /// told otherwise. Calls made after this fail with `HostStopped`.
  ///
  /// This is best effort. It does not run when the process is killed or crashes, or when Tauri restarts the app in
  /// development. The default does nothing.
  fn stop(&self) {}

  /// What the plugin does when [`start`](Self::start) fails. The default shows an error dialog and ends the app.
  fn on_start_error(&self) -> OnStartError {
    OnStartError::ShowDialogAndExit
  }
}

/// What the plugin does when the .NET side cannot be started, for example because the .NET runtime the backend
/// needs is not installed. Either way the error is logged.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum OnStartError {
  /// Shows an error dialog and ends the app with exit code 1, before any window is used. When the .NET runtime is
  /// missing, the dialog names the one to install; other failures are reported with their details. Without .NET
  /// nothing in the app would work, so this is the default.
  #[default]
  ShowDialogAndExit,
  /// Keeps the app running: every call then rejects with the error (`type` is `RuntimeMissing` or
  /// `HostInitFailed`), so the frontend can explain the problem itself.
  KeepRunning,
}

/// The error type of a call that reaches a host that has been stopped (see [`DotNetHost::stop`]).
pub(crate) const STOPPED: &str = "HostStopped";

/// Placeholder used until a real host is supplied through [`crate::Builder::host`].
/// Every call fails with a `HostNotConfigured` error instead of hanging.
pub(crate) struct NoHost;

impl DotNetHost for NoHost {
  fn start(&self, _events: EventSink) -> Result<(), BridgeError> {
    Ok(())
  }

  fn call(&self, _window_label: &str, _request_json: String, on_done: Completion) {
    on_done(error_response(&BridgeError::new(
      "HostNotConfigured",
      "No .NET host is configured for tauri-plugin-dotnet.",
    )));
  }

  fn cancel(&self, _call_id: &str) {}
}

/// The serialized response for a call that failed before reaching .NET.
pub(crate) fn error_response(error: &BridgeError) -> String {
  serde_json::json!({ "error": { "message": error.message, "type": error.kind } }).to_string()
}
