using System;
using System.Collections.Generic;
using System.Linq;

namespace ApexERP.Application.Services
{
    public enum ModifiedCashPolicyStatus { Draft, PendingApproval, Approved, Rejected }
    public enum ModifiedCashRecognitionTreatment { IncludeInBudgetActual, DiscloseSeparately }

    /// <summary>
    /// An explicitly approved policy for recognising obligations under a modified-cash budget basis.
    /// This evaluator is deliberately side-effect free: persistence, workflow approval and journal
    /// integration remain responsibilities of the calling application layer.
    /// </summary>
    public sealed record ModifiedCashRecognitionPolicy(
        Guid CompanyId,
        DateTime EffectiveFrom,
        DateTime? EffectiveTo,
        DateTime ReportingPeriodStart,
        DateTime ReportingPeriodEnd,
        int PostPeriodRecognitionWindowDays,
        ModifiedCashPolicyStatus Status,
        Guid PreparedBy,
        Guid? ApprovedBy,
        DateTime? ApprovedAt,
        ModifiedCashRecognitionTreatment Treatment,
        IReadOnlyDictionary<string, Guid> ClassificationMappings);

    /// <summary>
    /// Evidence for an obligation arising from a commitment. IncurredAt must be on or before
    /// the reporting cut-off; EvidenceAcceptedAt may fall in the policy's approved post-period window.
    /// </summary>
    public sealed record ModifiedCashCommitmentEvidence(
        Guid CompanyId,
        Guid CommitmentId,
        Guid SourceEventId,
        string SourceEventKind,
        DateTime IncurredAt,
        DateTime EvidenceAcceptedAt,
        decimal GrossObligationAmount,
        decimal PreviouslyRecognisedAmount,
        decimal PreviouslyLiquidatedAmount,
        Guid FundCentreId,
        Guid GlAccountId,
        bool IsCancelled,
        bool IsReversed);

    public sealed record ModifiedCashRecognitionDecision(
        bool IsEligible,
        string Reason,
        decimal RecognisableAmount,
        Guid? ClassificationGlAccountId,
        ModifiedCashRecognitionTreatment? Treatment);

    /// <summary>
    /// Fail-closed eligibility rules for modified-cash commitment recognition. It does not turn
    /// purchase-order encumbrances into cash flows: it only determines whether independently
    /// evidenced obligations satisfy a separately approved recognition policy.
    /// </summary>
    public static class ModifiedCashRecognitionPolicyEvaluator
    {
        public static ModifiedCashRecognitionDecision Evaluate(
            ModifiedCashRecognitionPolicy policy,
            ModifiedCashCommitmentEvidence evidence,
            IReadOnlySet<Guid> alreadyRecognisedSourceEvents)
        {
            if (policy.CompanyId == Guid.Empty || evidence.CompanyId != policy.CompanyId)
                return Reject("The evidence does not belong to the policy's legal entity.");
            if (policy.Status != ModifiedCashPolicyStatus.Approved ||
                !policy.ApprovedBy.HasValue || !policy.ApprovedAt.HasValue ||
                policy.PreparedBy == policy.ApprovedBy.Value)
                return Reject("A separately approved policy is required; the preparer cannot approve it.");
            if (policy.ReportingPeriodStart >= policy.ReportingPeriodEnd ||
                policy.EffectiveFrom >= (policy.EffectiveTo ?? DateTime.MaxValue) ||
                policy.PostPeriodRecognitionWindowDays < 0)
                return Reject("The policy period or recognition window is invalid.");
            if (policy.ReportingPeriodStart < policy.EffectiveFrom ||
                (policy.EffectiveTo.HasValue && policy.ReportingPeriodEnd > policy.EffectiveTo.Value))
                return Reject("The policy is not effective for the complete reporting period.");
            if (evidence.CommitmentId == Guid.Empty || evidence.SourceEventId == Guid.Empty ||
                evidence.FundCentreId == Guid.Empty || evidence.GlAccountId == Guid.Empty)
                return Reject("Commitment, source event and classification identifiers are mandatory.");
            if (alreadyRecognisedSourceEvents.Contains(evidence.SourceEventId))
                return Reject("The source event has already been recognised.");
            if (evidence.IsCancelled || evidence.IsReversed)
                return Reject("Cancelled or reversed obligations cannot be recognised.");
            if (string.IsNullOrWhiteSpace(evidence.SourceEventKind) ||
                !policy.ClassificationMappings.TryGetValue(evidence.SourceEventKind.Trim(), out var mappedGl) ||
                mappedGl == Guid.Empty)
                return Reject("The source event has no approved budget classification mapping.");
            if (evidence.IncurredAt < policy.ReportingPeriodStart ||
                evidence.IncurredAt >= policy.ReportingPeriodEnd)
                return Reject("The obligation was not incurred within the reporting period.");
            var latestEvidenceDate = policy.ReportingPeriodEnd.AddDays(policy.PostPeriodRecognitionWindowDays);
            if (evidence.EvidenceAcceptedAt < evidence.IncurredAt ||
                evidence.EvidenceAcceptedAt > latestEvidenceDate)
                return Reject("The evidence falls outside the approved recognition window.");
            if (evidence.GrossObligationAmount <= 0m ||
                evidence.PreviouslyRecognisedAmount < 0m ||
                evidence.PreviouslyLiquidatedAmount < 0m ||
                evidence.PreviouslyRecognisedAmount + evidence.PreviouslyLiquidatedAmount >
                    evidence.GrossObligationAmount)
                return Reject("The obligation amounts are invalid or exceed the source commitment.");
            var amount = evidence.GrossObligationAmount -
                evidence.PreviouslyRecognisedAmount - evidence.PreviouslyLiquidatedAmount;
            if (amount <= 0m)
                return Reject("There is no unrecognised and unliquidated obligation remaining.");

            return new ModifiedCashRecognitionDecision(
                true,
                "Eligible for controlled modified-cash recognition; this is not a cash receipt or payment.",
                amount,
                mappedGl,
                policy.Treatment);
        }

        private static ModifiedCashRecognitionDecision Reject(string reason) =>
            new(false, reason, 0m, null, null);
    }
}
