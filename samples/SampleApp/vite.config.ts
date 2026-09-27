import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Tauri expects a fixed dev-server port and serves the production build from ./dist.
export default defineConfig({
  plugins: [react()],
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    watch: {
      // Rust and .NET rebuilds must not trigger frontend reloads.
      ignored: ["**/src-tauri/**", "**/src-dotnet/**"],
    },
  },
});
