// Turns whatever a failed request throws into a message a person can act on. Pure (no browser APIs) so it is unit-tested.

function messageFromBody(data: unknown): string | null {
  if (data == null) return null;

  if (typeof data === "string") {
    const text = data.trim();
    // Ignore HTML error pages from proxies; they are not useful to show.
    return text !== "" && !text.startsWith("<") ? text.slice(0, 300) : null;
  }
  if (typeof data !== "object") return null;

  const body = data as Record<string, unknown>;
  if (typeof body.error === "string" && body.error) return body.error;

  // ASP.NET Core validation problem details: { title, errors: { Field: ["message", ...] } }
  if (body.errors && typeof body.errors === "object") {
    for (const value of Object.values(body.errors as Record<string, unknown>)) {
      if (Array.isArray(value) && typeof value[0] === "string") return value[0];
      if (typeof value === "string") return value;
    }
  }
  for (const key of ["detail", "title", "message"]) {
    if (typeof body[key] === "string" && body[key]) return body[key] as string;
  }
  return null;
}

export function extractErrorMessage(err: unknown, apiBaseUrl = ""): string {
  const e = err as any;
  const response = e?.response;

  if (response) {
    const fromBody = messageFromBody(response.data);
    switch (response.status) {
      case 401:
        return fromBody ?? "Your session has expired. Please sign in again.";
      case 403:
        return fromBody ?? "You do not have permission to do this.";
      case 404:
        return fromBody ?? "The requested item was not found.";
      case 409:
        return fromBody ?? "That conflicts with data that already exists.";
      default:
        return fromBody ?? `The server returned an error (${response.status}).`;
    }
  }

  const message: string = typeof e?.message === "string" ? e.message : "";
  if (e?.code === "ECONNABORTED" || e?.code === "ETIMEDOUT" || /timeout/i.test(message)) {
    return "The server took too long to respond. Please try again.";
  }
  if (e?.code === "ERR_NETWORK" || message === "Network Error") {
    return apiBaseUrl
      ? `Cannot reach the ApexERP API at ${apiBaseUrl}. Check that it is running and that this web app's address is allowed by the API (CORS).`
      : "Cannot reach the ApexERP API.";
  }
  return message || "Something went wrong.";
}
