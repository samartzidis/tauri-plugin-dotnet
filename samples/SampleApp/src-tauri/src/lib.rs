#[cfg(any(
  all(feature = "sidecar-backend", feature = "path-backend"),
  all(feature = "sidecar-backend", feature = "embedded-backend"),
  all(feature = "path-backend", feature = "embedded-backend"),
))]
compile_error!("enable at most one of the features sidecar-backend, path-backend and embedded-backend");

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
    // The sample shows every hosting mode, so it picks one explicitly. An app that uses only files can
    // write `.plugin(tauri_plugin_dotnet::backend!("MyApp.Backend"))` (sidecar in debug, path in release).
    // At most one of these features may be on; with none, the profile decides (as `backend!` does):
    // - `sidecar-backend`: the dev-only sidecar process under `dotnet watch` (a rebuild restarts just that
    //   process, not the app). Needs the .NET SDK and the backend's source at run time, so it only works on
    //   the machine that built it, in release too.
    // - `path-backend`: in-process from files even in a debug build (`bin/Debug`): no `dotnet watch`, and a
    //   rebuild is blocked while the app runs.
    // - `embedded-backend`: the backend travels inside this executable (`dotnet build -p:TauriDotNetEmbed=true`).
    // - none: sidecar in a debug build, `resource_dir()/dotnet/` in-process (shipped by `bundle.resources`)
    //   in release.
    .plugin(tauri_plugin_dotnet::init_with(|app| {
      #[cfg(feature = "sidecar-backend")]
      {
        tauri_plugin_dotnet::sidecar_backend_host!(app, "SampleApp.Backend")
      }
      #[cfg(feature = "path-backend")]
      {
        tauri_plugin_dotnet::path_backend_host!(app, "SampleApp.Backend")
      }
      #[cfg(feature = "embedded-backend")]
      {
        tauri_plugin_dotnet::embedded_backend_host!(app, "SampleApp.Backend")
      }
      #[cfg(not(any(feature = "sidecar-backend", feature = "path-backend", feature = "embedded-backend")))]
      {
        #[cfg(debug_assertions)]
        {
          tauri_plugin_dotnet::sidecar_backend_host!(app, "SampleApp.Backend")
        }
        #[cfg(not(debug_assertions))]
        {
          tauri_plugin_dotnet::path_backend_host!(app, "SampleApp.Backend")
        }
      }
    }))
    .run(tauri::generate_context!())
    .expect("error while running tauri application");
}
