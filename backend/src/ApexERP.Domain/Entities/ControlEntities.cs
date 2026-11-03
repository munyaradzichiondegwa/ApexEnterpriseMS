using System;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    /// <summary>
    /// A visible, owned and auditable exception raised by a business process that could not
    /// complete automatically -- for example an inter-company mirror posting that failed after the
    /// local invoice had already been posted. Nothing is swallowed: the failure becomes a row that a
    /// person can see, assign, retry and resolve, with the history kept (control requirement from the
    /// repository audit, finding 8, and the "exception management" workflow in the design document).
    /// </summary>
    public class ExceptionItem : TenantEntity
    {
        public ExceptionCategory Category { get; set; }

        /// <summary>Type of the record that raised the exception, e.g. "ArInvoice".</summary>
        public string SourceType { get; set; } = string.Empty;
        public Guid SourceId { get; set; }

        /// <summary>Latest failure message (updated on each failed retry).</summary>
        public string Message { get; set; } = string.Empty;

        public ExceptionStatus Status { get; set; } = ExceptionStatus.Open;
        public Guid? OwnerUserId { get; set; }

        public int RetryCount { get; set; }
        public DateTime? LastAttemptAt { get; set; }

        public DateTime? ResolvedAt { get; set; }
        public Guid? ResolvedBy { get; set; }
        public string? ResolutionNote { get; set; }
    }
}
