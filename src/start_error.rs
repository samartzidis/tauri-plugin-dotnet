//! Telling the user that .NET could not be started, before the app is ended (see [`OnStartError`]).
//!
//! [`OnStartError`]: crate::OnStartError

use tauri::{AppHandle, Runtime};

use crate::{error::BridgeError, hostfxr::RUNTIME_MISSING};

/// Keeps a long hostfxr report from making a dialog taller than the screen. The whole error is in the log.
const MAX_DIALOG_DETAILS_CHARS: usize = 1500;

/// What the dialog says for `error`. A missing runtime gets the user-facing first line of its message, naming what to
/// install; any other failure is a broken app, so it gets the details.
pub(crate) fn dialog_text(app_name: &str, error: &BridgeError) -> String {
  if error.kind == RUNTIME_MISSING {
    let summary = error.message.lines().next().unwrap_or_default();
    return format!("{summary}\n\nInstall it, then start {app_name} again.");
  }

  // A developer sees this in a debug build; reinstalling is only advice for someone who installed the app
  let advice = if cfg!(debug_assertions) { "" } else { " Reinstalling it may fix this." };
  format!(
    "{app_name} could not start because its .NET backend failed to load.{advice}\n\nDetails:\n{}",
    truncate_chars(&error.message, MAX_DIALOG_DETAILS_CHARS)
  )
}

fn truncate_chars(text: &str, max: usize) -> String {
  match text.char_indices().nth(max) {
    Some((end, _)) => format!("{}...", &text[..end]),
    None => text.to_string(),
  }
}

/// Shows the dialog for `error` and ends the process with exit code 1. Called from the plugin's setup, on the main
/// thread and before the event loop runs, so there is nothing to shut down yet.
pub(crate) fn show_and_exit<R: Runtime>(app: &AppHandle<R>, error: &BridgeError) -> ! {
  let app_name = app.package_info().name.clone();
  show(&app_name, &dialog_text(&app_name, error));
  log::logger().flush();
  std::process::exit(1)
}

#[cfg(not(any(target_os = "android", target_os = "ios")))]
fn show(title: &str, text: &str) {
  use rfd::{MessageButtons, MessageDialog, MessageLevel};

  MessageDialog::new()
    .set_level(MessageLevel::Error)
    .set_title(title)
    .set_description(text)
    .set_buttons(MessageButtons::Ok)
    .show();
}

/// No dialog on mobile, where the in-process host does not run anyway; the error is in the log.
#[cfg(any(target_os = "android", target_os = "ios"))]
fn show(_title: &str, _text: &str) {}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn a_missing_runtime_shows_only_the_users_line() {
    let error = BridgeError::new(
      RUNTIME_MISSING,
      "This app needs the .NET Runtime 8.0 (x64), which is not installed on this computer.\nNo .NET runtime (hostfxr) was found. Searched: C:\\dotnet",
    );

    assert_eq!(
      dialog_text("My App", &error),
      "This app needs the .NET Runtime 8.0 (x64), which is not installed on this computer.\n\n\
       Install it, then start My App again."
    );
  }

  #[test]
  fn any_other_failure_shows_its_details() {
    let error = BridgeError::new("HostInitFailed", "The Tauri.Plugin.DotNet package is version 0.2.0, but ...");

    let text = dialog_text("My App", &error);

    assert!(text.starts_with("My App could not start because its .NET backend failed to load."), "{text}");
    assert!(text.ends_with("Details:\nThe Tauri.Plugin.DotNet package is version 0.2.0, but ..."), "{text}");
  }

  #[test]
  fn long_details_are_cut_short() {
    let error = BridgeError::new("HostInitFailed", "x".repeat(MAX_DIALOG_DETAILS_CHARS + 500));

    let text = dialog_text("My App", &error);

    assert!(text.ends_with(&format!("{}...", "x".repeat(10))), "{text}");
    assert!(text.len() < MAX_DIALOG_DETAILS_CHARS + 200);
  }
}
