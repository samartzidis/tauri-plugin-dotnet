//! Tauri plugin that connects the frontend to .NET services through a typed RPC bridge.
//!
//! The frontend calls `invoke("plugin:dotnet|call", ...)` and gets a promise back; the call is
//! handed to a [`DotNetHost`], which runs it on the .NET side (`BridgeDispatcher`). Events
//! travel the other way as the `dotnet:event` Tauri event.

use std::sync::Arc;

use tauri::{
  plugin::{Builder as PluginBuilder, TauriPlugin},
  AppHandle, Emitter, Manager, RunEvent, Runtime,
};

mod bundle;
mod commands;
mod error;
mod host;
mod hostfxr;
mod macros;
mod shadow;
mod sidecar;
mod start_error;

pub use error::BridgeError;
pub use host::{Completion, DotNetHost, EventSink, OnStartError};
pub use hostfxr::{HostfxrHost, HostfxrOptions};
pub use sidecar::{SidecarHost, SidecarOptions};

/// Name of the Tauri event that carries .NET events to the frontend (see the TypeScript runtime).
pub const EVENT_CHANNEL: &str = "dotnet:event";

/// Plugin state: the connection to the .NET side.
pub(crate) struct DotNet {
  host: Arc<dyn DotNetHost>,
}

/// Configures and builds the plugin.
pub struct Builder {
  host: Arc<dyn DotNetHost>,
}

impl Default for Builder {
  fn default() -> Self {
    Self::new()
  }
}

impl Builder {
  pub fn new() -> Self {
    Self {
      host: Arc::new(host::NoHost),
    }
  }

  /// Sets the connection to the .NET side. Without one, every call fails with `HostNotConfigured`.
  pub fn host(mut self, host: impl DotNetHost) -> Self {
    self.host = Arc::new(host);
    self
  }

  pub fn build<R: Runtime>(self) -> TauriPlugin<R> {
    let host = self.host;
    plugin(move |_app| host)
  }
}

/// Initializes the plugin with a host made from the app handle when the plugin starts, so the host can
/// depend on the app (for example, find the backend under `resource_dir()`) without a `setup` hook in
/// the app. [`backend!`] wraps this for the usual layout.
///
/// ```ignore
/// tauri::Builder::default()
///   .plugin(tauri_plugin_dotnet::init_with(|app| {
///     HostfxrHost::new(tauri_plugin_dotnet::any_backend_options!(app, "MyApp.Backend"))
///   }))
/// ```
pub fn init_with<R, H, F>(make_host: F) -> TauriPlugin<R>
where
  R: Runtime,
  H: DotNetHost,
  F: FnOnce(&AppHandle<R>) -> H + Send + 'static,
{
  plugin(move |app| Arc::new(make_host(app)))
}

fn plugin<R, F>(make_host: F) -> TauriPlugin<R>
where
  R: Runtime,
  F: FnOnce(&AppHandle<R>) -> Arc<dyn DotNetHost> + Send + 'static,
{
  PluginBuilder::new("dotnet")
    .invoke_handler(tauri::generate_handler![commands::call, commands::cancel])
    .setup(move |app, _api| {
      let host = make_host(app);
      let handle = app.clone();
      let events: EventSink = Arc::new(move |window_label, event_json| {
        forward_event(&handle, window_label, &event_json)
      });
      // Without .NET nothing in the app works, so by default the user is told and the app ends here. A host set to
      // keep running rejects calls with the reason instead, so the frontend can show it.
      if let Err(error) = host.start(events) {
        log::error!("tauri-plugin-dotnet: the .NET host failed to start: {error}");
        if host.on_start_error() == OnStartError::ShowDialogAndExit {
          start_error::show_and_exit(app, &error);
        }
      }
      app.manage(DotNet { host });
      Ok(())
    })
    .on_event(|app, event| {
      if let Some(state) = app.try_state::<DotNet>() {
        handle_run_event(&*state.host, event);
      }
    })
    .build()
}

/// Tells the host when the app is exiting. `RunEvent::Exit` is the last thing a plugin sees: Tauri cleans up and ends the
/// process right after it, without running destructors, so this is the one place .NET can be told. `ExitRequested` is not
/// used: an app can veto it (to keep running in the tray), and then nothing is exiting.
fn handle_run_event(host: &dyn DotNetHost, event: &RunEvent) {
  if let RunEvent::Exit = event {
    host.stop();
  }
}

/// Initializes the plugin with no .NET host. Use [`Builder`] to supply one.
pub fn init<R: Runtime>() -> TauriPlugin<R> {
  Builder::new().build()
}

/// Emits a .NET event to one webview (`Some(label)`) or to all of them (`None`).
fn forward_event<R: Runtime>(app: &AppHandle<R>, window_label: Option<String>, event_json: &str) {
  let payload: serde_json::Value = match serde_json::from_str(event_json) {
    Ok(payload) => payload,
    Err(e) => {
      log::error!("tauri-plugin-dotnet: dropping malformed event from .NET: {e}");
      return;
    }
  };

  let result = match window_label {
    Some(label) => app.emit_to(label, EVENT_CHANNEL, payload),
    None => app.emit(EVENT_CHANNEL, payload),
  };
  if let Err(e) = result {
    log::error!("tauri-plugin-dotnet: failed to emit event to the frontend: {e}");
  }
}

#[cfg(test)]
mod tests {
  use std::sync::atomic::{AtomicUsize, Ordering};

  use super::*;

  /// Counts what it is told.
  #[derive(Default)]
  struct CountingHost {
    stops: AtomicUsize,
  }

  impl DotNetHost for CountingHost {
    fn start(&self, _events: EventSink) -> Result<(), BridgeError> {
      Ok(())
    }

    fn call(&self, _window_label: &str, _request_json: String, _on_done: Completion) {}

    fn cancel(&self, _call_id: &str) {}

    fn stop(&self) {
      self.stops.fetch_add(1, Ordering::SeqCst);
    }
  }

  #[test]
  fn the_host_is_stopped_when_the_app_exits() {
    let host = CountingHost::default();

    handle_run_event(&host, &RunEvent::Exit);

    assert_eq!(host.stops.load(Ordering::SeqCst), 1);
  }

  #[test]
  fn other_events_do_not_stop_the_host() {
    let host = CountingHost::default();

    handle_run_event(&host, &RunEvent::Ready);
    handle_run_event(&host, &RunEvent::Resumed);
    handle_run_event(&host, &RunEvent::MainEventsCleared);

    assert_eq!(host.stops.load(Ordering::SeqCst), 0);
  }

  #[test]
  fn a_host_that_does_not_implement_stop_ignores_it() {
    // The trait's default is a no-op, so hosts written before `stop` existed keep working
    struct Minimal;
    impl DotNetHost for Minimal {
      fn start(&self, _events: EventSink) -> Result<(), BridgeError> {
        Ok(())
      }
      fn call(&self, _window_label: &str, _request_json: String, _on_done: Completion) {}
      fn cancel(&self, _call_id: &str) {}
    }

    handle_run_event(&Minimal, &RunEvent::Exit);
  }
}
