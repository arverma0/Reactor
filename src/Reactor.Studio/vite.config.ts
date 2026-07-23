/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The Studio is served by `reactor serve` / `reactor studio` from embedded resources. During development,
// `npm run dev` proxies API calls to a running server (REACTOR_URL, default http://127.0.0.1:5180).
const target = process.env.REACTOR_URL ?? "http://127.0.0.1:5180";
const proxied = ["/studio/api", "/pipelines", "/validate", "/healthz", "/console"];

export default defineConfig({
  plugins: [react()],
  base: "/",
  build: {
    outDir: process.env.STUDIO_OUT_DIR ?? "dist",
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 4096,
    assetsInlineLimit: 0,
  },
  server: {
    port: 5173,
    proxy: Object.fromEntries(proxied.map((path) => [path, { target, changeOrigin: false }])),
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
});
