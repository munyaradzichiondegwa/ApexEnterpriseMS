namespace ApexERP.Domain.Controls
{
    /// <summary>Accounting identities the reporting layer must prove (spec sections 13.3, 21, 28.4).</summary>
    public static class FinancialIdentities
    {
        public static bool BalanceSheetBalances(decimal assets, decimal liabilities, decimal equity, decimal tolerance = 0.005m) =>
            Math.Abs(assets - (liabilities + equity)) <= tolerance;

        public static bool EquityRollForwardHolds(decimal opening, decimal movements, decimal closing, decimal tolerance = 0.005m) =>
            Math.Abs(closing - (opening + movements)) <= tolerance;

        public static bool CashReconciles(decimal opening, decimal netMovement, decimal closing, decimal tolerance = 0.005m) =>
            Math.Abs(closing - (opening + netMovement)) <= tolerance;

        /// <summary>Public-sector commitment control (spec section 21).</summary>
        public static decimal AvailableBudget(decimal approved, decimal commitments, decimal actual) => approved - commitments - actual;

        public static bool CommitmentAllowed(decimal approved, decimal commitments, decimal actual, decimal newCommitment, bool authorisedException) =>
            authorisedException || AvailableBudget(approved, commitments, actual) >= newCommitment;
    }
}
