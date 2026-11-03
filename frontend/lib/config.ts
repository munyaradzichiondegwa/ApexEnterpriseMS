// Build-time configuration for the browser bundle.
//
// Next.js inlines NEXT_PUBLIC_* values when the app is BUILT, so a hosting project (for example Vercel) must have
// NEXT_PUBLIC_API_URL set before the build runs. Silently falling back to localhost in a production build used to hide
// that mistake: the app shipped, then every request failed with a confusing network/CORS error. Now a production build
// without the setting reports exactly what is wrong.

export interface ApiConfig {
  /** Absolute API origin without a trailing slash, or "" when the app is misconfigured. */
  baseUrl: string;
  /** A human-readable explanation when the app cannot work as built, otherwise null. */
  error: string | null;
}

const DEVELOPMENT_DEFAULT = "http://localhost:8080";

export function resolveApiConfig(rawUrl: string | undefined | null, production: boolean): ApiConfig {
  const value = (rawUrl ?? "").trim();

  if (value === "") {
    if (production) {
      return {
        baseUrl: "",
        error:
          "ApexERP is not configured: NEXT_PUBLIC_API_URL was not set when this app was built, so it does not know where " +
          "the API is. Set it to the API's public URL in the hosting project's environment variables and redeploy.",
      };
    }
    return { baseUrl: DEVELOPMENT_DEFAULT, error: null }; // local development convenience only
  }

  try {
    const url = new URL(value);
    if (url.protocol !== "http:" && url.protocol !== "https:") throw new Error("unsupported protocol");
    return { baseUrl: value.replace(/\/+$/, ""), error: null };
  } catch {
    return {
      baseUrl: "",
      error: `ApexERP is misconfigured: NEXT_PUBLIC_API_URL ("${value}") is not a valid http(s) URL.`,
    };
  }
}

// NOTE: the property access must stay literal so Next.js can inline the value at build time.
const resolved = resolveApiConfig(process.env.NEXT_PUBLIC_API_URL, process.env.NODE_ENV === "production");

export const API_BASE_URL = resolved.baseUrl;
export const API_CONFIG_ERROR = resolved.error;
