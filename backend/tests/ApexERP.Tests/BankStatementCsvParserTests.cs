using System.Text;
using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class BankStatementCsvParserTests
{
    private static readonly DateTime Start = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ParsedBankStatement Parse(string csv, int count = 1, decimal debits = 10m,
        decimal credits = 20m, decimal opening = 100m, decimal closing = 110m) =>
        BankStatementCsvParser.Parse(Encoding.UTF8.GetBytes(csv), "USD", Start, End,
            debits, credits, opening, closing, count);

    private const string Header =
        "ExternalTransactionId,TransactionDate,Description,Reference,Debit,Credit,Currency";

    [Fact]
    public void Parse_AcceptsControlTotalReconciledCsvAndQuotedDescription()
    {
        var csv = Header + "\r\n" +
            "txn-001,2026-12-15T10:00:00Z,\"Office, rent\",REF-001,10,0,USD\r\n" +
            "txn-002,2026-12-16T10:00:00Z,Customer receipt,REF-002,0,20,USD";

        var result = Parse(csv, count: 2, debits: 10m, credits: 20m, opening: 100m, closing: 110m);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("Office, rent", result.Transactions[0].Description);
        Assert.Equal(10m, result.TotalDebits);
        Assert.Equal(20m, result.TotalCredits);
        Assert.Equal(64, result.Transactions[0].TransactionFingerprint.Length);
    }

    [Fact]
    public void Parse_AcceptsHeaderOnlyZeroActivityStatementWhenBalancesAgree()
    {
        var result = Parse(Header, count: 0, debits: 0m, credits: 0m, opening: 100m, closing: 100m);

        Assert.Empty(result.Transactions);
        Assert.Equal(0m, result.TotalDebits);
        Assert.Equal(0m, result.TotalCredits);
    }

    [Fact]
    public void Parse_RejectsCharactersAfterClosingQuotedField()
    {
        var csv = Header + "\n" +
            "txn-001,2026-12-15T10:00:00Z,\"Quoted\"x,REF-001,10,0,USD";

        var error = Assert.Throws<InvalidOperationException>(() =>
            Parse(csv, debits: 10m, credits: 0m, opening: 100m, closing: 90m));
        Assert.Contains("after closing quote", error.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicateExternalTransactionIdentity()
    {
        var csv = Header + "\n" +
            "txn-001,2026-12-15T10:00:00Z,First,REF-001,10,0,USD\n" +
            "txn-001,2026-12-16T10:00:00Z,Second,REF-002,0,20,USD";

        var error = Assert.Throws<InvalidOperationException>(() =>
            Parse(csv, count: 2, debits: 10m, credits: 20m, opening: 100m, closing: 110m));
        Assert.Contains("repeats external transaction identifier", error.Message);
    }

    [Fact]
    public void Parse_RejectsTransactionWithBothDebitAndCredit()
    {
        var csv = Header + "\n" +
            "txn-001,2026-12-15T10:00:00Z,Invalid,REF-001,10,20,USD";

        var error = Assert.Throws<InvalidOperationException>(() =>
            Parse(csv, debits: 10m, credits: 20m, opening: 100m, closing: 110m));
        Assert.Contains("exactly one positive debit or credit", error.Message);
    }

    [Fact]
    public void Parse_RejectsTransactionOutsideStatementPeriod()
    {
        var csv = Header + "\n" +
            "txn-001,2027-01-01T00:00:00Z,Outside,REF-001,10,0,USD";

        var error = Assert.Throws<InvalidOperationException>(() =>
            Parse(csv, debits: 10m, credits: 0m, opening: 100m, closing: 90m));
        Assert.Contains("outside the declared statement period", error.Message);
    }

    [Fact]
    public void Parse_RejectsControlTotalMismatch()
    {
        var csv = Header + "\n" +
            "txn-001,2026-12-15T10:00:00Z,Debit,REF-001,10,0,USD";

        var error = Assert.Throws<InvalidOperationException>(() =>
            Parse(csv, count: 1, debits: 9m, credits: 0m, opening: 100m, closing: 91m));
        Assert.Contains("do not match the declared statement control totals", error.Message);
    }

    [Fact]
    public void Parse_RejectsUnbalancedOpeningAndClosingBalances()
    {
        var csv = Header + "\n" +
            "txn-001,2026-12-15T10:00:00Z,Debit,REF-001,10,0,USD";

        var error = Assert.Throws<InvalidOperationException>(() =>
            Parse(csv, count: 1, debits: 10m, credits: 0m, opening: 100m, closing: 95m));
        Assert.Contains("does not reconcile to the declared closing balance", error.Message);
    }

    [Fact]
    public void Parse_RejectsCurrencyDifferentFromRegisteredBankAccount()
    {
        var csv = Header + "\n" +
            "txn-001,2026-12-15T10:00:00Z,Debit,REF-001,10,0,ZWL";

        var error = Assert.Throws<InvalidOperationException>(() =>
            Parse(csv, count: 1, debits: 10m, credits: 0m, opening: 100m, closing: 90m));
        Assert.Contains("currency does not match", error.Message);
    }
}
