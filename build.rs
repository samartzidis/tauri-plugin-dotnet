const COMMANDS: &[&str] = &["call", "cancel"];

fn main() {
  tauri_plugin::Builder::new(COMMANDS).build();
}
