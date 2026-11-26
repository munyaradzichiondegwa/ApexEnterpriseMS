import type { Config } from "tailwindcss";

const config: Config = {
  content: ["./app/**/*.{js,ts,jsx,tsx,mdx}", "./components/**/*.{js,ts,jsx,tsx,mdx}"],
  theme: {
    extend: {
      colors: {
        brand: {
          50: "#eef3f8", 100: "#dce7f1", 200: "#bfd1e2", 300: "#9bb7ce",
          400: "#7098b8", 500: "#4c789d", 600: "#355f83", 700: "#284b6d",
          800: "#203e60", 900: "#19385d"
        },
        apex: { blue: "#1597d4", teal: "#20b6a5", orange: "#f28b28" }
      }
    },
  },
  plugins: [],
};
export default config;
