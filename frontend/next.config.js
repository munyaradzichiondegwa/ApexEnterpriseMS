// On a hosting platform (Vercel sets VERCEL=1), refuse to build without the API address. NEXT_PUBLIC_* values are
// compiled into the browser bundle at build time, so a build without it would ship an app that cannot reach its server.
if (process.env.VERCEL && !(process.env.NEXT_PUBLIC_API_URL || "").trim()) {
  throw new Error(
    "NEXT_PUBLIC_API_URL is not set. Add it to the project's Environment Variables (the public URL of the ApexERP API, " +
      "e.g. https://api.example.co.zw) and redeploy."
  );
}

/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  output: 'standalone',
};
module.exports = nextConfig;
