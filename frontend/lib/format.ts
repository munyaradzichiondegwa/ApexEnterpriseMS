// Display helpers shared by the finance screens. Pure functions so they can be tested.

export function fmtMoney(value: number | null | undefined, currency = "USD"): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "-";
  try {
    return value.toLocaleString("en-US", { style: "currency", currency, minimumFractionDigits: 2, maximumFractionDigits: 2 });
  } catch {
    return value.toFixed(2);
  }
}

export function fmtNumber(value: number | null | undefined, digits = 2): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "-";
  return value.toLocaleString("en-US", { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function fmtDate(value: string | null | undefined): string {
  if (!value) return "-";
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? "-" : d.toISOString().slice(0, 10);
}

export function fmtDateTime(value: string | null | undefined): string {
  if (!value) return "-";
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? "-" : d.toISOString().slice(0, 16).replace("T", " ") + " UTC";
}

/** Today as yyyy-MM-dd for <input type="date">. */
export function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/** Splits "AR,Manual" style lists and trims them. */
export function splitCsv(text: string): string[] {
  return text.split(",").map((s) => s.trim()).filter(Boolean);
}

/** "NrvWriteDown" -> "Nrv Write Down"; "DETECTED" stays readable. */
export function humanise(text: string | null | undefined): string {
  if (!text) return "";
  return text.replace(/_/g, " ").replace(/([a-z])([A-Z])/g, "$1 $2");
}

export const STATUS_TONE: Record<string, "good" | "warn" | "bad" | "info" | "neutral"> = {
  Draft: "neutral", Approved: "info", Posted: "good", Active: "good", Completed: "good", Matched: "good", Reversed: "neutral",
  Pending: "warn", Failed: "bad", DeadLetter: "bad", Rejected: "bad", Cancelled: "neutral", Processed: "good",
  Reconciled: "good", Explained: "info", SignedOff: "good", Unreconciled: "bad", Certified: "good", Done: "good", Blocked: "bad", Waived: "info", Open: "warn",
  Critical: "bad", High: "bad", Medium: "warn", Low: "neutral", Released: "good", Authorised: "info", Verified: "good",
  DETECTED: "warn", CLASSIFIED: "warn", ASSIGNED: "info", INVESTIGATED: "info", RESOLVED: "info", INDEPENDENTLY_REVIEWED: "info", CLOSED: "good",
};
