import type { Config } from "tailwindcss";

const config: Config = {
  content: ["./app/**/*.{js,ts,jsx,tsx,mdx}", "./components/**/*.{js,ts,jsx,tsx,mdx}"],
  theme: {
    extend: {
      colors: {
        brand: {
          50: "#eef7f4", 100: "#d5ebe3", 200: "#aad7c7", 300: "#7ec3ab",
          400: "#4aa886", 500: "#2f8a6a", 600: "#246e54", 700: "#1c5442",
          800: "#153e31", 900: "#0e2921"
        }
      }
    },
  },
  plugins: [],
};
export default config;
