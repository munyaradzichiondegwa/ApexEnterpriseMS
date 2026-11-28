using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class ModifiedCashRecognitionPolicyEvaluatorTests
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Preparer = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Approver = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Commitment = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid SourceEvent = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid Fund = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ExpenseGl = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ModifiedCashRecognitionPolicy Policy(
        ModifiedCashPolicyStatus status = ModifiedCashPolicyStatus.Approved,
        Guid? approvedBy = null,
        int windowDays = 30) =>
        new(
            Company,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            windowDays,
            status,
            Preparer,
            approvedBy ?? Approver,
            new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedCashRecognitionTreatment.IncludeInBudgetActual,
            new Dictionary<string, Guid> { ["GoodsReceived"] = ExpenseGl });

    private static ModifiedCashCommitmentEvidence Evidence(
        decimal gross = 100m,
        decimal recognised = 20m,
        decimal liquidated = 30m,
        DateTime? incurred = null,
        DateTime? accepted = null) =>
        new(
            Company,
            Commitment,
            SourceEvent,
            "GoodsReceived",
            incurred ?? new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc),
            accepted ?? new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            gross,
            recognised,
            liquidated,
            Fund,
            ExpenseGl,
            false,
            false);

    [Fact]
    public void Evaluate_RecognisesOnlyUnrecognisedAndUnliquidatedBalance()
    {
        var result = ModifiedCashRecognitionPolicyEvaluator.Evaluate(
            Policy(), Evidence(), new HashSet<Guid>());

        Assert.True(result.IsEligible);
        Assert.Equal(50m, result.RecognisableAmount);
        Assert.Equal(ExpenseGl, result.ClassificationGlAccountId);
        Assert.Equal(ModifiedCashRecognitionTreatment.IncludeInBudgetActual, result.Treatment);
    }

    [Fact]
    public void Evaluate_RejectsPolicyWithoutIndependentApproval()
    {
        var result = ModifiedCashRecognitionPolicyEvaluator.Evaluate(
            Policy(approvedBy: Preparer), Evidence(), new HashSet<Guid>());

        Assert.False(result.IsEligible);
        Assert.Equal(0m, result.RecognisableAmount);
        Assert.Null(result.ClassificationGlAccountId);
    }

    [Fact]
    public void Evaluate_RejectsDuplicateSourceEvent()
    {
        var result = ModifiedCashRecognitionPolicyEvaluator.Evaluate(
            Policy(), Evidence(), new HashSet<Guid> { SourceEvent });

        Assert.False(result.IsEligible);
        Assert.Contains("already been recognised", result.Reason);
    }

    [Fact]
    public void Evaluate_RejectsEvidenceOutsidePostPeriodWindow()
    {
        var lateEvidence = Evidence(accepted: new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = ModifiedCashRecognitionPolicyEvaluator.Evaluate(
            Policy(windowDays: 30), lateEvidence, new HashSet<Guid>());

        Assert.False(result.IsEligible);
        Assert.Contains("outside the approved recognition window", result.Reason);
    }

    [Fact]
    public void Evaluate_RejectsCommitmentFromAnotherCompany()
    {
        var foreign = Evidence() with { CompanyId = Guid.NewGuid() };
        var result = ModifiedCashRecognitionPolicyEvaluator.Evaluate(
            Policy(), foreign, new HashSet<Guid>());

        Assert.False(result.IsEligible);
        Assert.Contains("legal entity", result.Reason);
    }

    [Fact]
    public void Evaluate_RejectsObligationWithoutApprovedClassification()
    {
        var unclassified = Evidence() with { SourceEventKind = "UnmappedEvent" };
        var result = ModifiedCashRecognitionPolicyEvaluator.Evaluate(
            Policy(), unclassified, new HashSet<Guid>());

        Assert.False(result.IsEligible);
        Assert.Contains("classification mapping", result.Reason);
    }

    [Fact]
    public void Evaluate_RejectsCancelledOrReversedCommitment()
    {
        var cancelled = Evidence() with { IsCancelled = true };
        var result = ModifiedCashRecognitionPolicyEvaluator.Evaluate(
            Policy(), cancelled, new HashSet<Guid>());

        Assert.False(result.IsEligible);
    }
}
