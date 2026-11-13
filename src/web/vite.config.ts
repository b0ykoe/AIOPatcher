import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Entwicklung: `npm run dev` (Port 5173) leitet API-Aufrufe an den lokal laufenden Server (Port 9443) weiter.
// Build: landet direkt in wwwroot des Servers, der das Frontend ausliefert.
// Lokal 7443/7444 statt 9443/8443: 8443 liegt unter Windows oft in einem von Hyper-V reservierten Portbereich.
const backend = "https://localhost:7443";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../AIOPatcher.Server/wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: backend, secure: false },
      "/healthz": { target: backend, secure: false },
    },
  },
});
