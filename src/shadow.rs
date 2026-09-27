//! A private copy of the backend's folder, loaded instead of the original.
//!
//! The runtime keeps the files it loaded open (on Windows a loaded assembly cannot be overwritten),
//! so while an app runs from the build output folder, `dotnet build` cannot replace the backend.
//! Loading a copy leaves the build output free: a rebuild always works, and a relaunch loads the
//! newest build. It is meant for development (see `HostfxrOptions::shadow_copy`).
//!
//! Each start makes a new folder in the system temp directory. Copies of earlier runs are removed
//! at the next start, but only when their process is gone: another app in development may still be
//! running from its own copy.

use std::{
  fs, io,
  path::{Path, PathBuf},
  sync::atomic::{AtomicU32, Ordering},
  thread,
  time::{Duration, SystemTime, UNIX_EPOCH},
};

/// Names of the copies: `tauri-plugin-dotnet-shadow-<pid>-<unix ms>-<n>`.
const PREFIX: &str = "tauri-plugin-dotnet-shadow-";

/// A file that cannot be copied right now (a build may be writing it) is retried for this long.
const COPY_ATTEMPTS: u32 = 30;
const RETRY_DELAY: Duration = Duration::from_millis(100);

/// A copy younger than this is never removed: its process may not have loaded it yet.
const MIN_AGE_TO_REMOVE: Duration = Duration::from_secs(60);

/// Where there is no cheap way to tell whether a process is alive, only copies older than this
/// are removed.
#[cfg(not(any(windows, target_os = "linux")))]
const UNKNOWN_LIVENESS_AGE: Duration = Duration::from_secs(24 * 60 * 60);

static COUNTER: AtomicU32 = AtomicU32::new(0);

/// The copy made for this run.
#[derive(Debug)]
pub(crate) struct ShadowCopy {
  /// The backend assembly inside the copy.
  pub assembly: PathBuf,
  /// The folder holding the copy.
  pub folder: PathBuf,
  /// How many files were copied.
  pub files: usize,
}

/// Copies the folder that holds `assembly` to a new folder in the system temp directory.
pub(crate) fn create(assembly: &Path) -> io::Result<ShadowCopy> {
  create_in(&std::env::temp_dir(), assembly)
}

fn create_in(root: &Path, assembly: &Path) -> io::Result<ShadowCopy> {
  let source = assembly
    .parent()
    .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "the backend path has no folder"))?;
  let name = assembly
    .file_name()
    .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "the backend path has no file name"))?;

  fs::create_dir_all(root)?;
  remove_stale_in(root, SystemTime::now());

  let folder = new_folder(root)?;
  let files = match copy_tree(source, &folder) {
    Ok(files) => files,
    Err(e) => {
      let _ = fs::remove_dir_all(&folder);
      return Err(e);
    }
  };
  Ok(ShadowCopy {
    assembly: folder.join(name),
    folder,
    files,
  })
}

/// Creates a folder that did not exist before (`create_dir`, never `create_dir_all`).
fn new_folder(root: &Path) -> io::Result<PathBuf> {
  let stamp = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis()).unwrap_or(0);
  for _ in 0..100 {
    let n = COUNTER.fetch_add(1, Ordering::Relaxed);
    let folder = root.join(format!("{PREFIX}{}-{stamp}-{n}", std::process::id()));
    match fs::create_dir(&folder) {
      Ok(()) => return Ok(folder),
      Err(e) if e.kind() == io::ErrorKind::AlreadyExists => continue,
      Err(e) => return Err(e),
    }
  }
  Err(io::Error::new(io::ErrorKind::AlreadyExists, "could not find an unused folder name"))
}

fn copy_tree(source: &Path, destination: &Path) -> io::Result<usize> {
  fs::create_dir_all(destination)?;
  let mut files = 0;
  for entry in fs::read_dir(source)? {
    let entry = entry?;
    let target = destination.join(entry.file_name());
    if entry.file_type()?.is_dir() {
      files += copy_tree(&entry.path(), &target)?;
    } else {
      copy_file(&entry.path(), &target)?;
      files += 1;
    }
  }
  Ok(files)
}

/// Copies one file, retrying: while a build is writing it, the copy fails for a moment.
fn copy_file(source: &Path, target: &Path) -> io::Result<()> {
  let mut attempt = 0;
  loop {
    match fs::copy(source, target) {
      Ok(_) => return Ok(()),
      Err(e) if attempt < COPY_ATTEMPTS => {
        attempt += 1;
        log::debug!("tauri-plugin-dotnet: copying {} failed ({e}), retry {attempt}", source.display());
        thread::sleep(RETRY_DELAY);
      }
      Err(e) => {
        return Err(io::Error::new(e.kind(), format!("could not copy {}: {e}", source.display())));
      }
    }
  }
}

/// Removes the copies of runs that have ended. Best effort: whatever cannot be removed stays.
fn remove_stale_in(root: &Path, now: SystemTime) {
  let Ok(entries) = fs::read_dir(root) else { return };
  for entry in entries.flatten() {
    let name = entry.file_name();
    let Some((pid, created_ms)) = name.to_str().and_then(parse_name) else { continue };
    let created = UNIX_EPOCH + Duration::from_millis(created_ms.min(u64::MAX as u128) as u64);
    let age = now.duration_since(created).unwrap_or_default();
    if age < MIN_AGE_TO_REMOVE || pid == std::process::id() || in_use(pid, &entry.path(), age) {
      continue;
    }
    if let Err(e) = fs::remove_dir_all(entry.path()) {
      log::debug!("tauri-plugin-dotnet: could not remove {}: {e}", entry.path().display());
    }
  }
}

/// `tauri-plugin-dotnet-shadow-<pid>-<unix ms>-<n>` -> (pid, unix ms).
fn parse_name(name: &str) -> Option<(u32, u128)> {
  let mut parts = name.strip_prefix(PREFIX)?.split('-');
  let pid = parts.next()?.parse().ok()?;
  let created = parts.next()?.parse().ok()?;
  parts.next()?.parse::<u32>().ok()?;
  parts.next().is_none().then_some((pid, created))
}

/// Windows: a loaded assembly cannot be opened for writing, so a folder with such a file belongs to
/// a running process. (Opening for writing changes nothing, it is only a probe.)
#[cfg(windows)]
fn in_use(_pid: u32, folder: &Path, _age: Duration) -> bool {
  let Ok(entries) = fs::read_dir(folder) else { return false };
  entries.flatten().any(|entry| {
    let path = entry.path();
    path.extension().is_some_and(|e| e.eq_ignore_ascii_case("dll"))
      && fs::OpenOptions::new().write(true).open(&path).is_err()
  })
}

#[cfg(target_os = "linux")]
fn in_use(pid: u32, _folder: &Path, _age: Duration) -> bool {
  Path::new("/proc").join(pid.to_string()).exists()
}

#[cfg(not(any(windows, target_os = "linux")))]
fn in_use(_pid: u32, _folder: &Path, age: Duration) -> bool {
  age < UNKNOWN_LIVENESS_AGE
}

#[cfg(test)]
mod tests {
  use super::*;

  /// A throwaway folder that is removed when it goes out of scope.
  struct Scratch(PathBuf);

  impl Scratch {
    fn new(name: &str) -> Self {
      let dir = std::env::temp_dir().join(format!("tauri-plugin-dotnet-test-shadow-{}-{name}", std::process::id()));
      let _ = fs::remove_dir_all(&dir);
      fs::create_dir_all(&dir).unwrap();
      Self(dir)
    }

    fn file(&self, relative: &str, contents: &str) -> PathBuf {
      let path = self.0.join(relative);
      fs::create_dir_all(path.parent().unwrap()).unwrap();
      fs::write(&path, contents).unwrap();
      path
    }
  }

  impl Drop for Scratch {
    fn drop(&mut self) {
      let _ = fs::remove_dir_all(&self.0);
    }
  }

  /// A backend folder shaped like a build output, including a nested `runtimes` tree.
  fn backend(scratch: &Scratch) -> PathBuf {
    scratch.file("build/MyApp.Backend.deps.json", "deps");
    scratch.file("build/MyApp.Backend.runtimeconfig.json", "config");
    scratch.file("build/Tauri.Plugin.DotNet.dll", "plugin");
    scratch.file("build/runtimes/win-x64/native/e_sqlite3.dll", "native");
    scratch.file("build/MyApp.Backend.dll", "backend v1")
  }

  /// Makes a copy-shaped folder as an earlier run would have, `age` ago.
  fn fake_copy(root: &Path, pid: u32, age: Duration, with_dll: bool) -> PathBuf {
    let created = SystemTime::now().duration_since(UNIX_EPOCH).unwrap() - age;
    let folder = root.join(format!("{PREFIX}{pid}-{}-0", created.as_millis()));
    fs::create_dir_all(&folder).unwrap();
    fs::write(folder.join("MyApp.Backend.json"), "x").unwrap();
    if with_dll {
      fs::write(folder.join("MyApp.Backend.dll"), "x").unwrap();
    }
    folder
  }

  const OLD: Duration = Duration::from_secs(600);

  #[test]
  fn copies_the_whole_folder_including_subfolders_and_leaves_the_source_alone() {
    let scratch = Scratch::new("copies");
    let assembly = backend(&scratch);

    let copy = create_in(&scratch.0.join("temp"), &assembly).unwrap();

    assert_eq!(copy.files, 5);
    assert_eq!(copy.assembly, copy.folder.join("MyApp.Backend.dll"));
    assert_eq!(fs::read_to_string(&copy.assembly).unwrap(), "backend v1");
    assert_eq!(
      fs::read_to_string(copy.folder.join("runtimes/win-x64/native/e_sqlite3.dll")).unwrap(),
      "native"
    );
    assert_eq!(fs::read_to_string(&assembly).unwrap(), "backend v1");
  }

  #[test]
  fn a_later_build_does_not_change_an_existing_copy() {
    let scratch = Scratch::new("later-build");
    let assembly = backend(&scratch);
    let copy = create_in(&scratch.0.join("temp"), &assembly).unwrap();

    fs::write(&assembly, "backend v2").unwrap();

    assert_eq!(fs::read_to_string(&copy.assembly).unwrap(), "backend v1");
    let next = create_in(&scratch.0.join("temp"), &assembly).unwrap();
    assert_eq!(fs::read_to_string(&next.assembly).unwrap(), "backend v2");
  }

  #[test]
  fn every_copy_gets_its_own_folder() {
    let scratch = Scratch::new("unique");
    let assembly = backend(&scratch);
    let root = scratch.0.join("temp");

    let a = create_in(&root, &assembly).unwrap();
    let b = create_in(&root, &assembly).unwrap();

    assert_ne!(a.folder, b.folder);
    assert!(a.folder.starts_with(&root) && b.folder.starts_with(&root));
  }

  #[test]
  fn a_missing_backend_folder_is_an_error_and_leaves_no_folder_behind() {
    let scratch = Scratch::new("missing");
    let root = scratch.0.join("temp");

    let error = create_in(&root, &scratch.0.join("not-here/MyApp.Backend.dll")).unwrap_err();

    assert_eq!(error.kind(), io::ErrorKind::NotFound);
    let leftovers = fs::read_dir(&root).map(|d| d.count()).unwrap_or(0);
    assert_eq!(leftovers, 0);
  }

  #[test]
  fn names_round_trip_and_odd_names_are_rejected() {
    assert_eq!(parse_name("tauri-plugin-dotnet-shadow-42-1789913594838-3"), Some((42, 1789913594838)));
    assert_eq!(parse_name("tauri-plugin-dotnet-shadow-42-17-3-extra"), None);
    assert_eq!(parse_name("tauri-plugin-dotnet-shadow-x-17-3"), None);
    assert_eq!(parse_name("tauri-plugin-dotnet-shadow-42-17"), None);
    assert_eq!(parse_name("tauri-plugin-dotnet-1-2-3"), None);
    assert_eq!(parse_name("other-42-17-3"), None);
  }

  #[test]
  fn removes_old_copies_of_ended_processes() {
    let scratch = Scratch::new("stale");
    let ended = fake_copy(&scratch.0, u32::MAX - 1, OLD, false);

    remove_stale_in(&scratch.0, SystemTime::now());

    assert!(!ended.exists());
  }

  #[test]
  fn keeps_young_copies_this_processes_copies_and_things_that_are_not_copies() {
    let scratch = Scratch::new("keep");
    let young = fake_copy(&scratch.0, u32::MAX - 1, Duration::from_secs(5), false);
    let own = fake_copy(&scratch.0, std::process::id(), OLD, false);
    let stranger = scratch.0.join("some-other-folder");
    fs::create_dir(&stranger).unwrap();
    let odd_name = scratch.0.join(format!("{PREFIX}not-a-pid"));
    fs::create_dir(&odd_name).unwrap();

    remove_stale_in(&scratch.0, SystemTime::now());

    assert!(young.exists() && own.exists() && stranger.exists() && odd_name.exists());
  }

  #[cfg(target_os = "linux")]
  #[test]
  fn keeps_the_copy_of_a_running_process() {
    // pid 1 always exists
    let scratch = Scratch::new("running");
    let running = fake_copy(&scratch.0, 1, OLD, false);

    remove_stale_in(&scratch.0, SystemTime::now());

    assert!(running.exists());
  }

  /// On Windows a loaded assembly is the sign of a running process: hold the file open without
  /// sharing, as the runtime does, and the copy must stay.
  #[cfg(windows)]
  #[test]
  fn keeps_a_copy_whose_assembly_is_loaded() {
    use std::os::windows::fs::OpenOptionsExt;

    let scratch = Scratch::new("loaded");
    let loaded = fake_copy(&scratch.0, u32::MAX - 1, OLD, true);
    let held = fs::OpenOptions::new()
      .read(true)
      .share_mode(1) // FILE_SHARE_READ: others may read but not write
      .open(loaded.join("MyApp.Backend.dll"))
      .unwrap();

    remove_stale_in(&scratch.0, SystemTime::now());
    assert!(loaded.exists(), "a copy with a loaded assembly must stay");

    drop(held);
    remove_stale_in(&scratch.0, SystemTime::now());
    assert!(!loaded.exists(), "once nothing holds it, it goes");
  }

  /// A build replacing the backend at the moment it is copied: the copy waits and succeeds.
  #[cfg(windows)]
  #[test]
  fn waits_for_a_file_that_is_busy_for_a_moment() {
    use std::os::windows::fs::OpenOptionsExt;

    let scratch = Scratch::new("busy");
    let assembly = backend(&scratch);
    let busy = fs::OpenOptions::new().read(true).share_mode(0).open(&assembly).unwrap();
    let release = thread::spawn(move || {
      thread::sleep(Duration::from_millis(350));
      drop(busy);
    });

    let started = std::time::Instant::now();
    let copy = create_in(&scratch.0.join("temp"), &assembly).unwrap();
    release.join().unwrap();

    assert!(started.elapsed() >= Duration::from_millis(300), "it should have had to wait");
    assert_eq!(fs::read_to_string(&copy.assembly).unwrap(), "backend v1");
  }
}
