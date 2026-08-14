import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  // main.ts intentionally keeps the large product shell in a single
  // template string. Use Vue's compiler-enabled ESM build so the shell is
  // rendered in both dev and production instead of leaving #app blank.
  resolve: {
    alias: {
      vue: "vue/dist/vue.esm-bundler.js",
    },
  },
  server: { port: 5173, host: "0.0.0.0" },
});

