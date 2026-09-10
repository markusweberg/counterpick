import { defineConfig } from "vite";

export default defineConfig({
  // WebView2 serves the build from a virtual host root, so relative asset paths
  // are what we want - not absolute /assets/... URLs.
  base: "./",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    target: "chrome120", // WebView2 on Win11 is well past this
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
