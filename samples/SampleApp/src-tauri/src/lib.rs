#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  tauri::Builder::default()
    // Registered first: its setup() installs the global `log` logger that every later plugin's
    // `log::info!`/`debug!`/etc. calls write through, tauri-plugin-dotnet's included - a plugin
    // registered before this one would have its early setup-time log lines silently dropped, since
    // the logger would not exist yet when they run. Defaults to stdout (this terminal) plus a file
    // under the OS log directory.
    .plugin(tauri_plugin_log::Builder::new().level(log::LevelFilter::Debug).build())
    // Native dialogs come from Tauri's own plugin; no .NET wrapper is needed for them.
    .plugin(tauri_plugin_dialog::init())
    // Without `embedded-backend`: a dev-only sidecar process in a debug build (a rebuild restarts
    // just that process, not the app), or `resource_dir()/dotnet/` in-process (shipped by
    // `bundle.resources`) in release. With it, the backend travels inside this executable instead
    // (`dotnet build -p:TauriDotNetEmbed=true`), always in-process.
    // An app that uses only files can write `.plugin(tauri_plugin_dotnet::backend!("MyApp.Backend"))`.
    // `path-backend` (ignored when `embedded-backend` is on) forces an in-process host that loads the backend
    // from files even in a debug build: `bin/Debug` here, so no `dotnet watch`, and a rebuild is blocked while
    // the app runs.
    .plugin(tauri_plugin_dotnet::init_with(|app| {
      #[cfg(all(feature = "path-backend", not(feature = "embedded-backend")))]
      {
        tauri_plugin_dotnet::HostfxrHost::new(tauri_plugin_dotnet::backend_options!(app, "SampleApp.Backend"))
      }
      #[cfg(not(all(feature = "path-backend", not(feature = "embedded-backend"))))]
      {
        tauri_plugin_dotnet::any_backend_host!(app, "SampleApp.Backend")
      }
    }))
    .run(tauri::generate_context!())
    .expect("error while running tauri application");
}
