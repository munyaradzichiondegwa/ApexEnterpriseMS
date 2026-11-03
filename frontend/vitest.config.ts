import { defineConfig } from "vitest/config";

// The tested modules (session, permissions, errors, config) are plain TypeScript with no DOM dependency.
export default defineConfig({
  test: {
    environment: "node",
    include: ["tests/**/*.test.ts"],
  },
});
