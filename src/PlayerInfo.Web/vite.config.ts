import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Local dev: `npm run dev` serves the UI on :5173 and proxies /api to the .NET API on :5011.
// `npm run build` writes into the API's wwwroot so `dotnet run` serves the built UI.
// The Docker image builds with `--outDir dist` and copies it into the runtime image.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5011',
    },
  },
  build: {
    outDir: '../PlayerInfo.Api/wwwroot',
    emptyOutDir: true,
  },
});
