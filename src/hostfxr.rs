//! In-process .NET hosting through `hostfxr`.
//!
//! [`HostfxrHost`] loads the .NET runtime into the Tauri process and talks to
//! `Tauri.Plugin.DotNet.Hosting.NativeHost` on the .NET side through `[UnmanagedCallersOnly]`
//! entry points. Only pointers and lengths cross the boundary: each side copies what it receives,
//! so neither ever frees the other's memory.
//!
//! The backend is a framework-dependent .NET library (built with `EnableDynamicLoading`) that
//! contains one [`IBridgeBackend`] implementation; the runtime it needs must be installed, or
//! shipped in a `dotnet` folder next to the executable (see [`HostfxrOptions`]).
//!
//! [`IBridgeBackend`]: https://github.com/samartzidis/tauri-plugin-dotnet

use std::{
  cell::RefCell,
  cmp::Ordering,
  env,
  fs,
  panic::{catch_unwind, AssertUnwindSafe},
  path::{Path, PathBuf},
  rc::Rc,
  sync::{
    atomic::{AtomicBool, Ordering as AtomicOrdering},
    OnceLock,
  },
  time::Duration,
};

use netcorehost::{error::HostingError, hostfxr::Hostfxr, pdcstr, pdcstring::PdCString};

use crate::{
  bundle,
  error::BridgeError,
  host::{error_response, Completion, DotNetHost, EventSink, OnStartError, STOPPED},
};

/// Error type used for everything that goes wrong while bringing the runtime up, except a missing runtime.
const INIT_FAILED: &str = "HostInitFailed";

/// Error type used when the .NET runtime the backend needs is not installed. The first line of its message is meant
/// for the app's user; what follows is for the developer.
pub(crate) const RUNTIME_MISSING: &str = "RuntimeMissing";

/// The runtime that every backend needs, and that the ASP.NET Core and Windows Desktop runtimes include.
const BASE_FRAMEWORK: &str = "Microsoft.NETCore.App";

/// How long [`DotNetHost::stop`] waits for the .NET side by default.
const DEFAULT_SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(5);

const HOSTFXR_LIBRARY: &str = if cfg!(windows) {
  "hostfxr.dll"
} else if cfg!(target_os = "macos") {
  "libhostfxr.dylib"
} else {
  "libhostfxr.so"
};

// The signatures of `Tauri.Plugin.DotNet.Hosting.NativeHost`'s entry points. Strings are UTF-8
// (pointer + byte length). `usize` context values are opaque to .NET and handed back unchanged.
type DoneFn = extern "system" fn(context: usize, response: *const u8, response_len: i32);
type EmitFn = extern "system" fn(
  context: usize,
  window_label: *const u8,
  window_label_len: i32,
  event: *const u8,
  event_len: i32,
);
type InitializeFn = extern "system" fn(
  assembly_path: *const u8,
  assembly_path_len: i32,
  emit: EmitFn,
  emit_context: usize,
  error: *mut u8,
  error_capacity: i32,
) -> i32;
type CallFn = extern "system" fn(
  window_label: *const u8,
  window_label_len: i32,
  request: *const u8,
  request_len: i32,
  context: usize,
  done: DoneFn,
);
type CancelFn = extern "system" fn(call_id: *const u8, call_id_len: i32);
/// The package's version packed into an int: `major << 20 | minor << 10 | patch`, or a negative number when it cannot be
/// (see `BridgeVersion::unpack`).
type VersionFn = extern "system" fn() -> i32;
/// Blocks until .NET has stopped and disposed its services, or the timeout (milliseconds) has passed. Returns
/// `SHUTDOWN_COMPLETED`, `SHUTDOWN_TIMED_OUT` or `SHUTDOWN_FAILED`.
type ShutdownFn = extern "system" fn(timeout_ms: i32) -> i32;

// What `NativeHost.Shutdown` returns
const SHUTDOWN_COMPLETED: i32 = 0;
const SHUTDOWN_TIMED_OUT: i32 = 1;

/// Where the backend's files come from.
#[derive(Debug, Clone)]
enum Source {
  /// Files on disk, next to the backend assembly.
  Path(PathBuf),
  /// A backend bundle embedded in the executable (see [`HostfxrOptions::embedded`]).
  Embedded(&'static [u8]),
}

/// How to find and load the .NET backend.
#[derive(Debug, Clone)]
pub struct HostfxrOptions {
  source: Source,
  dotnet_root: Option<PathBuf>,
  shutdown_timeout: Duration,
  on_start_error: OnStartError,
}

impl HostfxrOptions {
  /// `assembly_path` is the backend's main assembly (for example `MyApp.Backend.dll`). Its
  /// `.runtimeconfig.json` and `.deps.json` must sit next to it, which is what building with
  /// `<EnableDynamicLoading>true</EnableDynamicLoading>` produces.
  ///
  /// The backend is loaded from where it is, so the running app holds its files open: on Windows
  /// `dotnet build` cannot replace them until the app has exited.
  pub fn new(assembly_path: impl Into<PathBuf>) -> Self {
    Self {
      source: Source::Path(assembly_path.into()),
      dotnet_root: None,
      shutdown_timeout: DEFAULT_SHUTDOWN_TIMEOUT,
      on_start_error: OnStartError::default(),
    }
  }

  /// How long to wait, when the app exits, for the .NET side to finish: it stops taking calls, cancels the ones in
  /// flight, disposes the services (`IDisposable` / `IAsyncDisposable`) and runs the backend's `ShutdownAsync`. The
  /// default is 5 seconds. When the time is up the app exits anyway, and nothing more is waited for. It blocks the
  /// thread that is ending the app, so keep it short.
  ///
  /// `Duration::ZERO` skips the shutdown: .NET is simply left behind when the process ends.
  pub fn shutdown_timeout(mut self, timeout: Duration) -> Self {
    self.shutdown_timeout = timeout;
    self
  }

  /// What happens when .NET cannot be started, for example because the runtime the backend needs is not installed.
  /// The default, [`OnStartError::ShowDialogAndExit`], tells the user in a dialog and ends the app; use
  /// [`OnStartError::KeepRunning`] to let the frontend handle the error instead.
  pub fn on_start_error(mut self, action: OnStartError) -> Self {
    self.on_start_error = action;
    self
  }

  /// Loads the backend from a bundle embedded in the executable, so no backend file has to ship
  /// next to it. Pass the bundle made by building the backend with `TauriDotNetEmbed=true`:
  ///
  /// ```ignore
  /// HostfxrOptions::embedded(include_bytes!("../../src-dotnet/MyApp.Backend/bin/Release/net8.0/MyApp.Backend.tdnbundle"))
  /// ```
  ///
  /// The .NET runtime still has to be installed (or shipped in a `dotnet` folder next to the
  /// executable), and it must be .NET 8 or newer. Only managed assemblies are embedded: a
  /// backend that depends on native libraries still needs those on disk. The assemblies load
  /// into .NET's default load context, and `Assembly.Location` is empty for them. hostfxr can
  /// only start from a `runtimeconfig.json` file, so a copy of the embedded one is written to a
  /// private folder in the system temp directory for the moment startup takes.
  pub fn embedded(bundle: &'static [u8]) -> Self {
    Self {
      source: Source::Embedded(bundle),
      dotnet_root: None,
      shutdown_timeout: DEFAULT_SHUTDOWN_TIMEOUT,
      on_start_error: OnStartError::default(),
    }
  }

  /// Uses the .NET installation in `root` and fails if it has no `hostfxr`, instead of searching.
  ///
  /// Without this the runtime is looked up, in order, in: the `DOTNET_ROOT_<ARCH>` and
  /// `DOTNET_ROOT` environment variables, a `dotnet` folder next to the executable (for shipping
  /// an app-local runtime), the folder of the first `dotnet` on `PATH`, and the platform's
  /// default install locations.
  pub fn dotnet_root(mut self, root: impl Into<PathBuf>) -> Self {
    self.dotnet_root = Some(root.into());
    self
  }
}

enum State {
  Ready(Managed),
  Failed(BridgeError),
}

/// The managed entry points, valid for the rest of the process (the runtime cannot be unloaded).
#[derive(Clone, Copy)]
struct Managed {
  call: CallFn,
  cancel: CancelFn,
  shutdown: ShutdownFn,
}

/// A [`DotNetHost`] that runs .NET inside the Tauri process.
pub struct HostfxrHost {
  options: HostfxrOptions,
  state: OnceLock<State>,
  /// Set once the app is exiting: calls are then rejected, and `stop` does not run twice.
  stopped: AtomicBool,
}

impl HostfxrHost {
  pub fn new(options: HostfxrOptions) -> Self {
    Self {
      options,
      state: OnceLock::new(),
      stopped: AtomicBool::new(false),
    }
  }
}

impl DotNetHost for HostfxrHost {
  fn start(&self, events: EventSink) -> Result<(), BridgeError> {
    let state = self.state.get_or_init(|| match load(&self.options, events) {
      Ok(managed) => State::Ready(managed),
      Err(error) => {
        log::error!("tauri-plugin-dotnet: {error}");
        State::Failed(error)
      }
    });

    match state {
      State::Ready(_) => Ok(()),
      State::Failed(error) => Err(error.clone()),
    }
  }

  fn call(&self, window_label: &str, request_json: String, on_done: Completion) {
    match self.state.get() {
      // The app is exiting: .NET has been (or is being) shut down, so it is not asked anything more
      Some(State::Ready(_)) if self.stopped.load(AtomicOrdering::SeqCst) => on_done(error_response(&BridgeError::new(
        STOPPED,
        "The .NET backend has been shut down because the app is exiting.",
      ))),
      Some(State::Ready(managed)) => {
        // Ownership of the completion moves to .NET until it calls `complete_call` (exactly once).
        let context = Box::into_raw(Box::new(on_done)) as usize;
        (managed.call)(
          window_label.as_ptr(),
          window_label.len() as i32,
          request_json.as_ptr(),
          request_json.len() as i32,
          context,
          complete_call,
        );
      }
      Some(State::Failed(error)) => on_done(error_response(error)),
      None => on_done(error_response(&BridgeError::new(
        "HostNotStarted",
        "The .NET host has not been started.",
      ))),
    }
  }

  fn cancel(&self, call_id: &str) {
    if let Some(State::Ready(managed)) = self.state.get() {
      (managed.cancel)(call_id.as_ptr(), call_id.len() as i32);
    }
  }

  fn stop(&self) {
    // Tauri sends the exit event once, but a second stop must never run the shutdown twice
    if self.stopped.swap(true, AtomicOrdering::SeqCst) {
      return;
    }
    let Some(State::Ready(managed)) = self.state.get() else {
      return;
    };
    let timeout = self.options.shutdown_timeout;
    if timeout.is_zero() {
      return;
    }

    let millis = timeout.as_millis().min(i32::MAX as u128) as i32;
    log::info!("tauri-plugin-dotnet: stopping the .NET backend (waiting up to {millis} ms)");
    // The wait is bounded on the .NET side too, so this returns even when a service's Dispose never does
    match catch_unwind(AssertUnwindSafe(|| (managed.shutdown)(millis))) {
      Ok(SHUTDOWN_COMPLETED) => log::debug!("tauri-plugin-dotnet: the .NET backend stopped"),
      Ok(SHUTDOWN_TIMED_OUT) => {
        log::warn!("tauri-plugin-dotnet: the .NET backend did not finish stopping within {millis} ms; exiting anyway")
      }
      Ok(code) => log::warn!("tauri-plugin-dotnet: stopping the .NET backend failed (code {code}); see the backend's log"),
      Err(_) => log::error!("tauri-plugin-dotnet: stopping the .NET backend panicked"),
    }
  }

  fn on_start_error(&self) -> OnStartError {
    self.options.on_start_error
  }
}

/// Reads `len` bytes at `ptr` as text, replacing invalid UTF-8.
///
/// # Safety
/// `ptr` must be valid for reads of `len` bytes, or `len` must be 0.
unsafe fn read_text(ptr: *const u8, len: i32) -> String {
  if ptr.is_null() || len <= 0 {
    return String::new();
  }
  String::from_utf8_lossy(unsafe { std::slice::from_raw_parts(ptr, len as usize) }).into_owned()
}

/// Called by .NET when a call has finished. `context` is the `Box<Completion>` made in `call`.
extern "system" fn complete_call(context: usize, response: *const u8, response_len: i32) {
  // SAFETY: `context` was produced by `Box::into_raw` in `call`, and .NET completes each call once.
  let completion = unsafe { Box::from_raw(context as *mut Completion) };
  let response = unsafe { read_text(response, response_len) };
  // A panic must not unwind into .NET.
  if catch_unwind(AssertUnwindSafe(|| completion(response))).is_err() {
    log::error!("tauri-plugin-dotnet: a call completion panicked");
  }
}

/// Called by .NET when a service emits an event. `context` is the leaked `EventSink` given to `Initialize`.
extern "system" fn forward_event(
  context: usize,
  window_label: *const u8,
  window_label_len: i32,
  event: *const u8,
  event_len: i32,
) {
  // SAFETY: `context` points to an `EventSink` that is leaked, i.e. valid for the rest of the process.
  let events = unsafe { &*(context as *const EventSink) };
  let window_label = (window_label_len > 0).then(|| unsafe { read_text(window_label, window_label_len) });
  let event = unsafe { read_text(event, event_len) };
  if catch_unwind(AssertUnwindSafe(|| events(window_label, event))).is_err() {
    log::error!("tauri-plugin-dotnet: the event sink panicked");
  }
}

fn init_error(message: impl Into<String>) -> BridgeError {
  BridgeError::new(INIT_FAILED, message)
}

/// The error for a runtime that is not installed: one line for the user naming what to install, then `details` for
/// the developer.
fn runtime_missing(framework: Option<&RequiredFramework>, details: &str) -> BridgeError {
  let needed = match framework {
    Some(framework) => framework.description(),
    None => format!(".NET runtime ({})", process_architecture()),
  };
  BridgeError::new(
    RUNTIME_MISSING,
    format!("This app needs the {needed}, which is not installed on this computer.\n{details}"),
  )
}

/// The shared framework a backend's `runtimeconfig.json` asks for, such as `Microsoft.NETCore.App` `8.0.0`.
#[derive(Debug, Clone, PartialEq, Eq)]
struct RequiredFramework {
  name: String,
  version: String,
}

impl RequiredFramework {
  /// The framework to install for this runtime config, or `None` when it names none. Of several, the one that is not
  /// the base runtime wins, since the ASP.NET Core and Windows Desktop runtimes include it.
  fn from_runtime_config(json: &[u8]) -> Option<Self> {
    let config: serde_json::Value = serde_json::from_slice(json).ok()?;
    let options = config.get("runtimeOptions")?;
    let entries: Vec<&serde_json::Value> = match options.get("frameworks").and_then(|f| f.as_array()) {
      Some(list) => list.iter().collect(),
      None => options.get("framework").into_iter().collect(),
    };
    let frameworks: Vec<Self> = entries
      .into_iter()
      .filter_map(|entry| {
        Some(Self {
          name: entry.get("name")?.as_str()?.to_string(),
          version: entry.get("version")?.as_str()?.to_string(),
        })
      })
      .collect();
    frameworks
      .iter()
      .find(|f| f.name != BASE_FRAMEWORK)
      .or(frameworks.first())
      .cloned()
  }

  /// `8.0` from `8.0.0`: what the installers are named after.
  fn channel(&self) -> Option<String> {
    let mut parts = self.version.split('.');
    let major: u32 = parts.next()?.parse().ok()?;
    let minor: u32 = parts.next()?.parse().ok()?;
    Some(format!("{major}.{minor}"))
  }

  /// What to install, as Microsoft names the installer: `.NET Runtime 8.0 (x64)`.
  fn description(&self) -> String {
    let product = match self.name.as_str() {
      BASE_FRAMEWORK => ".NET Runtime",
      "Microsoft.AspNetCore.App" => "ASP.NET Core Runtime",
      "Microsoft.WindowsDesktop.App" => ".NET Desktop Runtime",
      other => other,
    };
    let version = self.channel().unwrap_or_else(|| self.version.clone());
    format!("{product} {version} ({})", process_architecture())
  }
}

/// The architecture of this process as .NET names it: a 64-bit app needs the x64 runtime, even on an arm64 machine.
fn process_architecture() -> &'static str {
  match env::consts::ARCH {
    "x86_64" => "x64",
    "aarch64" => "arm64",
    other => other,
  }
}

/// A `major.minor.patch` version. Only the major and minor numbers matter for compatibility.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct BridgeVersion {
  major: u32,
  minor: u32,
  patch: u32,
}

impl std::fmt::Display for BridgeVersion {
  fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
    write!(f, "{}.{}.{}", self.major, self.minor, self.patch)
  }
}

impl BridgeVersion {
  /// The version of a package as `NativeHost.GetVersion` reports it, or `None` when it could not be packed (negative).
  fn unpack(packed: i32) -> Option<Self> {
    (packed >= 0).then(|| Self {
      major: (packed >> 20) as u32,
      minor: ((packed >> 10) & 1023) as u32,
      patch: (packed & 1023) as u32,
    })
  }

  /// Reads `1.2.3`, also `1.2` and `1.2.3-beta.1+build`: a pre-release or build suffix is ignored, and a missing patch is 0.
  fn parse(text: &str) -> Option<Self> {
    let numbers = text.split(['-', '+']).next()?;
    let mut parts = numbers.split('.');
    let major = parts.next()?.trim().parse().ok()?;
    let minor = parts.next()?.trim().parse().ok()?;
    let patch = match parts.next() {
      Some(part) => part.trim().parse().ok()?,
      None => 0,
    };
    Some(Self { major, minor, patch })
  }

  /// The pair that decides compatibility. The patch number is free, which is a promise that a patch release never changes
  /// the interface between the crate and the package.
  fn line(&self) -> (u32, u32) {
    (self.major, self.minor)
  }
}

/// Refuses a package whose major or minor version differs from this crate's, and says which of the two to update.
fn check_package_version(reported: i32) -> Result<(), BridgeError> {
  check_versions(reported, env!("CARGO_PKG_VERSION"))
}

fn check_versions(reported: i32, crate_version: &str) -> Result<(), BridgeError> {
  let ours = BridgeVersion::parse(crate_version)
    .ok_or_else(|| init_error(format!("tauri-plugin-dotnet has a version it cannot read: {crate_version}")))?;
  let Some(package) = BridgeVersion::unpack(reported) else {
    return Err(init_error(format!(
      "The Tauri.Plugin.DotNet package reports a version this crate cannot read. {}",
      update_package_hint(&ours)
    )));
  };

  if package.line() == ours.line() {
    return Ok(());
  }

  let advice = if package.line() < ours.line() {
    format!("The package is older: {}", update_package_hint(&ours))
  } else {
    format!(
      "The crate is older: update the tauri-plugin-dotnet crate to {}.{}.x, the version of the package.",
      package.major, package.minor
    )
  };
  Err(init_error(format!(
    "The Tauri.Plugin.DotNet package is version {package}, but tauri-plugin-dotnet {ours} needs a package with the same \
     major and minor version ({}.{}.x). {advice}",
    ours.major, ours.minor
  )))
}

fn update_package_hint(crate_version: &BridgeVersion) -> String {
  format!(
    "Update the Tauri.Plugin.DotNet NuGet package to {}.{}.x, the version of the tauri-plugin-dotnet crate.",
    crate_version.major, crate_version.minor
  )
}

/// What to say when the package has no `GetVersion` at all, which only a package older than the check lacks.
fn missing_version_message(problem: &dyn std::fmt::Display) -> String {
  format!(
    "The Tauri.Plugin.DotNet package does not report its version (NativeHost.GetVersion could not be bound: {problem}), \
     so it is older than tauri-plugin-dotnet {}.",
    env!("CARGO_PKG_VERSION")
  )
}

/// The hint shown when no version could be read from the package at all.
fn package_hint() -> String {
  match BridgeVersion::parse(env!("CARGO_PKG_VERSION")) {
    Some(version) => update_package_hint(&version),
    None => "Update the Tauri.Plugin.DotNet NuGet package to the version of the tauri-plugin-dotnet crate.".to_string(),
  }
}

/// What hostfxr reports about a failure while `load` runs, for example which framework version is
/// missing and which are installed. hostfxr writes this to stderr by default, which a packaged GUI
/// app does not have, so it is collected here and appended to the error the frontend receives.
///
/// hostfxr's error writer is per thread, so this must be used on the thread that calls hostfxr.
struct ErrorCapture<'a> {
  hostfxr: &'a Hostfxr,
  lines: Rc<RefCell<Vec<String>>>,
}

impl<'a> ErrorCapture<'a> {
  fn install(hostfxr: &'a Hostfxr) -> Self {
    let lines = Rc::new(RefCell::new(Vec::new()));
    let sink = Rc::clone(&lines);
    hostfxr.set_error_writer(Some(Box::new(move |line| sink.borrow_mut().push(line.to_string_lossy()))));
    Self { hostfxr, lines }
  }

  fn details(&self) -> String {
    truncate_chars(self.lines.borrow().join("\n").trim(), MAX_DETAILS_CHARS)
  }
}

impl Drop for ErrorCapture<'_> {
  fn drop(&mut self) {
    self.hostfxr.set_error_writer(None);
  }
}

/// Keeps a pathological hostfxr report from producing an unreadable error.
const MAX_DETAILS_CHARS: usize = 4000;

fn truncate_chars(text: &str, max: usize) -> String {
  match text.char_indices().nth(max) {
    Some((end, _)) => format!("{}...", &text[..end]),
    None => text.to_string(),
  }
}

/// `summary`, followed by hostfxr's own report when there is one, otherwise by `hint`.
fn with_details(summary: String, details: &str, hint: &str) -> String {
  if !details.is_empty() {
    format!("{summary}\n{details}")
  } else if hint.is_empty() {
    summary
  } else {
    format!("{summary} {hint}")
  }
}

/// Loads the runtime, then the backend, and returns its entry points.
fn load(options: &HostfxrOptions, events: EventSink) -> Result<Managed, BridgeError> {
  match &options.source {
    Source::Path(path) => load_from_path(path, options.dotnet_root.as_deref(), events),
    Source::Embedded(bundle) => load_embedded(bundle, options.dotnet_root.as_deref(), events),
  }
}

fn load_from_path(
  assembly_path: &Path,
  dotnet_root: Option<&Path>,
  events: EventSink,
) -> Result<Managed, BridgeError> {
  let assembly = std::path::absolute(assembly_path)
    .map_err(|e| init_error(format!("Invalid backend path {}: {e}", assembly_path.display())))?;
  if !assembly.is_file() {
    return Err(init_error(format!(
      "The .NET backend assembly was not found at {}. Build the backend first. If it is built somewhere \
       else (another assembly name, a RuntimeIdentifier, artifacts output or a custom OutputPath), give \
       that path: `backend!` takes `assembly = \"...\"`, and `init_with` takes any path.",
      assembly.display()
    )));
  }

  let runtime_config = assembly.with_extension("runtimeconfig.json");
  if !runtime_config.is_file() {
    return Err(init_error(format!(
      "{} is missing. Build the backend with <EnableDynamicLoading>true</EnableDynamicLoading>.",
      runtime_config.display()
    )));
  }
  let framework = fs::read(&runtime_config).ok().and_then(|json| RequiredFramework::from_runtime_config(&json));

  let (hostfxr_path, dotnet_root) = locate_hostfxr(dotnet_root, framework.as_ref())?;
  log::debug!("tauri-plugin-dotnet: using hostfxr at {}", hostfxr_path.display());

  let hostfxr = Hostfxr::load_from_path(&hostfxr_path)
    .map_err(|e| init_error(format!("Failed to load {}: {e}", hostfxr_path.display())))?;
  let capture = ErrorCapture::install(&hostfxr);
  let fail = |message: String, hint: &str| init_error(with_details(message, &capture.details(), hint));

  let context = hostfxr
    .initialize_for_runtime_config_with_dotnet_root(to_native(&runtime_config)?, to_native(&dotnet_root)?)
    .map_err(|e| {
      let error = fail(
        format!(
          "Failed to initialize the .NET runtime from {}: {}.",
          runtime_config.display(),
          e.to_string().trim_end_matches('.')
        ),
        "Is a matching .NET runtime installed?",
      );
      missing_if_framework_missing(e, error, framework.as_ref())
    })?;
  let loader = context
    .get_delegate_loader()
    .map_err(|e| fail(format!("Failed to get the .NET delegate loader: {e}"), ""))?;

  // Loading through the backend's assembly path puts it, and Tauri.Plugin.DotNet next to it, in
  // one isolated load context that resolves dependencies from the backend's deps.json.
  let assembly_native = to_native(&assembly)?;
  let initialize = *loader
    .load_assembly_and_get_function_with_unmanaged_callers_only::<InitializeFn>(
      &assembly_native,
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("Initialize"),
    )
    .map_err(|e| {
      fail(
        format!(
          "Failed to load Tauri.Plugin.DotNet.Hosting.NativeHost from {}: {e}.",
          assembly.display()
        ),
        "Does the backend reference the Tauri.Plugin.DotNet package?",
      )
    })?;

  // Before anything else is bound or started: is this package the one this crate was made for?
  let version = *loader
    .load_assembly_and_get_function_with_unmanaged_callers_only::<VersionFn>(
      &assembly_native,
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("GetVersion"),
    )
    .map_err(|e| fail(missing_version_message(&e), &package_hint()))?;
  if let Err(error) = check_package_version(version()) {
    // The runtime is running by now: leaving its host library loaded is safer than unloading it underneath
    drop(capture);
    std::mem::forget(loader);
    std::mem::forget(context);
    std::mem::forget(hostfxr);
    return Err(error);
  }

  let call = *loader
    .load_assembly_and_get_function_with_unmanaged_callers_only::<CallFn>(
      &assembly_native,
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("Call"),
    )
    .map_err(|e| fail(format!("Failed to bind NativeHost.Call: {e}"), ""))?;
  let cancel = *loader
    .load_assembly_and_get_function_with_unmanaged_callers_only::<CancelFn>(
      &assembly_native,
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("Cancel"),
    )
    .map_err(|e| fail(format!("Failed to bind NativeHost.Cancel: {e}"), ""))?;
  let shutdown = *loader
    .load_assembly_and_get_function_with_unmanaged_callers_only::<ShutdownFn>(
      &assembly_native,
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("Shutdown"),
    )
    .map_err(|e| fail(format!("Failed to bind NativeHost.Shutdown: {e}"), ""))?;

  // Only hostfxr's own calls were being captured, not the backend's startup below.
  drop(capture);

  // The runtime cannot be unloaded, so the hosting library and context stay for the process's life.
  std::mem::forget(loader);
  std::mem::forget(context);
  std::mem::forget(hostfxr);

  let assembly_text = assembly
    .to_str()
    .ok_or_else(|| init_error(format!("The backend path {} is not valid Unicode.", assembly.display())))?;

  start_backend(initialize, assembly_text, events)?;
  Ok(Managed { call, cancel, shutdown })
}

/// The embedded route: the runtime starts from a copy of the embedded runtime config, every
/// assembly of the bundle is loaded from memory into the default load context, and the entry
/// points are then looked up by type name (no file to load them from).
fn load_embedded(bundle: &'static [u8], dotnet_root: Option<&Path>, events: EventSink) -> Result<Managed, BridgeError> {
  let bundle = bundle::parse(bundle)
    .map_err(|problem| init_error(format!("The embedded .NET backend bundle is invalid: {problem}.")))?;
  let framework = RequiredFramework::from_runtime_config(bundle.runtime_config);

  let (hostfxr_path, dotnet_root) = locate_hostfxr(dotnet_root, framework.as_ref())?;
  log::debug!("tauri-plugin-dotnet: using hostfxr at {}", hostfxr_path.display());

  let hostfxr = Hostfxr::load_from_path(&hostfxr_path)
    .map_err(|e| init_error(format!("Failed to load {}: {e}", hostfxr_path.display())))?;
  let capture = ErrorCapture::install(&hostfxr);
  let fail = |message: String, hint: &str| init_error(with_details(message, &capture.details(), hint));

  // hostfxr can only start from a runtimeconfig.json on disk. It is read while the runtime
  // starts, which the first delegate request below completes, so it is removed right after that.
  let runtime_config = TempRuntimeConfig::write(bundle.runtime_config)?;

  let context = hostfxr
    .initialize_for_runtime_config_with_dotnet_root(to_native(runtime_config.path())?, to_native(&dotnet_root)?)
    .map_err(|e| {
      let error = fail(
        format!(
          "Failed to initialize the .NET runtime from the embedded runtime config: {}.",
          e.to_string().trim_end_matches('.')
        ),
        "Is a matching .NET runtime installed?",
      );
      missing_if_framework_missing(e, error, framework.as_ref())
    })?;

  for assembly in &bundle.assemblies {
    context
      .load_assembly_from_bytes(assembly.bytes, assembly.symbols.unwrap_or_default())
      .map_err(|e| {
        fail(
          format!("Failed to load the embedded assembly {}.dll: {e}.", assembly.name),
          "Embedded backends need .NET 8 or newer.",
        )
      })?;
  }

  let loader = context
    .get_delegate_loader()
    .map_err(|e| fail(format!("Failed to get the .NET delegate loader: {e}"), ""))?;
  drop(runtime_config);

  let bind_error =
    |name: &str, e: &dyn std::fmt::Display| fail(format!("Failed to bind NativeHost.{name} from the embedded assemblies: {e}."), "");
  let initialize = *loader
    .get_function_with_unmanaged_callers_only::<InitializeFn>(
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("InitializeEmbedded"),
    )
    .map_err(|e| bind_error("InitializeEmbedded", &e))?;

  // Before anything else is bound or started: is this package the one this crate was made for?
  let version = *loader
    .get_function_with_unmanaged_callers_only::<VersionFn>(
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("GetVersion"),
    )
    .map_err(|e| fail(missing_version_message(&e), &package_hint()))?;
  if let Err(error) = check_package_version(version()) {
    drop(capture);
    std::mem::forget(loader);
    std::mem::forget(context);
    std::mem::forget(hostfxr);
    return Err(error);
  }

  let call = *loader
    .get_function_with_unmanaged_callers_only::<CallFn>(
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("Call"),
    )
    .map_err(|e| bind_error("Call", &e))?;
  let cancel = *loader
    .get_function_with_unmanaged_callers_only::<CancelFn>(
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("Cancel"),
    )
    .map_err(|e| bind_error("Cancel", &e))?;
  let shutdown = *loader
    .get_function_with_unmanaged_callers_only::<ShutdownFn>(
      pdcstr!("Tauri.Plugin.DotNet.Hosting.NativeHost, Tauri.Plugin.DotNet"),
      pdcstr!("Shutdown"),
    )
    .map_err(|e| bind_error("Shutdown", &e))?;

  drop(capture);

  std::mem::forget(loader);
  std::mem::forget(context);
  std::mem::forget(hostfxr);

  start_backend(initialize, bundle.backend, events)?;
  Ok(Managed { call, cancel, shutdown })
}

fn to_native(path: &Path) -> Result<PdCString, BridgeError> {
  PdCString::from_os_str(path.as_os_str()).map_err(|e| init_error(format!("Invalid path {}: {e}", path.display())))
}

/// Runs the managed `Initialize`: `backend` is the assembly path (path route) or the assembly
/// name (embedded route). .NET keeps `events` for the rest of the process.
fn start_backend(initialize: InitializeFn, backend: &str, events: EventSink) -> Result<(), BridgeError> {
  // Leaked on purpose: .NET may emit events for as long as the process runs.
  let emit_context = Box::into_raw(Box::new(events)) as usize;
  let mut error = [0u8; 2048];
  let error_len = initialize(
    backend.as_ptr(),
    backend.len() as i32,
    forward_event,
    emit_context,
    error.as_mut_ptr(),
    error.len() as i32,
  );
  if error_len != 0 {
    let message = String::from_utf8_lossy(&error[..(error_len as usize).min(error.len())]).into_owned();
    return Err(init_error(format!("The .NET backend failed to start: {message}")));
  }

  Ok(())
}

/// A copy of the embedded `runtimeconfig.json` in a folder that only this process created,
/// removed again when dropped.
struct TempRuntimeConfig {
  folder: PathBuf,
  file: PathBuf,
}

impl TempRuntimeConfig {
  fn write(contents: &[u8]) -> Result<Self, BridgeError> {
    let stamp = std::time::SystemTime::now()
      .duration_since(std::time::UNIX_EPOCH)
      .map_or(0, |d| d.as_nanos());

    for attempt in 0..16 {
      let folder = env::temp_dir().join(format!("tauri-plugin-dotnet-{}-{stamp}-{attempt}", std::process::id()));
      // `create_dir` fails when the folder exists, so it is always new and ours alone.
      match fs::create_dir(&folder) {
        Ok(()) => {
          let file = folder.join("app.runtimeconfig.json");
          let result = Self { folder, file };
          fs::write(&result.file, contents).map_err(|e| {
            init_error(format!("Failed to write the runtime config to {}: {e}", result.file.display()))
          })?;
          return Ok(result);
        }
        Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
        Err(e) => {
          return Err(init_error(format!("Failed to create a temporary folder in {}: {e}", env::temp_dir().display())))
        }
      }
    }

    Err(init_error("Failed to find an unused temporary folder name."))
  }

  fn path(&self) -> &Path {
    &self.file
  }
}

impl Drop for TempRuntimeConfig {
  fn drop(&mut self) {
    let _ = fs::remove_dir_all(&self.folder);
  }
}

/// hostfxr's "framework missing" (the runtime the backend asks for is not installed, though another may be) as
/// `RuntimeMissing`, keeping `error`'s message as the details; any other failure stays `error`.
fn missing_if_framework_missing(
  cause: HostingError,
  error: BridgeError,
  framework: Option<&RequiredFramework>,
) -> BridgeError {
  if cause == HostingError::FrameworkMissingFailure {
    runtime_missing(framework, &error.message)
  } else {
    error
  }
}

/// Finds `hostfxr` and the .NET root it belongs to. `framework` is what the backend needs, named in the error when
/// no runtime is found.
fn locate_hostfxr(
  explicit_root: Option<&Path>,
  framework: Option<&RequiredFramework>,
) -> Result<(PathBuf, PathBuf), BridgeError> {
  let roots = match explicit_root {
    // An explicit choice must not silently fall back to some other installation.
    Some(root) => vec![root.to_path_buf()],
    None => candidate_roots(),
  };

  find_hostfxr(&roots).ok_or_else(|| {
    let searched: Vec<String> = roots.iter().map(|r| r.display().to_string()).collect();
    runtime_missing(
      framework,
      &format!(
        "No .NET runtime (hostfxr) was found. Install the .NET runtime, set DOTNET_ROOT, or ship one \
         in a `dotnet` folder next to the executable. Searched: {}",
        if searched.is_empty() { "(nothing)".to_string() } else { searched.join("; ") }
      ),
    )
  })
}

/// The .NET installation roots to try, best first.
fn candidate_roots() -> Vec<PathBuf> {
  let mut roots: Vec<PathBuf> = Vec::new();

  let arch_var = match env::consts::ARCH {
    "x86_64" => Some("DOTNET_ROOT_X64"),
    "aarch64" => Some("DOTNET_ROOT_ARM64"),
    "x86" => Some("DOTNET_ROOT_X86"),
    _ => None,
  };
  for var in arch_var.into_iter().chain(["DOTNET_ROOT"]) {
    if let Some(value) = env::var_os(var).filter(|v| !v.is_empty()) {
      roots.push(PathBuf::from(value));
    }
  }

  if let Some(dir) = env::current_exe().ok().and_then(|exe| exe.parent().map(|p| p.join("dotnet"))) {
    roots.push(dir);
  }

  let dotnet_exe = if cfg!(windows) { "dotnet.exe" } else { "dotnet" };
  if let Some(path) = env::var_os("PATH") {
    for dir in env::split_paths(&path) {
      let exe = dir.join(dotnet_exe);
      if exe.is_file() {
        // `/usr/bin/dotnet` is usually a symlink into the real installation. (Not resolved on
        // Windows, where canonical paths carry a `\\?\` prefix that hostfxr does not accept.)
        #[cfg(not(windows))]
        let exe = fs::canonicalize(&exe).unwrap_or(exe);
        if let Some(root) = exe.parent() {
          roots.push(root.to_path_buf());
        }
      }
    }
  }

  if cfg!(windows) {
    for var in ["ProgramFiles", "ProgramW6432"] {
      if let Some(dir) = env::var_os(var) {
        roots.push(PathBuf::from(dir).join("dotnet"));
      }
    }
  } else if cfg!(target_os = "macos") {
    roots.push(PathBuf::from("/usr/local/share/dotnet"));
    roots.push(PathBuf::from("/opt/homebrew/opt/dotnet/libexec"));
  } else {
    roots.push(PathBuf::from("/usr/share/dotnet"));
    roots.push(PathBuf::from("/usr/lib/dotnet"));
    roots.push(PathBuf::from("/usr/lib64/dotnet"));
  }
  if !cfg!(windows) {
    if let Some(home) = env::var_os("HOME") {
      roots.push(PathBuf::from(home).join(".dotnet"));
    }
  }

  let mut seen = Vec::new();
  roots.retain(|r| {
    let new = !seen.contains(r);
    if new {
      seen.push(r.clone());
    }
    new
  });
  roots
}

/// The first root that contains a `hostfxr`, as `(hostfxr path, root)`.
fn find_hostfxr(roots: &[PathBuf]) -> Option<(PathBuf, PathBuf)> {
  roots
    .iter()
    .find_map(|root| latest_hostfxr(root).map(|hostfxr| (hostfxr, root.clone())))
}

/// The `hostfxr` with the highest version under `<root>/host/fxr/<version>/`. A newer `hostfxr`
/// can host older runtimes, so the newest is always the right choice.
fn latest_hostfxr(root: &Path) -> Option<PathBuf> {
  fs::read_dir(root.join("host").join("fxr"))
    .ok()?
    .filter_map(Result::ok)
    .filter_map(|entry| {
      let path = entry.path().join(HOSTFXR_LIBRARY);
      let version = parse_version(entry.file_name().to_str()?)?;
      path.is_file().then_some((version, path))
    })
    .max_by(|(a, _), (b, _)| a.cmp(b))
    .map(|(_, path)| path)
}

/// `major.minor.patch[-prerelease]`, ordered so that a release is newer than its own prereleases.
#[derive(Debug, PartialEq, Eq)]
struct Version {
  numbers: [u64; 3],
  prerelease: Option<String>,
}

fn parse_version(name: &str) -> Option<Version> {
  let (core, prerelease) = match name.split_once('-') {
    Some((core, pre)) => (core, Some(pre.to_string())),
    None => (name, None),
  };
  let mut parts = core.split('.').map(|p| p.parse::<u64>().ok());
  let numbers = [parts.next()??, parts.next()??, parts.next()??];
  parts.next().is_none().then_some(Version { numbers, prerelease })
}

impl Ord for Version {
  fn cmp(&self, other: &Self) -> Ordering {
    self.numbers.cmp(&other.numbers).then_with(|| match (&self.prerelease, &other.prerelease) {
      (None, None) => Ordering::Equal,
      (None, Some(_)) => Ordering::Greater,
      (Some(_), None) => Ordering::Less,
      (Some(a), Some(b)) => a.cmp(b),
    })
  }
}

impl PartialOrd for Version {
  fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
    Some(self.cmp(other))
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  /// A scratch directory that is removed on drop.
  struct TempDir(PathBuf);

  impl TempDir {
    fn new(name: &str) -> Self {
      let dir = env::temp_dir().join(format!("tauri-plugin-dotnet-test-{}-{name}", std::process::id()));
      let _ = fs::remove_dir_all(&dir);
      fs::create_dir_all(&dir).unwrap();
      Self(dir)
    }

    /// Adds `host/fxr/<version>/<hostfxr library>` under this directory.
    fn add_hostfxr(&self, version: &str) -> PathBuf {
      let dir = self.0.join("host").join("fxr").join(version);
      fs::create_dir_all(&dir).unwrap();
      let lib = dir.join(HOSTFXR_LIBRARY);
      fs::write(&lib, b"").unwrap();
      lib
    }
  }

  impl Drop for TempDir {
    fn drop(&mut self) {
      let _ = fs::remove_dir_all(&self.0);
    }
  }

  fn version(s: &str) -> Version {
    parse_version(s).unwrap_or_else(|| panic!("{s} should parse"))
  }

  #[test]
  fn details_follow_the_summary_and_replace_the_hint() {
    let summary = || "Failed.".to_string();
    assert_eq!(
      with_details(summary(), "Framework 'X' 99 is missing", "Install it."),
      "Failed.\nFramework 'X' 99 is missing"
    );
    assert_eq!(with_details(summary(), "", "Install it."), "Failed. Install it.");
    assert_eq!(with_details(summary(), "", ""), "Failed.");
  }

  #[test]
  fn truncation_counts_characters_not_bytes() {
    assert_eq!(truncate_chars("abc", 3), "abc");
    assert_eq!(truncate_chars("abcd", 3), "abc...");
    assert_eq!(truncate_chars("ααββ", 2), "αα...");
  }

  #[test]
  fn parses_release_and_prerelease_versions() {
    assert_eq!(version("8.0.26").numbers, [8, 0, 26]);
    assert_eq!(version("8.0.26").prerelease, None);
    assert_eq!(version("10.0.0-rc.1.25451.107").prerelease.as_deref(), Some("rc.1.25451.107"));
  }

  #[test]
  fn rejects_names_that_are_not_versions() {
    for name in ["", "latest", "8.0", "8.0.x", "8.0.1.2", "v8.0.1"] {
      assert!(parse_version(name).is_none(), "{name:?} must not parse");
    }
  }

  #[test]
  fn orders_versions_numerically_with_prereleases_below_their_release() {
    assert!(version("10.0.12") > version("9.0.20"));
    assert!(version("8.0.100") > version("8.0.9"), "numeric, not textual");
    assert!(version("10.0.0") > version("10.0.0-rc.1.25451.107"));
    assert!(version("10.0.0-rc.2") > version("10.0.0-rc.1"));
    assert!(version("10.0.0-rc.1.25451.107") > version("9.0.20"), "a 10.x prerelease still beats 9.x");
  }

  #[test]
  fn picks_the_highest_hostfxr_in_a_root() {
    let root = TempDir::new("highest");
    root.add_hostfxr("8.0.26");
    let newest = root.add_hostfxr("10.0.12");
    root.add_hostfxr("10.0.0-rc.1.25451.107");
    root.add_hostfxr("9.0.20");

    assert_eq!(latest_hostfxr(&root.0), Some(newest));
  }

  #[test]
  fn ignores_folders_that_are_not_versions_or_have_no_library() {
    let root = TempDir::new("ignores");
    let valid = root.add_hostfxr("8.0.26");
    root.add_hostfxr("not-a-version");
    fs::create_dir_all(root.0.join("host").join("fxr").join("99.0.0")).unwrap(); // no library inside

    assert_eq!(latest_hostfxr(&root.0), Some(valid));
  }

  #[test]
  fn a_root_without_hostfxr_yields_nothing() {
    let root = TempDir::new("empty");
    assert_eq!(latest_hostfxr(&root.0), None);
    assert_eq!(latest_hostfxr(&root.0.join("missing")), None);
  }

  #[test]
  fn searches_roots_in_order_and_reports_which_matched() {
    let empty = TempDir::new("order-empty");
    let first = TempDir::new("order-first");
    let second = TempDir::new("order-second");
    let expected = first.add_hostfxr("8.0.26");
    second.add_hostfxr("10.0.12");

    let roots = [empty.0.clone(), first.0.clone(), second.0.clone()];
    assert_eq!(find_hostfxr(&roots), Some((expected, first.0.clone())));
  }

  #[test]
  fn an_explicit_root_never_falls_back_to_discovery() {
    let missing = env::temp_dir().join("tauri-plugin-dotnet-test-no-such-root");

    let error = locate_hostfxr(Some(&missing), None).unwrap_err();

    assert_eq!(error.kind, RUNTIME_MISSING);
    assert!(error.message.contains("no-such-root"), "{}", error.message);
  }

  #[test]
  fn no_runtime_found_names_the_framework_to_install() {
    let missing = env::temp_dir().join("tauri-plugin-dotnet-test-no-such-root");
    let framework = RequiredFramework {
      name: BASE_FRAMEWORK.into(),
      version: "8.0.0".into(),
    };

    let error = locate_hostfxr(Some(&missing), Some(&framework)).unwrap_err();

    assert_eq!(error.kind, RUNTIME_MISSING);
    let first_line = error.message.lines().next().unwrap();
    assert_eq!(
      first_line,
      format!(
        "This app needs the .NET Runtime 8.0 ({}), which is not installed on this computer.",
        process_architecture()
      )
    );
    assert!(error.message.contains("No .NET runtime (hostfxr) was found"), "{}", error.message);
  }

  #[test]
  fn the_framework_is_read_from_a_runtime_config() {
    let config = br#"{"runtimeOptions":{"tfm":"net8.0","rollForward":"LatestMinor",
      "framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}"#;

    let framework = RequiredFramework::from_runtime_config(config).unwrap();

    assert_eq!(framework.name, "Microsoft.NETCore.App");
    assert_eq!(framework.version, "8.0.0");
    assert_eq!(framework.description(), format!(".NET Runtime 8.0 ({})", process_architecture()));
  }

  #[test]
  fn of_several_frameworks_the_one_that_includes_the_base_runtime_is_named() {
    let config = br#"{"runtimeOptions":{"frameworks":[
      {"name":"Microsoft.NETCore.App","version":"9.0.0"},
      {"name":"Microsoft.AspNetCore.App","version":"9.0.0"}]}}"#;

    let framework = RequiredFramework::from_runtime_config(config).unwrap();

    assert_eq!(framework.description(), format!("ASP.NET Core Runtime 9.0 ({})", process_architecture()));
  }

  #[test]
  fn a_runtime_config_without_a_framework_names_none() {
    assert_eq!(RequiredFramework::from_runtime_config(b"{}"), None);
    assert_eq!(RequiredFramework::from_runtime_config(b"not json"), None);
    assert_eq!(RequiredFramework::from_runtime_config(br#"{"runtimeOptions":{"framework":{"name":"X"}}}"#), None);
  }

  #[test]
  fn an_unknown_framework_or_version_still_gives_a_usable_message() {
    let odd = RequiredFramework {
      name: "Contoso.App".into(),
      version: "preview".into(),
    };

    assert_eq!(odd.description(), format!("Contoso.App preview ({})", process_architecture()));
  }

  #[test]
  fn only_hostfxrs_framework_missing_becomes_runtime_missing() {
    let framework = RequiredFramework {
      name: BASE_FRAMEWORK.into(),
      version: "8.0.0".into(),
    };
    let failure = || init_error("Failed to initialize the .NET runtime from x.runtimeconfig.json.\nhostfxr's report");

    let missing = missing_if_framework_missing(HostingError::FrameworkMissingFailure, failure(), Some(&framework));
    assert_eq!(missing.kind, RUNTIME_MISSING);
    assert!(missing.message.ends_with("\nFailed to initialize the .NET runtime from x.runtimeconfig.json.\nhostfxr's report"));

    let other = missing_if_framework_missing(HostingError::InvalidArgFailure, failure(), Some(&framework));
    assert_eq!(other, failure());
  }

  #[test]
  fn an_explicit_root_with_hostfxr_is_used() {
    let root = TempDir::new("explicit");
    let expected = root.add_hostfxr("8.0.26");

    assert_eq!(locate_hostfxr(Some(&root.0), None).unwrap(), (expected, root.0.clone()));
  }

  #[test]
  fn candidate_roots_are_unique_and_include_the_app_local_folder() {
    let roots = candidate_roots();

    let app_local = env::current_exe().unwrap().parent().unwrap().join("dotnet");
    assert!(roots.contains(&app_local));
    for (i, root) in roots.iter().enumerate() {
      assert!(!roots[..i].contains(root), "{} is listed twice", root.display());
    }
  }

  #[test]
  fn starting_with_a_missing_backend_fails_calls_instead_of_hanging() {
    use std::sync::{Arc, Mutex};

    let host = HostfxrHost::new(HostfxrOptions::new("definitely/not/here/Backend.dll"));
    let sink: EventSink = Arc::new(|_, _| {});

    let error = host.start(sink).unwrap_err();
    assert_eq!(error.kind, INIT_FAILED);
    assert!(error.message.contains("was not found"), "{}", error.message);

    let response = Arc::new(Mutex::new(None));
    let slot = response.clone();
    host.call("main", "{}".into(), Box::new(move |r| *slot.lock().unwrap() = Some(r)));
    let raw = response.lock().unwrap().take().expect("the call must complete");
    let parsed: serde_json::Value = serde_json::from_str(&raw).unwrap();
    assert_eq!(parsed["error"]["type"], INIT_FAILED);
  }

  #[test]
  fn starting_with_an_invalid_embedded_bundle_fails_calls_with_the_reason() {
    use std::sync::{Arc, Mutex};

    let host = HostfxrHost::new(HostfxrOptions::embedded(b"not a bundle"));
    let sink: EventSink = Arc::new(|_, _| {});

    let error = host.start(sink).unwrap_err();
    assert_eq!(error.kind, INIT_FAILED);
    assert!(error.message.contains("embedded .NET backend bundle is invalid"), "{}", error.message);

    let response = Arc::new(Mutex::new(None));
    let slot = response.clone();
    host.call("main", "{}".into(), Box::new(move |r| *slot.lock().unwrap() = Some(r)));
    let raw = response.lock().unwrap().take().expect("the call must complete");
    let parsed: serde_json::Value = serde_json::from_str(&raw).unwrap();
    assert_eq!(parsed["error"]["type"], INIT_FAILED);
  }

  #[test]
  fn an_explicit_dotnet_root_without_hostfxr_fails_an_embedded_start_too() {
    use std::sync::Arc;

    let empty = TempDir::new("embedded-no-hostfxr");
    // A bundle that parses, so the failure has to come from finding the runtime.
    static BUNDLE: &[u8] = bundle::tests::GOLDEN;
    let host = HostfxrHost::new(HostfxrOptions::embedded(BUNDLE).dotnet_root(&empty.0));

    let error = host.start(Arc::new(|_, _| {})).unwrap_err();

    assert_eq!(error.kind, RUNTIME_MISSING);
    assert!(error.message.contains("No .NET runtime (hostfxr) was found"), "{}", error.message);
  }

  #[test]
  fn the_temporary_runtime_config_holds_the_contents_and_removes_its_folder_when_dropped() {
    let config = TempRuntimeConfig::write(b"{\"a\":1}").unwrap();
    let path = config.path().to_path_buf();
    let folder = path.parent().unwrap().to_path_buf();

    assert_eq!(fs::read(&path).unwrap(), b"{\"a\":1}");
    assert_eq!(path.file_name().unwrap(), "app.runtimeconfig.json");
    assert!(folder.starts_with(env::temp_dir()));

    drop(config);

    assert!(!folder.exists());
  }

  #[test]
  fn each_temporary_runtime_config_gets_its_own_folder() {
    let first = TempRuntimeConfig::write(b"{}").unwrap();
    let second = TempRuntimeConfig::write(b"{}").unwrap();

    assert_ne!(first.path().parent(), second.path().parent());
  }

  #[test]
  fn calls_before_start_are_rejected() {
    use std::sync::{Arc, Mutex};

    let host = HostfxrHost::new(HostfxrOptions::new("Backend.dll"));
    let response = Arc::new(Mutex::new(None));
    let slot = response.clone();
    host.call("main", "{}".into(), Box::new(move |r| *slot.lock().unwrap() = Some(r)));

    let raw = response.lock().unwrap().take().expect("the call must complete");
    let parsed: serde_json::Value = serde_json::from_str(&raw).unwrap();
    assert_eq!(parsed["error"]["type"], "HostNotStarted");
  }

  #[test]
  fn a_missing_backend_is_reported_at_its_own_path() {
    use std::sync::Arc;

    let host = HostfxrHost::new(HostfxrOptions::new("definitely/not/here/Backend.dll"));
    let sink: EventSink = Arc::new(|_, _| {});
    let error = host.start(sink).unwrap_err();

    assert_eq!(error.kind, INIT_FAILED);
    assert!(error.message.contains("Backend.dll"), "{}", error.message);
    assert!(error.message.contains("Build the backend first"), "{}", error.message);
    assert!(error.message.contains("init_with"), "{}", error.message);
  }

  // --- version check --------------------------------------------------------

  fn packed(major: i32, minor: i32, patch: i32) -> i32 {
    (major << 20) | (minor << 10) | patch
  }

  #[test]
  fn versions_are_read_with_or_without_a_patch_number_and_suffix() {
    let v = |major, minor, patch| Some(BridgeVersion { major, minor, patch });
    assert_eq!(BridgeVersion::parse("0.1.0"), v(0, 1, 0));
    assert_eq!(BridgeVersion::parse("1.23.456"), v(1, 23, 456));
    assert_eq!(BridgeVersion::parse("0.2"), v(0, 2, 0));
    assert_eq!(BridgeVersion::parse("0.2.7-beta.1"), v(0, 2, 7));
    assert_eq!(BridgeVersion::parse("0.2.7+abc123"), v(0, 2, 7));
    assert_eq!(BridgeVersion::parse("0.2.7-rc.1+abc123"), v(0, 2, 7));
    assert_eq!(BridgeVersion::parse(""), None);
    assert_eq!(BridgeVersion::parse("1"), None);
    assert_eq!(BridgeVersion::parse("a.b.c"), None);
  }

  #[test]
  fn a_packed_version_is_unpacked_into_its_three_numbers() {
    let v = |major, minor, patch| Some(BridgeVersion { major, minor, patch });
    assert_eq!(BridgeVersion::unpack(packed(0, 1, 0)), v(0, 1, 0));
    assert_eq!(BridgeVersion::unpack(packed(2, 30, 4)), v(2, 30, 4));
    assert_eq!(BridgeVersion::unpack(packed(1023, 1023, 1023)), v(1023, 1023, 1023));
    // What NativeHost.GetVersion returns for a version it could not pack
    assert_eq!(BridgeVersion::unpack(-1), None);
  }

  #[test]
  fn the_packing_matches_what_the_nuget_package_does() {
    // NativeHost.PackVersion(0.1.0) is 1 << 10 = 1024; the .NET tests pin the layout from their side
    assert_eq!(packed(0, 1, 0), 1024);
    assert_eq!(BridgeVersion::unpack(1024), Some(BridgeVersion { major: 0, minor: 1, patch: 0 }));
  }

  #[test]
  fn the_crates_own_version_is_readable() {
    assert!(BridgeVersion::parse(env!("CARGO_PKG_VERSION")).is_some());
  }

  #[test]
  fn the_same_version_is_accepted() {
    assert!(check_versions(packed(0, 1, 0), "0.1.0").is_ok());
  }

  #[test]
  fn the_patch_number_may_differ_in_either_direction() {
    assert!(check_versions(packed(0, 1, 7), "0.1.0").is_ok());
    assert!(check_versions(packed(0, 1, 0), "0.1.7").is_ok());
  }

  #[test]
  fn a_pre_release_or_build_suffix_on_the_crate_is_ignored() {
    assert!(check_versions(packed(0, 3, 0), "0.3.0-beta.2").is_ok());
    assert!(check_versions(packed(0, 3, 5), "0.3.0+sha.abc").is_ok());
    assert!(check_versions(packed(0, 4, 0), "0.3.0-beta.2").is_err());
  }

  #[test]
  fn an_older_package_is_told_to_update_the_package() {
    let error = check_versions(packed(0, 1, 4), "0.2.0").unwrap_err();

    assert_eq!(error.kind, INIT_FAILED);
    assert!(error.message.contains("package is version 0.1.4"), "{}", error.message);
    assert!(error.message.contains("tauri-plugin-dotnet 0.2.0"), "{}", error.message);
    assert!(error.message.contains("(0.2.x)"), "{}", error.message);
    assert!(
      error.message.contains("The package is older: Update the Tauri.Plugin.DotNet NuGet package to 0.2.x"),
      "{}",
      error.message
    );
  }

  #[test]
  fn a_newer_package_is_told_to_update_the_crate() {
    let error = check_versions(packed(0, 3, 0), "0.2.5").unwrap_err();

    assert_eq!(error.kind, INIT_FAILED);
    assert!(error.message.contains("package is version 0.3.0"), "{}", error.message);
    assert!(error.message.contains("The crate is older: update the tauri-plugin-dotnet crate to 0.3.x"), "{}", error.message);
  }

  #[test]
  fn a_different_major_version_is_refused_even_with_the_same_minor() {
    assert!(check_versions(packed(1, 2, 0), "0.2.0").unwrap_err().message.contains("crate is older"));
    assert!(check_versions(packed(0, 2, 0), "1.2.0").unwrap_err().message.contains("package is older"));
  }

  #[test]
  fn a_version_the_package_could_not_pack_is_refused_with_advice() {
    let error = check_versions(-1, "0.1.0").unwrap_err();

    assert_eq!(error.kind, INIT_FAILED);
    assert!(error.message.contains("a version this crate cannot read"), "{}", error.message);
    assert!(error.message.contains("0.1.x"), "{}", error.message);
  }

  #[test]
  fn an_unreadable_crate_version_is_an_error_not_a_pass() {
    assert!(check_versions(packed(0, 1, 0), "not-a-version").is_err());
  }

  #[test]
  fn a_package_without_a_version_entry_is_reported_as_older_than_the_crate() {
    let message = missing_version_message(&"no such method");

    assert!(message.contains("does not report its version"), "{message}");
    assert!(message.contains("no such method"), "{message}");
    assert!(message.contains(&format!("older than tauri-plugin-dotnet {}", env!("CARGO_PKG_VERSION"))), "{message}");
    assert!(package_hint().contains("NuGet package"), "{}", package_hint());
  }

  // --- stopping -------------------------------------------------------------

  #[test]
  fn the_shutdown_timeout_is_five_seconds_by_default_for_both_routes_and_can_be_changed() {
    assert_eq!(HostfxrOptions::new("Backend.dll").shutdown_timeout, Duration::from_secs(5));
    assert_eq!(HostfxrOptions::embedded(b"bundle").shutdown_timeout, Duration::from_secs(5));
    assert_eq!(
      HostfxrOptions::new("Backend.dll").shutdown_timeout(Duration::from_millis(250)).shutdown_timeout,
      Duration::from_millis(250)
    );
  }

  #[test]
  fn a_failed_start_shows_the_dialog_by_default_for_both_routes_unless_told_to_keep_running() {
    let host = |options| HostfxrHost::new(options).on_start_error();

    assert_eq!(host(HostfxrOptions::new("Backend.dll")), OnStartError::ShowDialogAndExit);
    assert_eq!(host(HostfxrOptions::embedded(b"bundle")), OnStartError::ShowDialogAndExit);
    assert_eq!(
      host(HostfxrOptions::new("Backend.dll").on_start_error(OnStartError::KeepRunning)),
      OnStartError::KeepRunning
    );
  }

  // The managed entry points, replaced by functions that record what they are asked. Thread-local, because tests run in
  // parallel and each test runs on its own thread.
  thread_local! {
    static CALLS_REACHING_DOTNET: std::cell::Cell<u32> = const { std::cell::Cell::new(0) };
    static SHUTDOWN_TIMEOUTS: RefCell<Vec<i32>> = const { RefCell::new(Vec::new()) };
    static SHUTDOWN_RESULT: std::cell::Cell<i32> = const { std::cell::Cell::new(0) };
  }

  extern "system" fn fake_call(_: *const u8, _: i32, _: *const u8, _: i32, _: usize, _: DoneFn) {
    CALLS_REACHING_DOTNET.with(|c| c.set(c.get() + 1));
  }

  extern "system" fn fake_cancel(_: *const u8, _: i32) {}

  extern "system" fn fake_shutdown(timeout_ms: i32) -> i32 {
    SHUTDOWN_TIMEOUTS.with(|t| t.borrow_mut().push(timeout_ms));
    SHUTDOWN_RESULT.with(|r| r.get())
  }

  /// A started host over the fake entry points; `.NET` answers `shutdown_result` when asked to shut down.
  fn started_host(shutdown_timeout: Duration, shutdown_result: i32) -> HostfxrHost {
    CALLS_REACHING_DOTNET.with(|c| c.set(0));
    SHUTDOWN_TIMEOUTS.with(|t| t.borrow_mut().clear());
    SHUTDOWN_RESULT.with(|r| r.set(shutdown_result));

    let host = HostfxrHost::new(HostfxrOptions::new("Backend.dll").shutdown_timeout(shutdown_timeout));
    let managed = Managed {
      call: fake_call,
      cancel: fake_cancel,
      shutdown: fake_shutdown,
    };
    assert!(host.state.set(State::Ready(managed)).is_ok());
    host
  }

  fn shutdown_timeouts() -> Vec<i32> {
    SHUTDOWN_TIMEOUTS.with(|t| t.borrow().clone())
  }

  /// The type of the error a call completes with, or `None` if it did not complete.
  fn call_error_type(host: &HostfxrHost) -> Option<String> {
    use std::sync::{Arc, Mutex};

    let response = Arc::new(Mutex::new(None));
    let slot = response.clone();
    host.call("main", "{}".into(), Box::new(move |r| *slot.lock().unwrap() = Some(r)));
    let raw = response.lock().unwrap().take()?;
    let parsed: serde_json::Value = serde_json::from_str(&raw).unwrap();
    parsed["error"]["type"].as_str().map(str::to_string)
  }

  #[test]
  fn stop_asks_dotnet_to_shut_down_once_with_the_timeout_in_milliseconds() {
    let host = started_host(Duration::from_secs(3), SHUTDOWN_COMPLETED);

    host.stop();
    host.stop(); // Tauri sends the exit event once, but a second stop must not run the shutdown again

    assert_eq!(shutdown_timeouts(), vec![3000]);
  }

  #[test]
  fn stop_with_a_zero_timeout_leaves_dotnet_alone() {
    let host = started_host(Duration::ZERO, SHUTDOWN_COMPLETED);

    host.stop();

    assert!(shutdown_timeouts().is_empty());
  }

  #[test]
  fn stop_caps_an_enormous_timeout_at_what_the_interface_can_carry() {
    let host = started_host(Duration::from_secs(u64::MAX / 2), SHUTDOWN_COMPLETED);

    host.stop();

    assert_eq!(shutdown_timeouts(), vec![i32::MAX]);
  }

  #[test]
  fn stop_carries_on_when_dotnet_timed_out_or_failed() {
    for result in [SHUTDOWN_TIMED_OUT, 2, 99] {
      let host = started_host(Duration::from_secs(1), result);

      host.stop(); // must neither panic nor hang

      assert_eq!(shutdown_timeouts(), vec![1000], "result {result}");
    }
  }

  #[test]
  fn calls_made_after_stop_are_rejected_and_never_reach_dotnet() {
    let host = started_host(Duration::from_secs(1), SHUTDOWN_COMPLETED);
    assert_eq!(call_error_type(&host), None); // before stop the call goes through (the fake never answers)
    assert_eq!(CALLS_REACHING_DOTNET.with(|c| c.get()), 1);

    host.stop();

    assert_eq!(call_error_type(&host).as_deref(), Some(STOPPED));
    assert_eq!(CALLS_REACHING_DOTNET.with(|c| c.get()), 1, "the call after stop must not reach .NET");
  }

  #[test]
  fn calls_are_rejected_after_stop_even_when_the_shutdown_was_skipped() {
    let host = started_host(Duration::ZERO, SHUTDOWN_COMPLETED);

    host.stop();

    assert_eq!(call_error_type(&host).as_deref(), Some(STOPPED));
  }

  #[test]
  fn stop_before_start_is_harmless() {
    let host = HostfxrHost::new(HostfxrOptions::new("Backend.dll"));

    host.stop();

    // Nothing was started, so this is still the not-started error and not a stopped host
    assert_eq!(call_error_type(&host).as_deref(), Some("HostNotStarted"));
  }

  #[test]
  fn stop_after_a_failed_start_keeps_reporting_why_it_failed() {
    use std::sync::Arc;

    let host = HostfxrHost::new(HostfxrOptions::new("definitely/not/here/Backend.dll"));
    let sink: EventSink = Arc::new(|_, _| {});
    assert!(host.start(sink).is_err());

    host.stop();

    assert_eq!(call_error_type(&host).as_deref(), Some(INIT_FAILED));
  }
}
