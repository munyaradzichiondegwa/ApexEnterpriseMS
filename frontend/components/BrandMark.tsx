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
        <path d="M5 43 19 29l8 8L44 17" fill="none" stroke="#1597d4" strokeWidth="7" strokeLinecap="square" strokeLinejoin="miter" />
        <path d="m34 16 15-5-5 15" fill="none" stroke="#f28b28" strokeWidth="7" strokeLinecap="square" strokeLinejoin="miter" />
        <path d="M7 47h45" fill="none" stroke="#19385d" strokeWidth="2" opacity=".35" />
      </svg>
      <span className="min-w-0">
        <span className={`block font-extrabold tracking-tight leading-none ${wordColor} ${compact ? "text-base" : "text-2xl"}`}>APEXERP</span>
        {!compact && <span className={`mt-1 block text-[9px] font-semibold tracking-[.16em] ${taglineColor}`}>INTEGRATE. OPTIMIZE. SCALE.</span>}
      </span>
    </div>
  );
}
