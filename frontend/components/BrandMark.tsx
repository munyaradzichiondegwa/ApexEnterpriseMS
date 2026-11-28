import React from "react";

type BrandMarkProps = {
  compact?: boolean;
  inverse?: boolean;
  className?: string;
};

/** ApexERP mark: the blue mountain/A and orange rising arrow used in the README hero artwork. */
export default function BrandMark({ compact = false, inverse = true, className = "" }: BrandMarkProps) {
  const wordColor = inverse ? "text-white" : "text-[#19385d]";
  const taglineColor = inverse ? "text-[#a9c7d8]" : "text-[#55718b]";
  return (
    <div className={`inline-flex items-center gap-2.5 ${className}`} aria-label="ApexERP">
      <svg viewBox="0 0 64 52" role="img" aria-hidden="true" className={compact ? "h-8 w-10 shrink-0" : "h-12 w-14 shrink-0"}>
        <path d="M3 46 23 8 38 31 46 18 61 46H44L37 34 30 46Z" fill="#1597d4" />
        <path d="m27 29 17-16-3-1 17-9-7 19-2-4-18 18Z" fill="#f28b28" />
      </svg>
      <span className="min-w-0">
        <span className={`block font-extrabold tracking-tight leading-none ${wordColor} ${compact ? "text-base" : "text-2xl"}`}>APEXERP</span>
        {!compact && <span className={`mt-1 block text-[9px] font-semibold tracking-[.16em] ${taglineColor}`}>INTEGRATE. OPTIMIZE. SCALE.</span>}
      </span>
    </div>
  );
}
