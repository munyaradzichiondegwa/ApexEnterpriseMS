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
  // The browser talks to /backend on this origin; Next.js forwards it to the API. Cookies set by the API (the HttpOnly refresh
  // token) therefore belong to this site, so sign-in survives browsers that block third-party cookies.
  async rewrites() {
    // API_PROXY_TARGET is where THIS server reaches the API (inside Docker: http://api:8080). It falls back to the public API URL.
    const target = (process.env.API_PROXY_TARGET || process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8080').replace(/\/+$/, '');
    return [{ source: '/backend/:path*', destination: `${target}/:path*` }];
  },
  async headers() {
    return [
      {
        source: '/:path*',
        headers: [
          { key: 'X-Content-Type-Options', value: 'nosniff' },
          { key: 'X-Frame-Options', value: 'DENY' },
          { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
          { key: 'Permissions-Policy', value: 'camera=(), microphone=(), geolocation=()' },
        ],
      },
    ];
  },
};
module.exports = nextConfig;
