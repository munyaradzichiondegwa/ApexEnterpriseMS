using System.Text;
using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class ExternalCashbookCsvParserTests
{
    private const string Header =
        "ExternalTransactionId,TransactionDate,PostingDate,SourceDocumentReference,Description,Debit,Credit,Currency,ExternalAccountCode,ReversalIndicator,OriginalTransactionId";

    private static byte[] Csv(params string[] rows) =>
        Encoding.UTF8.GetBytes(Header + "\n" + string.Join("\n", rows));

    private static ParsedExternalCashbook Parse(byte[] bytes, int count = 2, decimal debits = 20m,
        decimal credits = 50m, decimal opening = 100m, decimal closing = 130m) =>
        ExternalCashbookCsvParser.Parse(bytes, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            debits, credits, opening, closing, count);

    [Fact]
    public void Parses_rows_and_reconciles_declared_control_totals()
    {
        var parsed = Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,2026-01-02T00:00:00Z,RCPT-1,Receipt,0,50,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,2026-01-03T00:00:00Z,PAY-1,Payment,20,0,USD,CASH,false,"));

        Assert.Equal(2, parsed.Transactions.Count);
        Assert.Equal(20m, parsed.TotalDebits);
        Assert.Equal(50m, parsed.TotalCredits);
        Assert.Equal(64, parsed.Transactions[0].TransactionFingerprint.Length);
    }

    [Fact]
    public void Supports_quoted_descriptions_containing_commas()
    {
        var parsed = Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,\"Receipt, bank transfer\",0,50,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,"));
        Assert.Equal("Receipt, bank transfer", parsed.Transactions[0].Description);
    }

    [Fact]
    public void Rejects_duplicate_external_transaction_ids()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,Receipt,0,50,USD,CASH,false,",
            "TX-1,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,")));
    }

    [Fact]
    public void Rejects_transaction_outside_declared_period()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2025-12-31T00:00:00Z,,RCPT-1,Receipt,0,50,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,")));
    }

    [Fact]
    public void Rejects_currency_mismatch()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,Receipt,0,50,EUR,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,")));
    }

    [Fact]
    public void Rejects_ambiguous_debit_and_credit_or_zero_amount_rows()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,Receipt,5,50,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,")));
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,Receipt,0,0,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,")));
    }

    [Fact]
    public void Rejects_control_total_and_balance_mismatches()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,Receipt,0,50,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,"), credits: 51m));
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,Receipt,0,50,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,"), closing: 131m));
    }

    [Fact]
    public void Requires_reversal_to_reference_original_transaction()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,REV-1,Reversal,20,0,USD,CASH,true,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,0,50,USD,CASH,false,")));
    }

    [Fact]
    public void Rejects_invalid_utf8_and_wrong_header()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(new byte[] { 0xff, 0xfe, 0xfd }));
        Assert.Throws<InvalidOperationException>(() => Parse(Encoding.UTF8.GetBytes("WrongHeader\nrow")));
    }

    [Fact]
    public void Rejects_control_totals_that_are_negative()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(Csv(
            "TX-1,2026-01-02T00:00:00Z,,RCPT-1,Receipt,0,50,USD,CASH,false,",
            "TX-2,2026-01-03T00:00:00Z,,PAY-1,Payment,20,0,USD,CASH,false,"), debits: -1m));
    }
}
