import React from "react";

type BrandMarkProps = {
  compact?: boolean;
  inverse?: boolean;
  className?: string;
};

/** ApexERP brand mark based on the supplied product specification artwork. */
export default function BrandMark({ compact = false, inverse = true, className = "" }: BrandMarkProps) {
  const wordColor = inverse ? "text-white" : "text-[#19385d]";
  const taglineColor = inverse ? "text-[#a9c7d8]" : "text-[#55718b]";
  return (
    <div className={`inline-flex items-center gap-2.5 ${className}`} aria-label="ApexERP">
      <svg viewBox="0 0 64 52" role="img" aria-hidden="true" className={compact ? "h-8 w-9 shrink-0" : "h-12 w-14 shrink-0"}>
        <path d="M4 46 24 7l18 31h-11L24 23 14 43z" fill="#1597d4" />
        <path d="m25 43 23-38 10 9-6 6-3-3-17 28z" fill="#f28b28" />
        <path d="m18 37 6-12 7 12z" fill="#19385d" opacity=".18" />
      </svg>
      <span className="min-w-0">
        <span className={`block font-extrabold tracking-tight leading-none ${wordColor} ${compact ? "text-base" : "text-2xl"}`}>APEXERP</span>
        {!compact && <span className={`mt-1 block text-[9px] font-semibold tracking-[.16em] ${taglineColor}`}>INTEGRATE. OPTIMIZE. SCALE.</span>}
      </span>
    </div>
  );
}
