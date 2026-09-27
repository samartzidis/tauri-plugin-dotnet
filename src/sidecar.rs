//! A [`DotNetHost`] that runs the .NET backend as a separate child process (dev only).
//!
//! Unlike [`crate::HostfxrHost`], which loads .NET inside this process, `SidecarHost` spawns the
//! backend as a child process (against a private copy of its folder, made with [`crate::shadow`] the
//! same way `HostfxrHost` makes its own) and talks to it over a local socket (a named pipe on
//! Windows, a Unix domain socket elsewhere). Because the ORIGINAL build output is never opened by
//! this process - only the copy is - a running `dotnet build` always succeeds; on a rebuild the host
//! just kills the old child and starts a new one against a fresh copy of the new dll, with no
//! restart of the Tauri app and no reload of the frontend.
//!
//! The wire JSON exchanged with `BridgeDispatcher` (`{"callId", "method", "args"}` requests,
//! `{"callId", "result"|"error"}` responses, `{"event", "data"}` events) is unchanged from the
//! in-process host; it travels verbatim inside an [`Envelope`] that only exists to frame messages
//! over the socket and correlate responses to calls.

use std::{
  collections::HashMap,
  io,
  path::{Path, PathBuf},
  sync::{Arc, Mutex, OnceLock},
  time::Duration,
};

use interprocess::local_socket::{
  tokio::{prelude::*, RecvHalf, SendHalf},
  GenericNamespaced, ListenerOptions, ToNsName,
};
use serde::{Deserialize, Serialize};
use tokio::{
  io::{AsyncReadExt, AsyncWriteExt},
  process::{Child, Command},
  sync::mpsc,
};

use crate::{
  error::BridgeError,
  host::{error_response, Completion, DotNetHost, EventSink, OnStartError, STOPPED},
  shadow,
};

/// A call landed while the sidecar was being killed and respawned after a rebuild. Unlike
/// [`STOPPED`] (the app is exiting), a `SidecarHost` recovers from this on its own; the caller can
/// simply retry.
const RESTARTING: &str = "HostRestarting";
/// The child process ended without a respawn having been requested (a crash, or an unhandled
/// exception in the backend). The host is left [`State::Failed`] until the next successful rebuild.
const SIDECAR_CRASHED: &str = "HostSidecarCrashed";
/// The sidecar could not be started, for example because the sidecar-runner tool the package's
/// build targets copy next to the backend dll is missing, or the process failed to launch.
const INIT_FAILED: &str = "HostInitFailed";

const DEFAULT_SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(5);
const DEFAULT_SPAWN_TIMEOUT: Duration = Duration::from_secs(10);
const DEFAULT_RESTART_DEBOUNCE: Duration = Duration::from_millis(300);
/// Frames larger than this are rejected as a protocol error rather than allocated.
const MAX_FRAME_LEN: u32 = 64 * 1024 * 1024;

/// How to find and run the .NET backend as a sidecar process.
#[derive(Debug, Clone)]
pub struct SidecarOptions {
  backend_dll: PathBuf,
  dotnet_root: Option<PathBuf>,
  shutdown_timeout: Duration,
  spawn_timeout: Duration,
  restart_debounce: Duration,
  watch: bool,
  on_start_error: OnStartError,
}

impl SidecarOptions {
  /// `backend_dll` is the backend's main assembly (for example `MyApp.Backend.dll`). The package's
  /// build targets copy the prebuilt sidecar-runner tool (`Tauri.Plugin.DotNet.SidecarHost.dll`)
  /// next to it on every Debug build; it travels inside the same shadow copy as the backend and is
  /// launched from there, never from this original build output.
  pub fn new(backend_dll: impl Into<PathBuf>) -> Self {
    Self {
      backend_dll: backend_dll.into(),
      dotnet_root: None,
      shutdown_timeout: DEFAULT_SHUTDOWN_TIMEOUT,
      spawn_timeout: DEFAULT_SPAWN_TIMEOUT,
      restart_debounce: DEFAULT_RESTART_DEBOUNCE,
      watch: true,
      on_start_error: OnStartError::default(),
    }
  }

  /// The `dotnet` installation the sidecar process runs under. Without this, `dotnet` is resolved
  /// from `PATH`, same as running `dotnet <tool.dll>` by hand.
  pub fn dotnet_root(mut self, root: impl Into<PathBuf>) -> Self {
    self.dotnet_root = Some(root.into());
    self
  }

  /// How long to wait, when the app exits or a rebuild triggers a respawn, for the .NET side to
  /// finish before the process is killed outright. The default is 5 seconds, the same as
  /// [`crate::HostfxrOptions::shutdown_timeout`].
  pub fn shutdown_timeout(mut self, timeout: Duration) -> Self {
    self.shutdown_timeout = timeout;
    self
  }

  /// How long to wait for the child process to connect and complete its handshake before treating
  /// the start (or a respawn) as failed. Default 10 seconds.
  pub fn spawn_timeout(mut self, timeout: Duration) -> Self {
    self.spawn_timeout = timeout;
    self
  }

  /// How long to wait, after the backend dll changes, for the burst of writes a build produces to
  /// settle before restarting the sidecar. Default 300 milliseconds.
  pub fn restart_debounce(mut self, debounce: Duration) -> Self {
    self.restart_debounce = debounce;
    self
  }

  /// Whether to watch the backend dll for changes and restart the sidecar automatically. Default
  /// on; turn off to manage restarts some other way.
  pub fn watch(mut self, enabled: bool) -> Self {
    self.watch = enabled;
    self
  }

  /// What happens when the sidecar cannot be started. See
  /// [`crate::HostfxrOptions::on_start_error`] for the same option on the in-process host.
  pub fn on_start_error(mut self, action: OnStartError) -> Self {
    self.on_start_error = action;
    self
  }
}

/// A [`DotNetHost`] that runs .NET in a child process, restarting it on its own when the backend
/// dll is rebuilt. See the [module docs](self) for why this exists.
pub struct SidecarHost {
  shared: Arc<Shared>,
}

struct Shared {
  options: SidecarOptions,
  events: OnceLock<EventSink>,
  inner: Mutex<State>,
  /// Owns the file watcher for as long as the host exists, independent of `inner`, so it survives
  /// every respawn. Left unset when `SidecarOptions::watch` is off.
  watcher: OnceLock<notify::RecommendedWatcher>,
  stopped: std::sync::atomic::AtomicBool,
}

enum State {
  NotStarted,
  Ready(Box<Managed>),
  Restarting,
  Failed(BridgeError),
}

struct Managed {
  child: Child,
  writer_tx: mpsc::UnboundedSender<String>,
  pending: Arc<Mutex<HashMap<String, Completion>>>,
  reader_task: tauri::async_runtime::JoinHandle<()>,
  writer_task: tauri::async_runtime::JoinHandle<()>,
}

impl Managed {
  /// Ends the child, its IO tasks, and fails whatever was still waiting on it.
  async fn shut_down(mut self, reason: &str, message: &str) {
    self.reader_task.abort();
    self.writer_task.abort();
    let _ = self.child.start_kill();
    let _ = self.child.wait().await;
    fail_all(&self.pending, reason, message);
  }
}

/// Fails every completion still waiting in `pending` and clears it.
fn fail_all(pending: &Mutex<HashMap<String, Completion>>, kind: &str, message: &str) {
  let completions: Vec<Completion> = {
    let mut map = pending.lock().unwrap_or_else(|e| e.into_inner());
    map.drain().map(|(_, c)| c).collect()
  };
  for on_done in completions {
    on_done(error_response(&BridgeError::new(kind, message)));
  }
}

impl SidecarHost {
  pub fn new(options: SidecarOptions) -> Self {
    Self {
      shared: Arc::new(Shared {
        options,
        events: OnceLock::new(),
        inner: Mutex::new(State::NotStarted),
        watcher: OnceLock::new(),
        stopped: std::sync::atomic::AtomicBool::new(false),
      }),
    }
  }
}

impl DotNetHost for SidecarHost {
  fn start(&self, events: EventSink) -> Result<(), BridgeError> {
    let _ = self.shared.events.set(events);

    let result = tauri::async_runtime::block_on(spawn_and_handshake(&self.shared));
    let mut inner = self.shared.inner.lock().unwrap_or_else(|e| e.into_inner());
    match result {
      Ok(managed) => {
        *inner = State::Ready(Box::new(managed));
        drop(inner);
        if self.shared.options.watch {
          watch::start(Arc::clone(&self.shared));
        }
        Ok(())
      }
      Err(error) => {
        *inner = State::Failed(error.clone());
        Err(error)
      }
    }
  }

  fn call(&self, window_label: &str, request_json: String, on_done: Completion) {
    if self.shared.stopped.load(std::sync::atomic::Ordering::SeqCst) {
      on_done(error_response(&BridgeError::new(
        STOPPED,
        "The .NET backend has been shut down because the app is exiting.",
      )));
      return;
    }

    let inner = self.shared.inner.lock().unwrap_or_else(|e| e.into_inner());
    match &*inner {
      State::Ready(managed) => {
        let call_id = extract_call_id(&request_json);
        managed
          .pending
          .lock()
          .unwrap_or_else(|e| e.into_inner())
          .insert(call_id.clone(), on_done);
        let envelope = Envelope::Call {
          call_id,
          window_label: window_label.to_string(),
          json: request_json,
        };
        if managed
          .writer_tx
          .send(serde_json::to_string(&envelope).expect("Envelope always serializes"))
          .is_err()
        {
          // The writer task has already ended (the connection is going down); the reader task or
          // the restart logic will fail this completion via `fail_all` shortly.
        }
      }
      State::Restarting => on_done(error_response(&BridgeError::new(
        RESTARTING,
        "The .NET backend is restarting after a rebuild.",
      ))),
      State::Failed(error) => on_done(error_response(error)),
      State::NotStarted => on_done(error_response(&BridgeError::new(
        "HostNotStarted",
        "The .NET host has not been started.",
      ))),
    }
  }

  fn cancel(&self, call_id: &str) {
    let inner = self.shared.inner.lock().unwrap_or_else(|e| e.into_inner());
    if let State::Ready(managed) = &*inner {
      let envelope = Envelope::Cancel {
        call_id: call_id.to_string(),
      };
      let _ = managed
        .writer_tx
        .send(serde_json::to_string(&envelope).expect("Envelope always serializes"));
    }
  }

  fn stop(&self) {
    if self.shared.stopped.swap(true, std::sync::atomic::Ordering::SeqCst) {
      return;
    }
    let managed = {
      let mut inner = self.shared.inner.lock().unwrap_or_else(|e| e.into_inner());
      match std::mem::replace(&mut *inner, State::Restarting) {
        State::Ready(managed) => Some(managed),
        other => {
          *inner = other;
          None
        }
      }
    };
    let Some(mut managed) = managed else { return };

    let timeout = self.shared.options.shutdown_timeout;
    tauri::async_runtime::block_on(async move {
      let envelope = Envelope::Shutdown {
        timeout_ms: timeout.as_millis().min(i32::MAX as u128) as i32,
      };
      if let Ok(json) = serde_json::to_string(&envelope) {
        let _ = managed.writer_tx.send(json);
      }
      let wait = tokio::time::timeout(timeout, managed.child.wait()).await;
      if wait.is_err() {
        log::warn!("tauri-plugin-dotnet: the sidecar did not exit within {timeout:?}; killing it");
      }
      managed.shut_down(STOPPED, "The app is exiting.").await;
    });
  }

  fn on_start_error(&self) -> OnStartError {
    self.shared.options.on_start_error
  }
}

/// Reads the `callId` out of a call request without fully deserializing it, since the request body
/// is otherwise opaque to the sidecar transport (see the [module docs](self)).
fn extract_call_id(request_json: &str) -> String {
  #[derive(Deserialize)]
  struct JustCallId {
    #[serde(rename = "callId")]
    call_id: String,
  }
  serde_json::from_str::<JustCallId>(request_json)
    .map(|r| r.call_id)
    .unwrap_or_default()
}

/// One message on the sidecar's local socket. The `json` fields carry the existing, unchanged wire
/// format (`BridgeRequest`/`BridgeResponse`/event JSON) as an opaque string; only this envelope
/// itself is new.
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
enum Envelope {
  /// Sent by the child once the backend has loaded and the dispatcher is ready for calls.
  Ready,
  /// Sent by the child instead of `Ready` when the backend failed to load; the child exits after this.
  Error { message: String, r#type: String },
  #[serde(rename_all = "camelCase")]
  Call {
    call_id: String,
    window_label: String,
    json: String,
  },
  #[serde(rename_all = "camelCase")]
  Response { call_id: String, json: String },
  #[serde(rename_all = "camelCase")]
  Cancel { call_id: String },
  #[serde(rename_all = "camelCase")]
  Event {
    window_label: Option<String>,
    json: String,
  },
  #[serde(rename_all = "camelCase")]
  Shutdown { timeout_ms: i32 },
}

/// Writes one length-prefixed JSON frame.
async fn write_frame<W: tokio::io::AsyncWrite + Unpin>(w: &mut W, json: &str) -> io::Result<()> {
  let bytes = json.as_bytes();
  w.write_all(&(bytes.len() as u32).to_le_bytes()).await?;
  w.write_all(bytes).await?;
  Ok(())
}

/// Reads one length-prefixed JSON frame. `Ok(None)` means the stream ended cleanly between frames.
async fn read_frame<R: tokio::io::AsyncRead + Unpin>(r: &mut R) -> io::Result<Option<String>> {
  let mut len_bytes = [0u8; 4];
  match r.read_exact(&mut len_bytes).await {
    Ok(_) => {}
    Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
    Err(e) => return Err(e),
  }
  let len = u32::from_le_bytes(len_bytes);
  if len > MAX_FRAME_LEN {
    return Err(io::Error::new(
      io::ErrorKind::InvalidData,
      format!("frame of {len} bytes exceeds the {MAX_FRAME_LEN}-byte limit"),
    ));
  }
  let mut buf = vec![0u8; len as usize];
  r.read_exact(&mut buf).await?;
  String::from_utf8(buf)
    .map(Some)
    .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))
}

/// Where the sidecar-runner tool is: a sibling of `backend_dll`, copied there by the package's
/// build targets on every Debug build (`TauriDotNetCopySidecarHost` in
/// `Tauri.Plugin.DotNet.targets`). Called with the shadow-copied backend path (not the original
/// build output), so the tool is loaded from the copy too - see [`spawn_and_handshake`].
fn resolve_sidecar_tool_path(backend_dll: &Path) -> Result<PathBuf, BridgeError> {
  let tool_path = backend_dll.with_file_name("Tauri.Plugin.DotNet.SidecarHost.dll");
  if !tool_path.exists() {
    return Err(BridgeError::new(
      INIT_FAILED,
      format!(
        "The sidecar-runner tool was not found at {} - expected the package's build targets to \
         copy it there on every Debug build (see TauriDotNetSkipSidecar / TauriDotNetSidecarPath).",
        tool_path.display()
      ),
    ));
  }
  Ok(tool_path)
}

/// Spawns the sidecar process and waits for it to connect and hand over its dispatcher's readiness.
async fn spawn_and_handshake(shared: &Arc<Shared>) -> Result<Managed, BridgeError> {
  let options = &shared.options;

  // A private copy, so the original build output is never held open by this process: `dotnet build`
  // can always replace it, even for files a plain path-based load would keep locked for the sidecar's
  // whole lifetime (a native dependency, or - before this - the backend dll itself). Loading the copy
  // by path rather than from bytes also keeps `Assembly.Location` meaningful, which is what lets
  // native dependencies resolve at all (see BackendAssemblyResolver) and lets a debugger's source
  // paths bind normally, the same way they already do for `HostfxrHost`'s own shadow copy.
  log::debug!(
    "tauri-plugin-dotnet: copying the .NET backend from {} to a temporary folder",
    options.backend_dll.parent().unwrap_or(&options.backend_dll).display()
  );
  let started = std::time::Instant::now();
  let copy = shadow::create(&options.backend_dll).map_err(|e| {
    BridgeError::new(
      INIT_FAILED,
      format!(
        "Failed to copy the .NET backend from {} to a temporary folder: {e}",
        options.backend_dll.parent().unwrap_or(&options.backend_dll).display()
      ),
    )
  })?;
  log::info!(
    "tauri-plugin-dotnet: loading a copy of the sidecar backend ({} files, {:.0?}) from {}",
    copy.files,
    started.elapsed(),
    copy.folder.display()
  );

  // Resolved against the COPY, not `options.backend_dll`: the sidecar-runner tool was copied next
  // to the backend dll by the package's build targets (TauriDotNetCopySidecarHost), so it travelled
  // inside the same shadow copy and must be launched from there - never from the original build
  // output, or it would end up locked exactly like the backend dll used to be before shadow-copying.
  let tool_path = resolve_sidecar_tool_path(&copy.assembly)?;

  let pipe_name = format!(
    "tauri-plugin-dotnet-sidecar-{}-{}",
    std::process::id(),
    NEXT_SOCKET_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
  );
  let name = pipe_name
    .clone()
    .to_ns_name::<GenericNamespaced>()
    .map_err(|e| BridgeError::new(INIT_FAILED, format!("Failed to name the sidecar socket: {e}")))?;
  let listener = ListenerOptions::new()
    .name(name)
    .create_tokio()
    .map_err(|e| BridgeError::new(INIT_FAILED, format!("Failed to listen for the sidecar: {e}")))?;

  // `dotnet_root` selects which `dotnet` muxer starts the sidecar-runner (and therefore which
  // runtime it and the backend it loads run under) - by the time the runner's `Main` executes, the
  // muxer has already resolved everything, so this is a launch-time choice, not a CLI argument.
  let dotnet_exe: PathBuf = match &options.dotnet_root {
    Some(root) => root.join(if cfg!(windows) { "dotnet.exe" } else { "dotnet" }),
    None => PathBuf::from("dotnet"),
  };
  let mut command = Command::new(dotnet_exe);
  command
    .arg(&tool_path)
    .arg("--pipe")
    .arg(&pipe_name)
    .arg("--backend")
    .arg(&copy.assembly);
  let mut child = command
    .spawn()
    .map_err(|e| BridgeError::new(INIT_FAILED, format!("Failed to start the sidecar process: {e}")))?;

  let accept = async {
    tokio::select! {
      accepted = listener.accept() => accepted.map_err(|e| BridgeError::new(INIT_FAILED, format!("The sidecar failed to connect: {e}"))),
      status = child.wait() => match status {
        Ok(status) => Err(BridgeError::new(INIT_FAILED, format!("The sidecar process exited before connecting (status {status})"))),
        Err(e) => Err(BridgeError::new(INIT_FAILED, format!("Failed to wait for the sidecar process: {e}"))),
      },
    }
  };
  let stream = match tokio::time::timeout(options.spawn_timeout, accept).await {
    Ok(Ok(stream)) => stream,
    Ok(Err(error)) => {
      let _ = child.start_kill();
      return Err(error);
    }
    Err(_) => {
      let _ = child.start_kill();
      return Err(BridgeError::new(
        INIT_FAILED,
        format!("The sidecar did not connect within {:?}.", options.spawn_timeout),
      ));
    }
  };

  let (mut recv, send) = stream.split();
  let ready = tokio::time::timeout(options.spawn_timeout, read_frame(&mut recv)).await;
  match ready {
    Ok(Ok(Some(json))) => match serde_json::from_str::<Envelope>(&json) {
      Ok(Envelope::Ready) => {}
      Ok(Envelope::Error { message, r#type }) => {
        let _ = child.start_kill();
        return Err(BridgeError::new(r#type, message));
      }
      other => {
        let _ = child.start_kill();
        return Err(BridgeError::new(
          "HostProtocolError",
          format!("Unexpected handshake message from the sidecar: {other:?}"),
        ));
      }
    },
    Ok(Ok(None)) => {
      let _ = child.start_kill();
      return Err(BridgeError::new(
        INIT_FAILED,
        "The sidecar closed the connection before completing its handshake.",
      ));
    }
    Ok(Err(e)) => {
      let _ = child.start_kill();
      return Err(BridgeError::new(INIT_FAILED, format!("Failed to read the sidecar's handshake: {e}")));
    }
    Err(_) => {
      let _ = child.start_kill();
      return Err(BridgeError::new(
        INIT_FAILED,
        format!("The sidecar did not complete its handshake within {:?}.", options.spawn_timeout),
      ));
    }
  }

  let pending: Arc<Mutex<HashMap<String, Completion>>> = Arc::new(Mutex::new(HashMap::new()));
  let (writer_tx, writer_rx) = mpsc::unbounded_channel::<String>();
  let writer_task = tauri::async_runtime::spawn(run_writer(send, writer_rx));
  let events = shared.events.get().cloned();
  let reader_task = tauri::async_runtime::spawn(run_reader(
    recv,
    Arc::clone(&pending),
    events,
    Arc::clone(shared),
  ));

  Ok(Managed {
    child,
    writer_tx,
    pending,
    reader_task,
    writer_task,
  })
}

static NEXT_SOCKET_ID: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

/// Drains `rx` and frames each message onto the socket, one writer per connection so events, call
/// requests and cancellations never interleave mid-frame.
async fn run_writer(mut send: SendHalf, mut rx: mpsc::UnboundedReceiver<String>) {
  while let Some(json) = rx.recv().await {
    if write_frame(&mut send, &json).await.is_err() {
      break;
    }
  }
}

/// Reads frames until the connection ends, resolving pending calls and forwarding events. If the
/// connection ends on its own (not because [`SidecarHost::stop`] or a rebuild-triggered restart
/// aborted this task first), that is an unexpected crash: the host is marked [`State::Failed`] and
/// whatever was still pending is failed with [`SIDECAR_CRASHED`].
async fn run_reader(
  mut recv: RecvHalf,
  pending: Arc<Mutex<HashMap<String, Completion>>>,
  events: Option<EventSink>,
  shared: Arc<Shared>,
) {
  loop {
    match read_frame(&mut recv).await {
      Ok(Some(json)) => match serde_json::from_str::<Envelope>(&json) {
        Ok(Envelope::Response { call_id, json }) => {
          let completion = pending.lock().unwrap_or_else(|e| e.into_inner()).remove(&call_id);
          if let Some(on_done) = completion {
            on_done(json);
          }
        }
        Ok(Envelope::Event { window_label, json }) => {
          if let Some(events) = &events {
            events(window_label, json);
          }
        }
        Ok(other) => log::warn!("tauri-plugin-dotnet: unexpected sidecar message: {other:?}"),
        Err(e) => log::error!("tauri-plugin-dotnet: malformed sidecar message: {e}"),
      },
      Ok(None) => {
        handle_unexpected_close(&shared, &pending, "The sidecar closed the connection unexpectedly.");
        return;
      }
      Err(e) => {
        handle_unexpected_close(&shared, &pending, &format!("The sidecar connection failed: {e}"));
        return;
      }
    }
  }
}

/// Marks the host `Failed` and fails whatever was pending, but only if it is still `Ready` for this
/// same connection — a deliberate [`SidecarHost::stop`]/restart aborts this task before it can reach
/// here, so reaching this function at all means the child went away on its own.
fn handle_unexpected_close(shared: &Shared, pending: &Mutex<HashMap<String, Completion>>, message: &str) {
  let mut inner = shared.inner.lock().unwrap_or_else(|e| e.into_inner());
  if matches!(*inner, State::Ready(_)) {
    log::error!("tauri-plugin-dotnet: {message}");
    *inner = State::Failed(BridgeError::new(SIDECAR_CRASHED, message.to_string()));
    drop(inner);
    fail_all(pending, SIDECAR_CRASHED, message);
  }
}

/// Kills the current sidecar, if any, and starts a fresh one against the (presumably rebuilt)
/// backend dll. Called by the file watcher; safe to call even if the host is already `Failed`.
async fn restart(shared: &Arc<Shared>) {
  let started = std::time::Instant::now();
  let old = {
    let mut inner = shared.inner.lock().unwrap_or_else(|e| e.into_inner());
    std::mem::replace(&mut *inner, State::Restarting)
  };
  if let State::Ready(managed) = old {
    managed
      .shut_down(RESTARTING, "The .NET backend is restarting after a rebuild.")
      .await;
  }

  match spawn_and_handshake(shared).await {
    Ok(managed) => {
      log::info!(
        "tauri-plugin-dotnet: the sidecar restarted after a rebuild ({:.0?})",
        started.elapsed()
      );
      *shared.inner.lock().unwrap_or_else(|e| e.into_inner()) = State::Ready(Box::new(managed));
    }
    Err(error) => {
      log::error!(
        "tauri-plugin-dotnet: failed to restart the sidecar after {:.0?}: {error}",
        started.elapsed()
      );
      *shared.inner.lock().unwrap_or_else(|e| e.into_inner()) = State::Failed(error);
    }
  }
}

mod watch;

#[cfg(test)]
mod tests {
  use super::*;
  use std::time::Duration;

  #[test]
  fn options_have_the_documented_defaults() {
    let options = SidecarOptions::new("MyApp.Backend.dll");
    assert_eq!(options.shutdown_timeout, Duration::from_secs(5));
    assert_eq!(options.spawn_timeout, Duration::from_secs(10));
    assert_eq!(options.restart_debounce, Duration::from_millis(300));
    assert!(options.watch);
    assert_eq!(options.on_start_error, OnStartError::ShowDialogAndExit);
    assert!(options.dotnet_root.is_none());
  }

  #[test]
  fn options_builder_methods_override_the_defaults() {
    let options = SidecarOptions::new("MyApp.Backend.dll")
      .dotnet_root("C:/dotnet")
      .shutdown_timeout(Duration::from_secs(1))
      .spawn_timeout(Duration::from_secs(2))
      .restart_debounce(Duration::from_millis(50))
      .watch(false)
      .on_start_error(OnStartError::KeepRunning);

    assert_eq!(options.dotnet_root, Some(PathBuf::from("C:/dotnet")));
    assert_eq!(options.shutdown_timeout, Duration::from_secs(1));
    assert_eq!(options.spawn_timeout, Duration::from_secs(2));
    assert_eq!(options.restart_debounce, Duration::from_millis(50));
    assert!(!options.watch);
    assert_eq!(options.on_start_error, OnStartError::KeepRunning);
  }

  #[test]
  fn resolve_sidecar_tool_path_reports_a_missing_tool() {
    let dir = std::env::temp_dir().join(format!("tdn-sidecar-test-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let backend = dir.join("MyApp.Backend.dll");

    let error = resolve_sidecar_tool_path(&backend).unwrap_err();

    assert_eq!(error.kind, INIT_FAILED);
    assert!(error.message.contains("Tauri.Plugin.DotNet.SidecarHost.dll"));
    std::fs::remove_dir_all(&dir).ok();
  }

  #[test]
  fn resolve_sidecar_tool_path_returns_the_colocated_tool() {
    let dir = std::env::temp_dir().join(format!("tdn-sidecar-test-{}", std::process::id() as u64 + 1));
    std::fs::create_dir_all(&dir).unwrap();
    let backend = dir.join("MyApp.Backend.dll");
    let tool = dir.join("Tauri.Plugin.DotNet.SidecarHost.dll");
    std::fs::write(&tool, b"").unwrap();

    let resolved = resolve_sidecar_tool_path(&backend).unwrap();

    assert_eq!(resolved, tool);
    std::fs::remove_dir_all(&dir).ok();
  }

  #[test]
  fn envelopes_round_trip_through_json() {
    let cases = vec![
      Envelope::Ready,
      Envelope::Error {
        message: "boom".into(),
        r#type: "InvalidOperationException".into(),
      },
      Envelope::Call {
        call_id: "c1".into(),
        window_label: "main".into(),
        json: "{\"callId\":\"c1\",\"method\":\"Svc.Do\",\"args\":[]}".into(),
      },
      Envelope::Response {
        call_id: "c1".into(),
        json: "{\"callId\":\"c1\",\"result\":null}".into(),
      },
      Envelope::Cancel { call_id: "c1".into() },
      Envelope::Event {
        window_label: Some("main".into()),
        json: "{\"event\":\"progress\",\"data\":{}}".into(),
      },
      Envelope::Event {
        window_label: None,
        json: "{\"event\":\"closed\"}".into(),
      },
      Envelope::Shutdown { timeout_ms: 5000 },
    ];

    for case in cases {
      let json = serde_json::to_string(&case).unwrap();
      let round_tripped: Envelope = serde_json::from_str(&json).unwrap();
      assert_eq!(format!("{case:?}"), format!("{round_tripped:?}"));
    }
  }

  #[tokio::test]
  async fn a_frame_round_trips_through_an_in_memory_pipe() {
    let (mut a, mut b) = tokio::io::duplex(1024);
    write_frame(&mut a, "hello").await.unwrap();
    let received = read_frame(&mut b).await.unwrap();
    assert_eq!(received.as_deref(), Some("hello"));
  }

  #[tokio::test]
  async fn a_frame_exactly_at_the_buffer_boundary_round_trips() {
    let payload = "x".repeat(1024);
    let (mut a, mut b) = tokio::io::duplex(4096);
    write_frame(&mut a, &payload).await.unwrap();
    let received = read_frame(&mut b).await.unwrap();
    assert_eq!(received.as_deref(), Some(payload.as_str()));
  }

  #[tokio::test]
  async fn a_clean_close_between_frames_reads_as_none() {
    let (a, mut b) = tokio::io::duplex(1024);
    drop(a);
    let received = read_frame(&mut b).await.unwrap();
    assert_eq!(received, None);
  }

  #[tokio::test]
  async fn an_oversized_length_prefix_is_rejected() {
    let (mut a, mut b) = tokio::io::duplex(1024);
    a.write_all(&(MAX_FRAME_LEN + 1).to_le_bytes()).await.unwrap();
    let result = read_frame(&mut b).await;
    assert!(result.is_err());
  }

  #[test]
  fn extract_call_id_reads_the_call_id_field() {
    assert_eq!(
      extract_call_id("{\"callId\":\"abc\",\"method\":\"Svc.Do\",\"args\":[]}"),
      "abc"
    );
  }

  #[test]
  fn extract_call_id_defaults_when_absent_or_malformed() {
    assert_eq!(extract_call_id("not json"), "");
    assert_eq!(extract_call_id("{}"), "");
  }

  // --- Real-process tests: spawn the actual .NET sidecar-runner against the actual fixture backend.
  // Ignored by default (needs both built first via `dotnet build`, not just `cargo test`); run with
  // `cargo test --lib -- --ignored sidecar::tests::real_process`.

  fn fixture_backend_dll() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
      .join("dotnet/tests/fixtures/SidecarFixtureBackend/bin/Debug/net8.0/SidecarFixtureBackend.dll")
  }

  fn no_op_events() -> EventSink {
    Arc::new(|_window_label, _event_json| {})
  }

  /// Issues one call through the public `DotNetHost` API and blocks for its response.
  fn call_and_wait(host: &SidecarHost, call_id: &str, method: &str, args_json: &str) -> String {
    let request = format!(r#"{{"callId":"{call_id}","method":"{method}","args":{args_json}}}"#);
    let (tx, rx) = std::sync::mpsc::channel();
    host.call("main", request, Box::new(move |response| { let _ = tx.send(response); }));
    rx.recv_timeout(Duration::from_secs(10))
      .unwrap_or_else(|_| "<<no response within 10s>>".to_string())
  }

  fn child_pid(host: &SidecarHost) -> u32 {
    let inner = host.shared.inner.lock().unwrap_or_else(|e| e.into_inner());
    match &*inner {
      State::Ready(managed) => managed.child.id().expect("the child process has no pid"),
      other => panic!("expected the host to be Ready, was {other:?}"),
    }
  }

  #[cfg(windows)]
  fn kill_process_externally(pid: u32) {
    let status = std::process::Command::new("taskkill")
      .args(["/F", "/PID", &pid.to_string()])
      .status()
      .expect("failed to run taskkill");
    assert!(status.success(), "taskkill failed for pid {pid}");
  }

  #[cfg(not(windows))]
  fn kill_process_externally(pid: u32) {
    let status = std::process::Command::new("kill")
      .args(["-9", &pid.to_string()])
      .status()
      .expect("failed to run kill");
    assert!(status.success(), "kill -9 failed for pid {pid}");
  }

  impl std::fmt::Debug for State {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
      match self {
        State::NotStarted => write!(f, "NotStarted"),
        State::Ready(_) => write!(f, "Ready"),
        State::Restarting => write!(f, "Restarting"),
        State::Failed(e) => write!(f, "Failed({e})"),
      }
    }
  }

  #[test]
  #[ignore = "needs the .NET sidecar-runner and fixture backend built first (see fixture_backend_dll)"]
  fn real_process_answers_a_call() {
    let backend = fixture_backend_dll();
    assert!(
      backend.exists(),
      "fixture backend not built at {}; run `dotnet build dotnet/src/Tauri.Plugin.DotNet.SidecarHost` \
       and `dotnet build dotnet/tests/fixtures/SidecarFixtureBackend` first",
      backend.display()
    );

    let host = SidecarHost::new(SidecarOptions::new(backend).watch(false));
    host.start(no_op_events()).expect("the sidecar failed to start");

    let response = call_and_wait(&host, "c1", "EchoService.Echo", r#"["hello"]"#);
    assert!(response.contains("\"result\":\"hello\""), "unexpected response: {response}");

    host.stop();
  }

  #[test]
  #[ignore = "needs the .NET sidecar-runner and fixture backend built first (see fixture_backend_dll)"]
  fn real_process_crash_is_detected_and_fails_subsequent_calls() {
    let backend = fixture_backend_dll();
    assert!(backend.exists(), "fixture backend not built at {}", backend.display());

    let host = SidecarHost::new(SidecarOptions::new(backend).watch(false));
    host.start(no_op_events()).expect("the sidecar failed to start");

    // A working call first, to prove the crash (not a start-up failure) is what gets detected.
    let response = call_and_wait(&host, "c1", "EchoService.Echo", r#"["hello"]"#);
    assert!(response.contains("\"result\":\"hello\""), "unexpected response: {response}");

    // Kill the child directly, outside of SidecarHost's own stop/restart machinery, simulating a
    // real crash: the reader task must notice the closed connection on its own.
    let pid = child_pid(&host);
    kill_process_externally(pid);
    std::thread::sleep(Duration::from_millis(1000));

    let response = call_and_wait(&host, "c2", "EchoService.Echo", r#"["after crash"]"#);
    assert!(
      response.contains(SIDECAR_CRASHED),
      "expected a {SIDECAR_CRASHED} error, got: {response}"
    );

    let inner = host.shared.inner.lock().unwrap_or_else(|e| e.into_inner());
    assert!(
      matches!(&*inner, State::Failed(e) if e.kind == SIDECAR_CRASHED),
      "expected State::Failed({SIDECAR_CRASHED}), was {:?}",
      *inner
    );
  }

  #[test]
  #[ignore = "needs the .NET sidecar-runner and fixture backend built first (see fixture_backend_dll)"]
  fn real_process_does_not_lock_a_dependency_dll() {
    let backend = fixture_backend_dll();
    assert!(backend.exists(), "fixture backend not built at {}", backend.display());

    // Tauri.Plugin.DotNet.dll is a DEPENDENCY of the fixture backend, copied into its output folder
    // next to it. The sidecar never loads THIS copy at all - only a shadow copy of the whole folder
    // (src/shadow.rs) - so overwriting it while the sidecar runs must always succeed, exactly what a
    // `dotnet build` that refreshes this dependency (a plugin-library rebuild, a package bump) needs.
    let dependency_dll = backend.with_file_name("Tauri.Plugin.DotNet.dll");
    assert!(dependency_dll.exists(), "expected a copy of the plugin library at {}", dependency_dll.display());
    let library_dll = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
      .join("dotnet/src/Tauri.Plugin.DotNet/bin/Debug/net8.0/Tauri.Plugin.DotNet.dll");
    assert!(library_dll.exists(), "expected the plugin library to be built at {}", library_dll.display());

    let host = SidecarHost::new(SidecarOptions::new(backend).watch(false));
    host.start(no_op_events()).expect("the sidecar failed to start");

    let response = call_and_wait(&host, "c1", "EchoService.Echo", r#"["hello"]"#);
    assert!(response.contains("\"result\":\"hello\""), "unexpected response: {response}");

    // The actual repro: overwrite the dependency dll on disk while the sidecar that "loaded" it is
    // still running. `std::fs::copy` does a real open-for-write, so this fails with an IO error if the
    // file is locked - exactly the MSB3026 symptom this fix removes.
    std::fs::copy(&library_dll, &dependency_dll)
      .unwrap_or_else(|e| panic!("overwriting {} failed (would be locked): {e}", dependency_dll.display()));

    // And the sidecar itself is unaffected: its own copy is already fully loaded in memory.
    let response = call_and_wait(&host, "c2", "EchoService.Echo", r#"["still alive"]"#);
    assert!(response.contains("still alive"), "unexpected response: {response}");

    host.stop();
  }
}
