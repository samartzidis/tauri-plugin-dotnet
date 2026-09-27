//! The file watcher that restarts the sidecar when the backend's output folder is rebuilt
//! (`SidecarOptions::watch`, on by default).

use std::path::Path;
use std::sync::Arc;

use notify::{RecursiveMode, Watcher};
use tokio::sync::mpsc;

use super::{restart, Shared};

/// Whether a changed path should trigger a sidecar restart: any `.dll` or `.json` (covers the
/// backend's own dll, a dependency dll, `.deps.json`, `.runtimeconfig.json`), case-insensitively.
/// Everything else (`.pdb`, temp files, ...) is noise the watcher ignores.
fn is_relevant_change(path: &Path) -> bool {
  matches!(
    path.extension().and_then(|e| e.to_str()),
    Some(ext) if ext.eq_ignore_ascii_case("dll") || ext.eq_ignore_ascii_case("json")
  )
}

/// Starts watching the backend dll's output folder (non-recursively) for any `.dll`/`.json` change
/// and restarting the sidecar, debounced, whenever one occurs. The watcher is stored in
/// `shared.watcher` so it lives for the host's whole lifetime; this function returns once it is set
/// up.
pub(super) fn start(shared: Arc<Shared>) {
  let backend_dll = shared.options.backend_dll.clone();
  let Some(dir) = backend_dll.parent().map(|p| p.to_path_buf()) else {
    log::warn!("tauri-plugin-dotnet: the backend dll has no parent directory; not watching for rebuilds");
    return;
  };

  let (tick_tx, mut tick_rx) = mpsc::unbounded_channel::<()>();
  let watcher = notify::recommended_watcher(move |res: notify::Result<notify::Event>| {
    if let Ok(event) = res {
      // Watch the whole output folder, not just the main dll: a rebuild that only changes a
      // dependency dll or the deps.json/runtimeconfig.json (the main dll's own output can be
      // byte-identical and skipped by MSBuild's SkipUnchangedFiles) must still trigger a
      // restart. `.pdb`-only and other noise (temp files, `obj/` is out of scope since the
      // watch is non-recursive) is filtered out.
      if event.paths.iter().any(|p| is_relevant_change(p)) {
        let _ = tick_tx.send(());
      }
    }
  });

  let mut watcher = match watcher {
    Ok(w) => w,
    Err(e) => {
      log::error!("tauri-plugin-dotnet: failed to create the sidecar rebuild watcher: {e}");
      return;
    }
  };
  if let Err(e) = watcher.watch(&dir, RecursiveMode::NonRecursive) {
    log::error!(
      "tauri-plugin-dotnet: failed to watch {} for rebuilds: {e}",
      dir.display()
    );
    return;
  }
  let _ = shared.watcher.set(watcher);

  let debounce = shared.options.restart_debounce;
  tauri::async_runtime::spawn(async move {
    loop {
      // Wait for the first tick of a burst, then keep resetting the deadline until it settles.
      if tick_rx.recv().await.is_none() {
        return;
      }
      loop {
        match tokio::time::timeout(debounce, tick_rx.recv()).await {
          Ok(Some(())) => continue, // another tick arrived inside the window; keep waiting
          Ok(None) => return,       // the sender was dropped: the host is gone
          Err(_) => break,          // the window elapsed quietly: the burst has settled
        }
      }
      log::info!("tauri-plugin-dotnet: detected a rebuild of the .NET backend, restarting the sidecar");
      restart(&shared).await;
    }
  });
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn reacts_to_dll_and_json_changes() {
    assert!(is_relevant_change(Path::new("MyApp.Backend.dll")));
    assert!(is_relevant_change(Path::new("Some.Dependency.DLL"))); // case-insensitive
    assert!(is_relevant_change(Path::new("MyApp.Backend.deps.json")));
    assert!(is_relevant_change(Path::new("MyApp.Backend.runtimeconfig.json")));
  }

  #[test]
  fn ignores_everything_else() {
    assert!(!is_relevant_change(Path::new("MyApp.Backend.pdb")));
    assert!(!is_relevant_change(Path::new("MyApp.Backend.dll.tmp")));
    assert!(!is_relevant_change(Path::new("no_extension")));
  }
}
