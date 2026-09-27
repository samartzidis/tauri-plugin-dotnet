import { WebviewWindow } from "@tauri-apps/api/webviewWindow";

let childCounter = 0;

/**
 * Opens a child window showing the same SPA on the `#/child` route. Windows are created from
 * JS with Tauri's own API; the `child-*` label is what the capability file grants access to.
 */
export function openChildWindow(): void {
  const label = `child-${++childCounter}`;
  const child = new WebviewWindow(label, {
    url: "index.html#/child",
    title: "Child Window (dynamic)",
    width: 600,
    height: 400,
    parent: "main",
  });
  void child.once("tauri://error", (e) => console.error(`Failed to create ${label}:`, e.payload));
}
