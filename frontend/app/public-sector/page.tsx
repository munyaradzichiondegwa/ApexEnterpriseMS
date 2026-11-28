"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";
import { downloadCsv } from "@/lib/exports";

export default function PublicSectorPage() {
  const [tab, setTab] = useState<"funds" | "tenders" | "assets" | "ipsas24">("funds");
  const [year] = useState(new Date().getFullYear());
  const [fundCentres, setFundCentres] = useState<any[]>([]);
  const [budgetAccounts, setBudgetAccounts] = useState<any[]>([]);
  const [utilization, setUtilization] = useState<any[]>([]);
  const [tenders, setTenders] = useState<any[]>([]);
  const [batches, setBatches] = useState<any[]>([]);
  const [budgetComparison, setBudgetComparison] = useState<any | null>(null);
  const [budgetComparisonLoading, setBudgetComparisonLoading] = useState(false);
  const [approvedBudgetVersions, setApprovedBudgetVersions] = useState<any[]>([]);
  const [budgetExecutionActuals, setBudgetExecutionActuals] = useState<any[]>([]);
  const [cashPeriodAttestations, setCashPeriodAttestations] = useState<any[]>([]);
  const [expectedCashSources, setExpectedCashSources] = useState<any[]>([]);
  const [cashSourceSubmissions, setCashSourceSubmissions] = useState<any[]>([]);
  const [registerAttestation, setRegisterAttestation] = useState<any | null>(null);
  const [registerAttestationEvidence, setRegisterAttestationEvidence] = useState("");
  const [expectedSourceDraft, setExpectedSourceDraft] = useState({ sourceKind: "BankAccount", sourceCode: "", sourceName: "", sourceIdentifier: "", isRequired: true });
  const [sourceSubmissionDraft, setSourceSubmissionDraft] = useState({ expectedCashSourceId: "", recordCount: 0, controlTotal: 0, openingBalance: 0, closingBalance: 0, currency: "USD", evidenceReference: "", revisionReason: "" });
  const [recordLineDraft, setRecordLineDraft] = useState({ submissionId: "", sourceRecordId: "", sourceReference: "", budgetExecutionActualId: "", isExcluded: false, exclusionReason: "" });
  const [reconciliationLines, setReconciliationLines] = useState<any[]>([]);
  const [zeroActivityEvidence, setZeroActivityEvidence] = useState("");
  const [cashActualReconciliation, setCashActualReconciliation] = useState<any | null>(null);
  const [cashBankTransactions, setCashBankTransactions] = useState<any[]>([]);
  const [cashArReceipts, setCashArReceipts] = useState<any[]>([]);
  const [cashApPayments, setCashApPayments] = useState<any[]>([]);
  const [cashActualDraft, setCashActualDraft] = useState({ budgetVersionId: "", budgetVersionLineId: "", sourceKind: "BankTransaction", sourceRecordId: "", eventKind: "CashPayment", effectiveDate: new Date().toISOString().slice(0, 10), cashFlowSection: "Operating" });
  const [budgetDraftId, setBudgetDraftId] = useState<string | null>(null);
  const [budgetDraft, setBudgetDraft] = useState({ kind: "Original", currency: "USD", accountingBasis: "Accrual", classificationBasis: "", entityScope: "", periodStart: `${year}-01-01T00:00:00.000Z`, periodEnd: `${year + 1}-01-01T00:00:00.000Z`, authorityReference: "", amendmentReason: "", amendmentNature: "" });
  const [budgetLines, setBudgetLines] = useState([{ fundCentreId: "", glAccountId: "", costCentreId: null as string | null, description: "", classificationCode: "", treasuryClassificationCode: "", noteReference: "", amount: 0 }]);
  const [varianceDraft, setVarianceDraft] = useState({ budgetVersionLineId: "", explanation: "", evidenceReference: "", commentReference: "" });
  const [budgetReconciliations, setBudgetReconciliations] = useState<any[]>([]);
  const [budgetReconciliationDraft, setBudgetReconciliationDraft] = useState({
    type: "Basis", description: "", statementSection: "Operating",
    revenueAmount: 0, expenseAmount: 0, operatingAmount: 0,
    investingAmount: 0, financingAmount: 0, supportingReference: "",
  });
  const [error, setError] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [newFund, setNewFund] = useState({ code: "", name: "", level: "Vote", financialYear: year, appropriationAmount: 0, carriedForward: 0 });
  const [newTender, setNewTender] = useState({ title: "", specifications: "", submissionDeadline: "" });

  function load() {
    api.get(`/public-sector/fund-centres?year=${year}`).then((r) => setFundCentres(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/gl/accounts").then((r) => setBudgetAccounts(r.data.filter((a: any) => a.isActive))).catch(() => {});
    api.get(`/public-sector/fund-centres/utilization?year=${year}`).then((r) => setUtilization(r.data)).catch(() => {});
    api.get("/public-sector/tenders").then((r) => setTenders(r.data)).catch(() => {});
    api.get("/public-sector/asset-verification/batches").then((r) => setBatches(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createFund(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/public-sector/fund-centres", newFund);
      setMsg("Fund centre created.");
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createBudgetVersion(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const response = await api.post("/public-sector/budget-versions", {
        financialYear: year,
        ...budgetDraft,
        amendmentNature: budgetDraft.kind === "Revised" ? budgetDraft.amendmentNature : null,
        lines: budgetLines.map((line) => ({ ...line, amount: Number(line.amount) })),
      });
      setBudgetDraftId(response.data.id);
      setMsg(`${budgetDraft.kind} budget draft created. Submit it for independent approval before it appears in the statement.`);
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  function exportBudgetComparison() {
    if (!budgetComparison) return;
    const lines = budgetComparison.lines ?? [];
    const incomeLines = lines.filter((line: any) => line.accountType === "Income");
    const expenseLines = lines.filter((line: any) => line.accountType === "Expense");
    const sum = (items: any[], field: string) => items.reduce((total, line) => total + Number(line[field] ?? 0), 0);
    const canCalculateSurplusDeficit = lines.length > 0 && incomeLines.length + expenseLines.length === lines.length;
    const actualsComplete = lines.every((line: any) => line.actualOnComparableBasis != null);
    const originalSurplusDeficit = canCalculateSurplusDeficit
      ? sum(incomeLines, "originalBudget") - sum(expenseLines, "originalBudget") : null;
    const finalSurplusDeficit = canCalculateSurplusDeficit
      ? sum(incomeLines, "finalBudget") - sum(expenseLines, "finalBudget") : null;
    const actualSurplusDeficit = canCalculateSurplusDeficit && actualsComplete
      ? sum(incomeLines, "actualOnComparableBasis") - sum(expenseLines, "actualOnComparableBasis") : null;
    const summaryRow = {
      description: "TOTAL SURPLUS / (DEFICIT) FOR THE YEAR",
      treasuryClassificationCode: "", classificationCode: "", noteReference: "",
      accountType: "Summary", fundCentreCode: "", fundCentreName: "", fundCentreLevel: "",
      costCentreCode: "", costCentreName: "", accountCode: "", accountName: "",
      originalBudget: originalSurplusDeficit, finalBudget: finalSurplusDeficit,
      actualOnComparableBasis: actualSurplusDeficit,
      variance: actualSurplusDeficit == null || finalSurplusDeficit == null
        ? null : finalSurplusDeficit - actualSurplusDeficit,
      variancePercent: null, originalToFinalExplanation: "", budgetVersionLineId: null,
    };
    const exportRows = canCalculateSurplusDeficit ? [...lines, summaryRow] : lines;
    downloadCsv(`apexerp-ipsas24-budget-comparison-${year}`, exportRows, [
      // Reporting metadata required to interpret the separate statement.
      { header: "Financial Year", value: () => budgetComparison.financialYear },
      { header: "Reporting Period Start", value: () => new Date(budgetComparison.periodStart).toISOString().slice(0, 10) },
      { header: "Reporting Period End", value: () => new Date(new Date(budgetComparison.periodEnd).getTime() - 86400000).toISOString().slice(0, 10) },
      { header: "Currency", value: () => budgetComparison.currency },
      { header: "Budget Basis", value: () => budgetComparison.accountingBasis },
      { header: "Classification Basis", value: () => budgetComparison.classificationBasis },
      { header: "Entity Scope", value: () => budgetComparison.entityScope },
      // Core Appendix 5 statement columns, in the prescribed order.
      { header: "Budget Line", value: (row: any) => row.description },
      { header: "Notes", value: (row: any) => row.noteReference ?? "" },
      { header: "Original", value: (row: any) => row.originalBudget },
      { header: "Final", value: (row: any) => row.finalBudget },
      { header: "Actual Amounts on Comparable Basis", value: (row: any) => row.actualOnComparableBasis },
      { header: "Difference between final budget and actual", value: (row: any) => row.variance },
      { header: "Comment Ref", value: (row: any) => budgetComparison.varianceExplanations?.find((x: any) => x.budgetVersionLineId === row.budgetVersionLineId && x.varianceAmount === row.variance && x.status === "Approved")?.commentReference ?? budgetComparison.varianceExplanations?.find((x: any) => x.budgetVersionLineId === row.budgetVersionLineId && x.varianceAmount === row.variance && x.status === "Approved")?.evidenceReference ?? "" },
      // Supporting dimensions and audit references.
      { header: "Treasury Classification", value: (row: any) => row.treasuryClassificationCode ?? "" },
      { header: "GL SubType", value: (row: any) => row.classificationCode },
      { header: "Fund Centre Code", value: (row: any) => row.fundCentreCode },
      { header: "Fund Centre", value: (row: any) => row.fundCentreName },
      { header: "Fund Centre Level", value: (row: any) => row.fundCentreLevel },
      { header: "Cost Centre Code", value: (row: any) => row.costCentreCode },
      { header: "Cost Centre", value: (row: any) => row.costCentreName },
      { header: "GL Account Code", value: (row: any) => row.accountCode },
      { header: "GL Account", value: (row: any) => row.accountName },
      { header: "Variance Percent", value: (row: any) => row.variancePercent },
      { header: "Original-to-Final Explanation", value: (row: any) => row.originalToFinalExplanation },
      { header: "Variance Explanation", value: (row: any) => budgetComparison.varianceExplanations?.find((x: any) => x.budgetVersionLineId === row.budgetVersionLineId)?.explanation ?? "" },
      { header: "Evidence Reference", value: (row: any) => budgetComparison.varianceExplanations?.find((x: any) => x.budgetVersionLineId === row.budgetVersionLineId)?.evidenceReference ?? "" },
    ]);
  }

  async function submitBudgetVersion() {
    if (!budgetDraftId) return;
    setError(null);
    try {
      await api.post(`/public-sector/budget-versions/${budgetDraftId}/submit-for-approval`);
      setMsg("Budget version submitted for independent approval.");
      setBudgetDraftId(null);
      loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  function updateBudgetLine(index: number, field: string, value: string | number) {
    setBudgetLines((current) => current.map((line, i) => i === index ? { ...line, [field]: value } : line));
  }

  async function loadBudgetComparison() {
    setBudgetComparisonLoading(true);
    setError(null);
    try {
      const [response, reconciliations] = await Promise.all([
        api.get(`/public-sector/budget-versions/comparison?year=${year}`),
        api.get(`/public-sector/budget-reconciliations?year=${year}`),
      ]);
      setBudgetComparison(response.data);
      setBudgetReconciliations(reconciliations.data);
    } catch (err) {
      setBudgetComparison(null);
      setError(extractErrorMessage(err));
      try {
        const reconciliations = await api.get(`/public-sector/budget-reconciliations?year=${year}`);
        setBudgetReconciliations(reconciliations.data);
      } catch { setBudgetReconciliations([]); }
    } finally {
      setBudgetComparisonLoading(false);
    }
  }

  async function createBudgetReconciliation(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const cashBasis = budgetComparison?.accountingBasis === "Cash";
      const payload = {
        financialYear: year,
        ...budgetReconciliationDraft,
        revenueAmount: cashBasis ? 0 : Number(budgetReconciliationDraft.revenueAmount),
        expenseAmount: cashBasis ? 0 : Number(budgetReconciliationDraft.expenseAmount),
        operatingAmount: Number(budgetReconciliationDraft.operatingAmount),
        investingAmount: Number(budgetReconciliationDraft.investingAmount),
        financingAmount: Number(budgetReconciliationDraft.financingAmount),
      };
      await api.post("/public-sector/budget-reconciliations", payload);
      setBudgetReconciliationDraft({
        type: "Basis", description: "", statementSection: cashBasis ? "Operating" : "Revenue",
        revenueAmount: 0, expenseAmount: 0, operatingAmount: 0,
        investingAmount: 0, financingAmount: 0, supportingReference: "",
      });
      setMsg("Reconciliation adjustment created. Submit it for independent workflow approval.");
      await loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function budgetReconciliationAction(id: string, action: "submit" | "finalise") {
    setError(null);
    try {
      if (action === "submit")
        await api.post(`/public-sector/budget-reconciliations/${id}/submit-for-approval`);
      else
        await api.post(`/public-sector/budget-reconciliations/${id}/finalise-approval`);
      setMsg(action === "submit"
        ? "Reconciliation submitted to the approval workflow. An independent approver must approve it before finalisation."
        : "Approved reconciliation finalised.");
      await loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function loadCashActuals() {
    try {
      const [versions, actuals, accounts, receipts, payments, attestations] = await Promise.all([
        api.get(`/public-sector/budget-versions/approved?year=${year}`),
        api.get(`/public-sector/budget-execution-actuals?year=${year}`),
        api.get("/cashbank/accounts"),
        api.get(`/ar/receipts?year=${year}`),
        api.get(`/ap/payments?year=${year}`),
        api.get(`/public-sector/cash-period-attestations?year=${year}`),
      ]);
      const cashVersions = versions.data.filter((v: any) => v.accountingBasis === "Cash");
      setApprovedBudgetVersions(cashVersions);
      setBudgetExecutionActuals(actuals.data);
      setCashPeriodAttestations(attestations.data);
      if (cashVersions.length) {
        const budgetVersionId = cashVersions[0].id;
        const [sources, submissions, register] = await Promise.all([
          api.get(`/public-sector/expected-cash-sources?budgetVersionId=${budgetVersionId}`),
          api.get(`/public-sector/cash-source-submissions?budgetVersionId=${budgetVersionId}`),
          api.get(`/public-sector/expected-cash-sources/completeness-attestation?budgetVersionId=${budgetVersionId}`),
        ]);
        setExpectedCashSources(sources.data);
        setCashSourceSubmissions(submissions.data);
        setRegisterAttestation(register.data);
      } else {
        setExpectedCashSources([]);
        setCashSourceSubmissions([]);
        setRegisterAttestation(null);
      }
      const reconciliation = await api.get(`/public-sector/budget-execution-actuals/reconciliation?year=${year}`);
      setCashActualReconciliation(reconciliation.data);
      const accountTransactions = await Promise.all(accounts.data.map((account: any) =>
        api.get(`/cashbank/accounts/${account.id}/transactions`).then((r) => r.data)
      ));
      setCashBankTransactions(accountTransactions.flat().filter((t: any) =>
        t.isReconciled && t.journalEntryId && (Number(t.debit) > 0 || Number(t.credit) > 0)
      ));
      setCashArReceipts(receipts.data.filter((r: any) => r.journalEntryId && Number(r.amount) > 0));
      setCashApPayments(payments.data.filter((p: any) =>
        p.journalEntryId && p.preparedBy && p.authorisedBy && p.preparedBy !== p.authorisedBy && p.authorisedAt && Number(p.amount) > 0
      ));
    } catch (err) {
      setError(extractErrorMessage(err));
    }
  }

  async function createZeroActivityAttestation() {
    const budgetVersionId = approvedBudgetVersions[0]?.id;
    if (!budgetVersionId || !zeroActivityEvidence.trim()) {
      setError("Select an approved cash budget and enter the nil-return evidence reference.");
      return;
    }
    setError(null);
    try {
      await api.post("/public-sector/cash-period-attestations", { budgetVersionId, evidenceReference: zeroActivityEvidence.trim() });
      setZeroActivityEvidence("");
      setMsg("Zero-activity declaration created as a draft. Submit it for independent certification.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function submitZeroActivityAttestation(id: string) {
    try {
      await api.post(`/public-sector/cash-period-attestations/${id}/submit`);
      setMsg("Zero-activity declaration submitted for independent certification.");
      await loadCashActuals();
      await loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function certifyZeroActivityAttestation(id: string) {
    try {
      await api.post(`/public-sector/cash-period-attestations/${id}/certify`);
      setMsg("Zero-activity declaration independently certified.");
      await loadCashActuals();
      await loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createCashActual(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await api.post("/public-sector/budget-execution-actuals", {
        ...cashActualDraft,
        sourceKind: cashActualDraft.sourceKind,
        effectiveDate: `${cashActualDraft.effectiveDate}T00:00:00.000Z`,
      });
      setMsg("Cash allocation captured as a draft. For split AR/AP sources, capture every matching budget-line allocation so the total equals the source amount before submitting allocations for independent certification.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function submitCashActual(id: string) {
    try {
      await api.post(`/public-sector/budget-execution-actuals/${id}/submit`);
      setMsg("Cash actual submitted for independent certification.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function certifyCashActual(id: string) {
    try {
      await api.post(`/public-sector/budget-execution-actuals/${id}/certify`);
      setMsg("Cash actual certified.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }



  async function createVarianceExplanation(e: React.FormEvent) {
    e.preventDefault();
    if (!budgetComparison?.finalBudgetVersionId || !varianceDraft.budgetVersionLineId) return;
    try {
      await api.post("/public-sector/budget-variance-explanations", {
        budgetVersionId: budgetComparison.finalBudgetVersionId,
        budgetVersionLineId: varianceDraft.budgetVersionLineId,
        explanation: varianceDraft.explanation,
        evidenceReference: varianceDraft.evidenceReference,
        commentReference: varianceDraft.commentReference,
      });
      setVarianceDraft({ budgetVersionLineId: "", explanation: "", evidenceReference: "", commentReference: "" });
      setMsg("Variance explanation created. Submit it for independent review.");
      await loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function varianceExplanationAction(id: string, action: "submit" | "review" | "reject") {
    try {
      if (action === "submit") await api.post(`/public-sector/budget-variance-explanations/${id}/submit`);
      else await api.post(`/public-sector/budget-variance-explanations/${id}/review`, {
        accepted: action === "review",
        reviewNote: action === "review"
          ? "Variance explanation and supporting evidence independently reviewed."
          : "Rejected: explanation or supporting evidence requires correction before acceptance.",
      });
      setMsg(action === "submit" ? "Variance explanation submitted for review."
        : action === "review" ? "Variance explanation approved."
        : "Variance explanation rejected; the preparer may submit a corrected explanation.");
      await loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createExpectedCashSource(e: React.FormEvent) {
    e.preventDefault();
    const version = approvedBudgetVersions[0];
    if (!version) return setError("An approved cash-basis budget is required.");
    try {
      await api.post("/public-sector/expected-cash-sources", {
        budgetVersionId: version.id, ...expectedSourceDraft,
        effectiveFrom: version.periodStart, effectiveTo: version.periodEnd,
      });
      setExpectedSourceDraft({ ...expectedSourceDraft, sourceCode: "", sourceName: "", sourceIdentifier: "" });
      setMsg("Expected source created. Submit it for independent approval.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function expectedSourceAction(id: string, action: "submit" | "approve") {
    try {
      await api.post(`/public-sector/expected-cash-sources/${id}/${action}`);
      setMsg(action === "submit" ? "Expected source submitted for independent approval." : "Expected source approved.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createSourceSubmission(e: React.FormEvent) {
    e.preventDefault();
    try {
      const priorSubmission = cashSourceSubmissions
        .filter((item: any) => item.expectedCashSourceId === sourceSubmissionDraft.expectedCashSourceId)
        .sort((a: any, b: any) => (b.submissionRevision ?? 1) - (a.submissionRevision ?? 1))[0];
      await api.post("/public-sector/cash-source-submissions", {
        ...sourceSubmissionDraft,
        recordCount: Number(sourceSubmissionDraft.recordCount),
        controlTotal: Number(sourceSubmissionDraft.controlTotal),
        openingBalance: Number(sourceSubmissionDraft.openingBalance),
        closingBalance: Number(sourceSubmissionDraft.closingBalance),
        revisionReason: priorSubmission ? sourceSubmissionDraft.revisionReason : null,
      });
      setMsg(priorSubmission
        ? "Replacement source submission created as a new revision. The previous reviewed record remains immutable; submit the new revision for independent review."
        : "Source control submission created. Submit it for independent review.");
      setSourceSubmissionDraft({ ...sourceSubmissionDraft, expectedCashSourceId: "", recordCount: 0, controlTotal: 0, evidenceReference: "", revisionReason: "" });
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function sourceSubmissionAction(id: string, action: "submit" | "review") {
    try {
      if (action === "submit") await api.post(`/public-sector/cash-source-submissions/${id}/submit`);
      else await api.post(`/public-sector/cash-source-submissions/${id}/review`, { accepted: true, reviewNote: "Source control totals and evidence reviewed." });
      setMsg(action === "submit" ? "Source control submission sent for review." : "Source control submission reviewed.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  const selectedSourceKind = expectedCashSources.find((x: any) =>
    x.id === cashSourceSubmissions.find((s: any) => s.id === recordLineDraft.submissionId)?.expectedCashSourceId
  )?.sourceKind;
  const sourceRecordsForReconciliation = selectedSourceKind === "BankAccount" ? cashBankTransactions
    : selectedSourceKind === "ReceivablesControl" ? cashArReceipts
    : selectedSourceKind === "PayablesControl" ? cashApPayments : [];

  async function loadReconciliationLines(submissionId: string) {
    if (!submissionId) { setReconciliationLines([]); return; }
    try {
      const response = await api.get(`/public-sector/cash-source-submissions/${submissionId}/reconciliation-lines`);
      setReconciliationLines(response.data);
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function addSourceReconciliationLine(e: React.FormEvent) {
    e.preventDefault();
    if (!recordLineDraft.submissionId) return setError("Select a reviewed source submission.");
    try {
      await api.post(`/public-sector/cash-source-submissions/${recordLineDraft.submissionId}/reconciliation-lines`, {
        sourceRecordId: recordLineDraft.sourceRecordId,
        sourceReference: recordLineDraft.sourceReference,
        budgetExecutionActualId: recordLineDraft.isExcluded ? null : recordLineDraft.budgetExecutionActualId,
        isExcluded: recordLineDraft.isExcluded,
        exclusionReason: recordLineDraft.isExcluded ? recordLineDraft.exclusionReason : null,
      });
      setMsg("Source transaction mapping recorded.");
      setRecordLineDraft({ ...recordLineDraft, sourceRecordId: "", sourceReference: "", budgetExecutionActualId: "", exclusionReason: "" });
      await loadReconciliationLines(recordLineDraft.submissionId);
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function sourceRecordReconciliationAction(id: string, action: "submit" | "review") {
    try {
      if (action === "submit") await api.post(`/public-sector/cash-source-submissions/${id}/record-reconciliation/submit`);
      else await api.post(`/public-sector/cash-source-submissions/${id}/record-reconciliation/review`, {
        accepted: true, reviewNote: "Transaction-level population reconciled to source evidence and certified actual allocations.",
      });
      setMsg(action === "submit" ? "Transaction-level reconciliation submitted for independent review." : "Transaction-level reconciliation independently reviewed.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function registerAttestationAction(action: "create" | "submit" | "review") {
    const version = approvedBudgetVersions[0];
    if (!version) return setError("An approved cash budget is required.");
    try {
      if (action === "create") {
        await api.post("/public-sector/expected-cash-sources/completeness-attestation", {
          budgetVersionId: version.id, expectedSourceCount: expectedCashSources.length,
          entityScope: version.entityScope, evidenceReference: registerAttestationEvidence.trim(),
        });
        setRegisterAttestationEvidence("");
      } else if (action === "submit") {
        await api.post(`/public-sector/expected-cash-sources/completeness-attestation/${registerAttestation.id}/submit`);
      } else {
        await api.post(`/public-sector/expected-cash-sources/completeness-attestation/${registerAttestation.id}/review`, {
          accepted: true, reviewNote: "Expected-source population completeness independently verified against the supporting inventory.",
        });
      }
      setMsg(action === "create" ? "Source-register completeness attestation created." : action === "submit" ? "Source-register completeness submitted for certification." : "Source-register completeness independently certified.");
      await loadCashActuals();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createTender(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/public-sector/tenders", newTender);
      setMsg("Tender notice created.");
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  const requiresZfrmClassification = budgetDraft.classificationBasis.toLowerCase().includes("zfrm") ||
    budgetDraft.classificationBasis.toLowerCase().includes("zimbabwe financial reporting manual");
  const selectableBudgetAccounts = budgetAccounts.filter((a: any) =>
    (a.type === "Income" || a.type === "Expense" ||
      (budgetDraft.accountingBasis === "Cash" &&
        ["Operating", "Investing", "Financing"].includes(a.cashFlowCategory))) &&
    (!requiresZfrmClassification || a.type === "Income" || a.type === "Expense")
  );
  const fmt = (n: number) => (n ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Public Sector & Government</h1>
      <p className="text-slate-500 mb-6">Vote/Program/Sub-Program fund hierarchy, commitment accounting, e-tendering, and asset verification.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}
      {msg && <div className="text-sm text-brand-700 bg-brand-50 border border-brand-200 rounded-lg px-3 py-2 mb-4">{msg}</div>}

      <div className="flex gap-2 mb-6">
        <button className={tab === "funds" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("funds")}>Fund Centres & Budget</button>
        <button className={tab === "tenders" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("tenders")}>E-Tendering</button>
        <button className={tab === "assets" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("assets")}>Asset Verification</button>
        <button className={tab === "ipsas24" ? "btn-primary" : "btn-secondary"} onClick={() => { setTab("ipsas24"); loadBudgetComparison(); loadCashActuals(); }}>IPSAS 24 Budget Statement</button>
      </div>

      {tab === "funds" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <h2 className="font-semibold mb-3">Budget Utilization ({year})</h2>
            <table className="table-base">
              <thead><tr><th>Code</th><th>Name</th><th>Appropriation</th><th>Commitments</th><th>Actuals</th><th>Available</th><th>% Used</th></tr></thead>
              <tbody>
                {utilization.map((u: any) => (
                  <tr key={u.fundCentreId} className={u.overNinetyPercent ? "bg-red-50" : ""}>
                    <td className="font-mono">{u.code}</td><td>{u.name}</td>
                    <td className="text-right">{fmt(u.appropriation)}</td><td className="text-right">{fmt(u.commitments)}</td>
                    <td className="text-right">{fmt(u.actuals)}</td><td className="text-right">{fmt(u.available)}</td>
                    <td className={`text-right font-medium ${u.overNinetyPercent ? "text-red-600" : ""}`}>{u.percentUtilised}%</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <form onSubmit={createFund} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Fund Centre</h2>
            <div><label className="label">Code</label><input className="input" required value={newFund.code} onChange={(e) => setNewFund({ ...newFund, code: e.target.value })} /></div>
            <div><label className="label">Name</label><input className="input" required value={newFund.name} onChange={(e) => setNewFund({ ...newFund, name: e.target.value })} /></div>
            <div>
              <label className="label">Level</label>
              <select className="input" value={newFund.level} onChange={(e) => setNewFund({ ...newFund, level: e.target.value })}>
                <option value="Vote">Vote</option><option value="Program">Program</option><option value="SubProgram">Sub-Program</option>
              </select>
            </div>
            <div><label className="label">Appropriation Amount</label><input className="input" type="number" value={newFund.appropriationAmount} onChange={(e) => setNewFund({ ...newFund, appropriationAmount: Number(e.target.value) })} /></div>
            <button className="btn-primary w-full">Create Fund Centre</button>
          </form>
        </div>
      )}

      {tab === "tenders" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Tender #</th><th>Title</th><th>Deadline</th><th>Status</th><th>Bids</th></tr></thead>
              <tbody>{tenders.map((t: any) => (<tr key={t.id}><td className="font-mono">{t.tenderNo}</td><td>{t.title}</td><td>{new Date(t.submissionDeadline).toLocaleString()}</td><td>{t.status}</td><td>{t.bids?.length ?? 0}</td></tr>))}</tbody>
            </table>
          </div>
          <form onSubmit={createTender} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Tender Notice</h2>
            <div><label className="label">Title</label><input className="input" required value={newTender.title} onChange={(e) => setNewTender({ ...newTender, title: e.target.value })} /></div>
            <div><label className="label">Specifications</label><textarea className="input" value={newTender.specifications} onChange={(e) => setNewTender({ ...newTender, specifications: e.target.value })} /></div>
            <div><label className="label">Submission Deadline</label><input className="input" type="datetime-local" required value={newTender.submissionDeadline} onChange={(e) => setNewTender({ ...newTender, submissionDeadline: e.target.value })} /></div>
            <button className="btn-primary w-full">Publish Tender</button>
            <p className="text-xs text-slate-400">Bids submitted after the deadline are rejected automatically by the server clock.</p>
          </form>
        </div>
      )}

      {tab === "ipsas24" && (
        <section className="card space-y-4">
          <div className="flex items-center justify-between gap-3 flex-wrap">
            <div>
              <h2 className="font-semibold text-lg">Statement of Comparison of Budget and Actual Amounts</h2>
              <p className="text-xs text-slate-500">IPSAS 24 · {budgetComparison ? `Period ${new Date(budgetComparison.periodStart).toLocaleDateString()} to ${new Date(new Date(budgetComparison.periodEnd).getTime() - 86400000).toLocaleDateString()} · ${budgetComparison.currency}` : `Financial year ${year}`}</p>
            </div>
            <button className="btn-secondary" onClick={loadBudgetComparison} disabled={budgetComparisonLoading}>
              {budgetComparisonLoading ? "Loading…" : "Refresh statement"}
            </button>
            {budgetComparison && <button className="btn-secondary" onClick={exportBudgetComparison}
              disabled={!budgetComparison.comparableActualsAvailable || !budgetComparison.varianceExplanationsComplete || !budgetComparison.originalToFinalExplanationsComplete || (budgetComparison.accountingBasis === "Cash" && !cashActualReconciliation?.reconciled)}
              title={!budgetComparison.comparableActualsAvailable ? "Comparable actuals are not available." : !budgetComparison.originalToFinalExplanationsComplete ? "Classify and explain changes from the original to final budget before export." : !budgetComparison.varianceExplanationsComplete ? "Approve all non-zero variance explanations before export." : budgetComparison.accountingBasis === "Cash" && !cashActualReconciliation?.reconciled ? "Complete the independently reviewed Statement of Cash Flows bridge before export." : "Export the reconciled IPSAS 24 comparison."}>Export CSV</button>}
          </div>
          {budgetComparisonLoading && <p className="text-sm text-slate-500">Loading approved budget versions and ledger actuals…</p>}
          <form onSubmit={createBudgetVersion} className="rounded-lg border p-4 space-y-3">
            <h3 className="font-semibold">Create an original or revised budget version</h3>
            <p className="text-xs text-slate-500">The draft is not included in the financial statement until an independent approver finalises its approval workflow.</p>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
              <div><label className="label">Version type</label><select className="input" value={budgetDraft.kind} onChange={(e) => setBudgetDraft({ ...budgetDraft, kind: e.target.value, amendmentNature: "" })}><option value="Original">Original</option><option value="Revised">Revised</option></select></div>
              <div><label className="label">Currency</label><input className="input" maxLength={3} required value={budgetDraft.currency} onChange={(e) => setBudgetDraft({ ...budgetDraft, currency: e.target.value.toUpperCase() })} /></div>
              <div><label className="label">Budget accounting basis</label><select className="input" value={budgetDraft.accountingBasis} onChange={(e) => setBudgetDraft({ ...budgetDraft, accountingBasis: e.target.value })}><option value="Accrual">Accrual</option><option value="Cash">Cash</option></select></div>
              <div><label className="label">Classification basis</label><input className="input" required value={budgetDraft.classificationBasis} onChange={(e) => setBudgetDraft({ ...budgetDraft, classificationBasis: e.target.value })} placeholder="ZFRM Appendix 5 / approved jurisdictional classification" /><p className="text-xs text-slate-500">When the basis contains “ZFRM”, each line must also carry a Treasury Appendix 5 category. The GL SubType remains separately validated against the account.</p></div>
              <div><label className="label">Entity scope</label><input className="input" required value={budgetDraft.entityScope} onChange={(e) => setBudgetDraft({ ...budgetDraft, entityScope: e.target.value })} placeholder="Reporting entity / MDA" /></div>
              <p className="md:col-span-3 text-xs text-slate-500">For automatic accrual-basis actuals, entity scope must match the registered company name and currency must match its functional currency. Otherwise actuals are withheld pending reconciliation.</p>
              <div><label className="label">Authority reference</label><input className="input" required value={budgetDraft.authorityReference} onChange={(e) => setBudgetDraft({ ...budgetDraft, authorityReference: e.target.value })} placeholder="Appropriation Act / authority" /></div>
              <div><label className="label">Period start</label><input className="input" type="date" required value={budgetDraft.periodStart.slice(0, 10)} onChange={(e) => setBudgetDraft({ ...budgetDraft, periodStart: `${e.target.value}T00:00:00.000Z` })} /></div>
              <div><label className="label">Period end (exclusive)</label><input className="input" type="date" required value={budgetDraft.periodEnd.slice(0, 10)} onChange={(e) => setBudgetDraft({ ...budgetDraft, periodEnd: `${e.target.value}T00:00:00.000Z` })} /></div>
              {budgetDraft.kind === "Revised" && <>
                <div><label className="label">Nature of authorised change</label><select className="input" required value={budgetDraft.amendmentNature} onChange={(e) => setBudgetDraft({ ...budgetDraft, amendmentNature: e.target.value })}><option value="">Select amendment nature</option><option value="ReallocationWithinBudget">Reallocation within the approved budget</option><option value="OtherAuthorisedFactors">Other authorised factors</option><option value="Mixed">Both reallocations and other authorised factors</option></select></div>
                <div className="md:col-span-3"><label className="label">Reason for revision</label><textarea className="input" required value={budgetDraft.amendmentReason} onChange={(e) => setBudgetDraft({ ...budgetDraft, amendmentReason: e.target.value })} /></div>
              </>}
            </div>
            <div className="space-y-2">
              <div className="flex items-center justify-between"><h4 className="font-medium text-sm">Budget lines</h4><button type="button" className="btn-secondary" onClick={() => setBudgetLines((current) => [...current, { fundCentreId: "", glAccountId: "", costCentreId: null, description: "", classificationCode: "", treasuryClassificationCode: "", noteReference: "", amount: 0 }])}>Add line</button></div>
              {budgetLines.map((line, index) => (
                <div key={index} className="grid grid-cols-1 md:grid-cols-9 gap-2 border-t pt-2">
                  <select className="input" required value={line.fundCentreId} onChange={(e) => updateBudgetLine(index, "fundCentreId", e.target.value)}><option value="">Fund centre</option>{fundCentres.map((f: any) => <option key={f.id} value={f.id}>{f.code} — {f.name}</option>)}</select>
                  <select className="input" required value={line.glAccountId} onChange={(e) => {
                    const account = selectableBudgetAccounts.find((a: any) => a.id === e.target.value);
                    updateBudgetLine(index, "glAccountId", e.target.value);
                    updateBudgetLine(index, "classificationCode", account?.subType ?? "");
                  }}><option value="">Select GL account</option>{selectableBudgetAccounts.map((a: any) => <option key={a.id} value={a.id}>{a.code} — {a.name} ({a.type})</option>)}</select>
                  <input className="input" required placeholder="Description" value={line.description} onChange={(e) => updateBudgetLine(index, "description", e.target.value)} />
                  <input className="input" readOnly placeholder="GL SubType (from account)" value={line.classificationCode} />
                  <input className="input" placeholder="ZFRM Appendix 5 category / Treasury code" required={budgetVersionDraft.classificationBasis.toLowerCase().includes("zfrm") || budgetVersionDraft.classificationBasis.toLowerCase().includes("zimbabwe financial reporting manual")} value={line.treasuryClassificationCode} onChange={(e) => updateBudgetLine(index, "treasuryClassificationCode", e.target.value)} />
                  <input className="input" placeholder="Notes reference (Appendix 5)" value={line.noteReference} onChange={(e) => updateBudgetLine(index, "noteReference", e.target.value)} />
                  <input className="input" type="number" min="0" step="0.01" required placeholder="Amount" value={line.amount} onChange={(e) => updateBudgetLine(index, "amount", Number(e.target.value))} />
                  <button type="button" className="btn-secondary" disabled={budgetLines.length === 1} onClick={() => setBudgetLines((current) => current.filter((_, i) => i !== index))}>Remove</button>
                </div>
              ))}
            </div>
            <div className="flex gap-2 flex-wrap">
              <button className="btn-primary" disabled={selectableBudgetAccounts.length === 0 || fundCentres.length === 0}>Save budget draft</button>
              {budgetDraftId && <button type="button" className="btn-secondary" onClick={submitBudgetVersion}>Submit draft for approval</button>}
            </div>
            {selectableBudgetAccounts.length === 0 && <p className="text-xs text-amber-700">Configure active income/expense accounts, or for cash budgets balance-sheet accounts classified to operating, investing or financing cash flows.</p>}
          </form>
          <div className="border rounded-lg p-4 space-y-3">
            <div>
              <h3 className="font-semibold">Cash-basis budget execution actuals</h3>
              <p className="text-xs text-slate-500">Supported sources: reconciled bank movements, AR receipts and independently authorised AP payments. AR/AP sources can be allocated across multiple tax-free invoices and invoice GL lines by selecting the same source against each matching budget line. Partial invoice settlements are supported when all invoice lines share one GL classification; partial settlement across multiple GL classifications, VAT, withholding-tax and cost-centre allocations remain blocked pending explicit line-level mapping. Capture every allocation so the amounts reconcile exactly to the receipt/payment before submitting any allocation for independent certification. Modified-cash commitments are not enabled.</p>
            </div>
            <form onSubmit={createCashActual} className="grid grid-cols-1 md:grid-cols-3 gap-3">
              <div>
                <label className="label">Approved cash budget</label>
                <select className="input" required value={cashActualDraft.budgetVersionId} onChange={(e) => {
                  const selected = approvedBudgetVersions.find((v: any) => v.id === e.target.value);
                  setCashActualDraft({ ...cashActualDraft, budgetVersionId: e.target.value, budgetVersionLineId: selected?.lines?.[0]?.id ?? "" });
                }}>
                  <option value="">Select approved cash budget</option>
                  {approvedBudgetVersions.map((v: any) => <option key={v.id} value={v.id}>{v.kind} v{v.versionNumber} · {v.currency} · {v.authorityReference}</option>)}
                </select>
              </div>
              <div>
                <label className="label">Budget line</label>
                <select className="input" required value={cashActualDraft.budgetVersionLineId} onChange={(e) => setCashActualDraft({ ...cashActualDraft, budgetVersionLineId: e.target.value })}>
                  <option value="">Select budget line</option>
                  {(approvedBudgetVersions.find((v: any) => v.id === cashActualDraft.budgetVersionId)?.lines ?? []).map((line: any) => <option key={line.id} value={line.id}>{line.description} · {line.classificationCode}</option>)}
                </select>
              </div>
              <div>
                <label className="label">Source record type</label>
                <select className="input" value={cashActualDraft.sourceKind} onChange={(e) => setCashActualDraft({
                  ...cashActualDraft, sourceKind: e.target.value, sourceRecordId: "",
                  eventKind: e.target.value === "ArReceipt" ? "CashReceipt" : "CashPayment",
                })}>
                  <option value="BankTransaction">Reconciled bank movement</option>
                  <option value="ArReceipt">AR receipt</option>
                  <option value="ApPayment">Authorised AP payment</option>
                </select>
              </div>
              <div>
                <label className="label">{cashActualDraft.sourceKind === "BankTransaction" ? "Reconciled bank transaction" : cashActualDraft.sourceKind === "ArReceipt" ? "Posted AR receipt" : "Independently authorised AP payment"}</label>
                <select className="input" required value={cashActualDraft.sourceRecordId} onChange={(e) => setCashActualDraft({ ...cashActualDraft, sourceRecordId: e.target.value })}>
                  <option value="">Select source record</option>
                  {cashActualDraft.sourceKind === "BankTransaction" && cashBankTransactions.map((t: any) => <option key={t.id} value={t.id}>{t.reference || t.id} · {new Date(t.transactionDate).toLocaleDateString()} · debit {fmt(t.debit)} / credit {fmt(t.credit)}</option>)}
                  {cashActualDraft.sourceKind === "ArReceipt" && cashArReceipts.map((r: any) => <option key={r.id} value={r.id}>{r.receiptNo} · {new Date(r.receiptDate).toLocaleDateString()} · {fmt(r.amount)} {r.currency}</option>)}
                  {cashActualDraft.sourceKind === "ApPayment" && cashApPayments.map((p: any) => <option key={p.id} value={p.id}>{p.paymentNo} · {new Date(p.paymentDate).toLocaleDateString()} · {fmt(p.amount)} {p.currency}</option>)}
                </select>
              </div>
              <div>
                <label className="label">Cash event</label>
                <select className="input" value={cashActualDraft.eventKind} onChange={(e) => setCashActualDraft({ ...cashActualDraft, eventKind: e.target.value })}>
                  <option value="CashPayment">Cash payment</option><option value="CashReceipt">Cash receipt</option>
                </select>
              </div>
              <div>
                <label className="label">Statement of Cash Flows section</label>
                <select className="input" value={cashActualDraft.cashFlowSection} onChange={(e) => setCashActualDraft({ ...cashActualDraft, cashFlowSection: e.target.value })}>
                  <option value="Operating">Operating</option><option value="Investing">Investing</option><option value="Financing">Financing</option>
                </select>
              </div>
              <div>
                <label className="label">Budget effective date</label>
                <input className="input" type="date" required value={cashActualDraft.effectiveDate} onChange={(e) => setCashActualDraft({ ...cashActualDraft, effectiveDate: e.target.value })} />
              </div>
              <div className="md:col-span-3"><button className="btn-primary" disabled={!approvedBudgetVersions.length || !(cashActualDraft.sourceKind === "BankTransaction" ? cashBankTransactions.length : cashActualDraft.sourceKind === "ArReceipt" ? cashArReceipts.length : cashApPayments.length)}>Capture cash actual</button></div>
            </form>

            <div className="border rounded-lg p-4 space-y-4">
              <div>
                <h4 className="font-semibold">Expected source register and transaction reconciliation</h4>
                <p className="text-xs text-slate-500">Register every bank account, receivables-control and payables-control population for this budget period. Each source must have reviewed period controls, record-by-record allocation or a reasoned exclusion, and independent register-completeness certification. Cashbook imports require a separate adapter and are not enabled here.</p>
              </div>
              <form onSubmit={createExpectedCashSource} className="grid grid-cols-1 md:grid-cols-4 gap-2">
                <select className="input" value={expectedSourceDraft.sourceKind} onChange={(e) => setExpectedSourceDraft({ ...expectedSourceDraft, sourceKind: e.target.value })}>
                  <option value="BankAccount">Bank account</option><option value="ReceivablesControl">Receivables control</option><option value="PayablesControl">Payables control</option>
                </select>
                <input className="input" required placeholder="Source code" value={expectedSourceDraft.sourceCode} onChange={(e) => setExpectedSourceDraft({ ...expectedSourceDraft, sourceCode: e.target.value })} />
                <input className="input" required placeholder="Source name" value={expectedSourceDraft.sourceName} onChange={(e) => setExpectedSourceDraft({ ...expectedSourceDraft, sourceName: e.target.value })} />
                <input className="input" required placeholder={expectedSourceDraft.sourceKind === "BankAccount" ? "Bank account ID" : "AR/AP control GL account ID"} value={expectedSourceDraft.sourceIdentifier} onChange={(e) => setExpectedSourceDraft({ ...expectedSourceDraft, sourceIdentifier: e.target.value })} />
                <button className="btn-secondary md:col-span-4" disabled={!approvedBudgetVersions.length}>Add expected source</button>
              </form>
              <div className="overflow-x-auto">
                <table className="table-base">
                  <thead><tr><th>Source</th><th>Type</th><th>Required</th><th>Status</th><th>Action</th></tr></thead>
                  <tbody>{expectedCashSources.map((source: any) => (
                    <tr key={source.id}>
                      <td>{source.sourceCode} — {source.sourceName}</td><td>{source.sourceKind}</td><td>{source.isRequired ? "Yes" : "No"}</td><td>{source.status}</td>
                      <td>{source.status === "Draft" ? <button type="button" className="btn-secondary" onClick={() => expectedSourceAction(source.id, "submit")}>Submit</button> : source.status === "PendingApproval" ? <button type="button" className="btn-secondary" onClick={() => expectedSourceAction(source.id, "approve")}>Approve</button> : "—"}</td>
                    </tr>
                  ))}</tbody>
                </table>
              </div>
              <form onSubmit={createSourceSubmission} className="grid grid-cols-1 md:grid-cols-4 gap-2 border-t pt-3">
                <select className="input md:col-span-2" required value={sourceSubmissionDraft.expectedCashSourceId} onChange={(e) => {
                  const version = approvedBudgetVersions[0];
                  setSourceSubmissionDraft({ ...sourceSubmissionDraft, expectedCashSourceId: e.target.value, currency: version?.currency ?? "USD", revisionReason: "" });
                }}>
                  <option value="">Select approved expected source</option>
                  {expectedCashSources.filter((x: any) => x.status === "Approved").map((source: any) => <option key={source.id} value={source.id}>{source.sourceCode} — {source.sourceName}</option>)}
                </select>
                <input className="input" type="number" min="0" step="1" required placeholder="Record count" value={sourceSubmissionDraft.recordCount} onChange={(e) => setSourceSubmissionDraft({ ...sourceSubmissionDraft, recordCount: Number(e.target.value) })} />
                <input className="input" type="number" min="0" step="0.01" required placeholder="Control total" value={sourceSubmissionDraft.controlTotal} onChange={(e) => setSourceSubmissionDraft({ ...sourceSubmissionDraft, controlTotal: Number(e.target.value) })} />
                <input className="input" type="number" step="0.01" required placeholder="Opening balance" value={sourceSubmissionDraft.openingBalance} onChange={(e) => setSourceSubmissionDraft({ ...sourceSubmissionDraft, openingBalance: Number(e.target.value) })} />
                <input className="input" type="number" step="0.01" required placeholder="Closing balance" value={sourceSubmissionDraft.closingBalance} onChange={(e) => setSourceSubmissionDraft({ ...sourceSubmissionDraft, closingBalance: Number(e.target.value) })} />
                <input className="input" required maxLength={3} placeholder="Currency" value={sourceSubmissionDraft.currency} onChange={(e) => setSourceSubmissionDraft({ ...sourceSubmissionDraft, currency: e.target.value.toUpperCase() })} />
                <input className="input" required placeholder="Evidence reference" value={sourceSubmissionDraft.evidenceReference} onChange={(e) => setSourceSubmissionDraft({ ...sourceSubmissionDraft, evidenceReference: e.target.value })} />
                {(() => {
                  const prior = cashSourceSubmissions
                    .filter((item: any) => item.expectedCashSourceId === sourceSubmissionDraft.expectedCashSourceId)
                    .sort((a: any, b: any) => (b.submissionRevision ?? 1) - (a.submissionRevision ?? 1))[0];
                  const canRevise = !prior || prior.status === "Rejected" || prior.recordLevelStatus === "Rejected" ||
                    (prior.status === "Reviewed" && prior.recordLevelStatus !== "PendingReview");
                  return prior ? <>
                    <input className="input md:col-span-3" required placeholder="Reason for replacing the prior source submission"
                      value={sourceSubmissionDraft.revisionReason}
                      onChange={(e) => setSourceSubmissionDraft({ ...sourceSubmissionDraft, revisionReason: e.target.value })} />
                    <p className="md:col-span-4 text-xs text-slate-500">Latest submission: revision {prior.submissionRevision ?? 1} ({prior.status} / {prior.recordLevelStatus}). A replacement revision preserves the prior record and requires a reason plus new independent control and record-level review.</p>
                    <button className="btn-secondary md:col-span-4" disabled={!canRevise || !sourceSubmissionDraft.revisionReason.trim()}>Create replacement source revision</button>
                  </> : <button className="btn-secondary md:col-span-4" disabled={!sourceSubmissionDraft.expectedCashSourceId}>Create source control submission</button>;
                })()}
              </form>
              <div className="overflow-x-auto">
                <table className="table-base">
                  <thead><tr><th>Source</th><th>Records / total</th><th>Control review</th><th>Record reconciliation</th><th>Action</th></tr></thead>
                  <tbody>{cashSourceSubmissions.map((submission: any) => {
                    const source = expectedCashSources.find((x: any) => x.id === submission.expectedCashSourceId);
                    return <tr key={submission.id}>
                      <td>{source?.sourceCode ?? submission.expectedCashSourceId} · Rev {submission.submissionRevision ?? 1}</td>
                      <td>{submission.recordCount} / {submission.currency} {fmt(submission.controlTotal)}</td>
                      <td>{submission.status}</td><td>{submission.recordLevelStatus}</td>
                      <td className="flex gap-1 flex-wrap">
                        {submission.status === "Draft" && <button type="button" className="btn-secondary" onClick={() => sourceSubmissionAction(submission.id, "submit")}>Submit</button>}
                        {submission.status === "PendingReview" && <button type="button" className="btn-secondary" onClick={() => sourceSubmissionAction(submission.id, "review")}>Review</button>}
                        {submission.status === "Reviewed" && submission.recordLevelStatus === "NotStarted" && <button type="button" className="btn-secondary" onClick={() => { setRecordLineDraft({ ...recordLineDraft, submissionId: submission.id, sourceRecordId: "", sourceReference: "" }); loadReconciliationLines(submission.id); }}>Reconcile records</button>}
                        {submission.recordLevelStatus === "PendingReview" && <button type="button" className="btn-secondary" onClick={() => sourceRecordReconciliationAction(submission.id, "review")}>Review records</button>}
                        {submission.status === "Reviewed" && submission.recordLevelStatus === "NotStarted" && <button type="button" className="btn-secondary" onClick={() => sourceRecordReconciliationAction(submission.id, "submit")}>Submit records</button>}
                      </td>
                    </tr>;
                  })}</tbody>
                </table>
              </div>
              {recordLineDraft.submissionId && (
                <div className="border-t pt-3 space-y-3">
                  <h5 className="font-medium">Transaction mapping — {cashSourceSubmissions.find((x: any) => x.id === recordLineDraft.submissionId)?.status}</h5>
                  <form onSubmit={addSourceReconciliationLine} className="grid grid-cols-1 md:grid-cols-4 gap-2">
                    <select className="input md:col-span-2" required value={recordLineDraft.sourceRecordId} onChange={(e) => {
                      const source = sourceRecordsForReconciliation.find((x: any) => x.id === e.target.value);
                      const fallbackReference = source?.id ? String(source.id).replace(/-/g, "") : "";
                      const reference = source?.reference || source?.receiptNo || source?.paymentNo || fallbackReference;
                      setRecordLineDraft({ ...recordLineDraft, sourceRecordId: e.target.value, sourceReference: reference });
                    }}>
                      <option value="">Select source transaction</option>
                      {sourceRecordsForReconciliation.map((source: any) => <option key={source.id} value={source.id}>{source.reference || source.receiptNo || source.paymentNo || source.id} · {fmt(source.amount ?? source.debit ?? source.credit ?? 0)}</option>)}
                    </select>
                    <input className="input" required placeholder="Source reference" value={recordLineDraft.sourceReference} onChange={(e) => setRecordLineDraft({ ...recordLineDraft, sourceReference: e.target.value })} />
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={recordLineDraft.isExcluded} onChange={(e) => setRecordLineDraft({ ...recordLineDraft, isExcluded: e.target.checked })} /> Exclude with reason</label>
                    {!recordLineDraft.isExcluded && <select className="input md:col-span-2" required value={recordLineDraft.budgetExecutionActualId} onChange={(e) => setRecordLineDraft({ ...recordLineDraft, budgetExecutionActualId: e.target.value })}>
                      <option value="">Select certified actual allocation</option>
                      {budgetExecutionActuals.filter((x: any) => x.status === "Certified" && x.sourceRecordId === recordLineDraft.sourceRecordId).map((actual: any) => <option key={actual.id} value={actual.id}>{actual.sourceReference} · {actual.currency} {fmt(actual.amount)} · {actual.cashFlowSection}</option>)}
                    </select>}
                    {recordLineDraft.isExcluded && <input className="input md:col-span-2" required placeholder="Reason for exclusion" value={recordLineDraft.exclusionReason} onChange={(e) => setRecordLineDraft({ ...recordLineDraft, exclusionReason: e.target.value })} />}
                    <button className="btn-secondary md:col-span-4" disabled={!recordLineDraft.sourceRecordId}>Add allocation / exclusion</button>
                  </form>
                  <div className="overflow-x-auto">
                    <table className="table-base"><thead><tr><th>Source record</th><th>Source amount</th><th>Actual allocation</th><th>Exclusion</th></tr></thead>
                      <tbody>{reconciliationLines.map((line: any) => <tr key={line.id}><td>{line.sourceReference}</td><td>{fmt(line.sourceAmount)}</td><td>{line.allocatedAmount ? fmt(line.allocatedAmount) : "—"}</td><td>{line.isExcluded ? line.exclusionReason : "—"}</td></tr>)}
                        {reconciliationLines.length === 0 && <tr><td colSpan={4} className="text-slate-500 text-center">No transaction mappings entered yet.</td></tr>}</tbody>
                    </table>
                  </div>
                </div>
              )}
              <div className="border-t pt-3 space-y-2">
                <h5 className="font-medium">Independent expected-source register completeness certification</h5>
                <p className="text-xs text-slate-500">The evidence reference must identify the signed management inventory or other authoritative population evidence used to establish that no bank account, receivables/payables control population or reporting source has been omitted.</p>
                {(!registerAttestation || registerAttestation.status === "Rejected") && <div className="flex gap-2 flex-wrap">
                  <input className="input flex-1" required placeholder="Signed source inventory evidence reference" value={registerAttestationEvidence} onChange={(e) => setRegisterAttestationEvidence(e.target.value)} />
                  <button type="button" className="btn-secondary" disabled={!expectedCashSources.length || !registerAttestationEvidence.trim()} onClick={() => registerAttestationAction("create")}>Create attestation</button>
                </div>}
                {registerAttestation && <div className="flex items-center gap-2 flex-wrap text-sm">
                  <span>Status: <strong>{registerAttestation.status}</strong></span>
                  <span>Sources declared: {registerAttestation.expectedSourceCount}</span>
                  <span>Evidence: {registerAttestation.evidenceReference}</span>
                  {registerAttestation.status === "Draft" && <button type="button" className="btn-secondary" onClick={() => registerAttestationAction("submit")}>Submit</button>}
                  {registerAttestation.status === "PendingCertification" && <button type="button" className="btn-secondary" onClick={() => registerAttestationAction("review")}>Certify</button>}
                </div>}
              </div>
            </div>
            <div className="border rounded-lg p-4 space-y-3 mb-4">
              <div>
                <h4 className="font-semibold">Period completeness — nil activity declaration</h4>
                <p className="text-xs text-slate-500">Use only when there were no bank transactions, AR receipts or AP payments in the approved budget period. ApexERP recounts all three source populations at creation, submission and certification. Any source activity blocks a nil return.</p>
              </div>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-2">
                <div className="md:col-span-2">
                  <label className="label">Supporting nil-return evidence reference</label>
                  <input className="input" value={zeroActivityEvidence} onChange={(e) => setZeroActivityEvidence(e.target.value)} placeholder="Cashbook/bank confirmation reference" />
                </div>
                <div className="flex items-end">
                  <button className="btn-secondary w-full" type="button" disabled={!approvedBudgetVersions.length || !zeroActivityEvidence.trim()} onClick={createZeroActivityAttestation}>Create nil declaration</button>
                </div>
              </div>
              <div className="overflow-x-auto">
                <table className="table-base">
                  <thead><tr><th>Period</th><th>Observed bank / AR / AP</th><th>Evidence</th><th>Status</th><th>Action</th></tr></thead>
                  <tbody>
                    {cashPeriodAttestations.map((item: any) => (
                      <tr key={item.id}>
                        <td>{new Date(item.periodStart).toLocaleDateString()} – {new Date(item.periodEnd).toLocaleDateString()}</td>
                        <td>{item.observedBankTransactionCount} / {item.observedArReceiptCount} / {item.observedApPaymentCount}</td>
                        <td>{item.evidenceReference}</td>
                        <td>{item.status}</td>
                        <td>{item.status === "Draft"
                          ? <button className="btn-secondary" onClick={() => submitZeroActivityAttestation(item.id)}>Submit</button>
                          : item.status === "PendingCertification"
                            ? <button className="btn-secondary" onClick={() => certifyZeroActivityAttestation(item.id)}>Certify</button>
                            : "—"}</td>
                      </tr>
                    ))}
                    {cashPeriodAttestations.length === 0 && <tr><td colSpan={5} className="text-center text-slate-500">No period nil declarations recorded.</td></tr>}
                  </tbody>
                </table>
              </div>
            </div>
            <div className="overflow-x-auto">
              <table className="table-base">
                <thead><tr><th>Source reference</th><th>Event</th><th>Effective date</th><th>Cash-flow section</th><th className="text-right">Amount</th><th>Status</th><th>Action</th></tr></thead>
                <tbody>
                  {budgetExecutionActuals.map((actual: any) => (
                    <tr key={actual.id}>
                      <td className="font-mono">{actual.sourceReference}</td><td>{actual.eventKind}</td>
                      <td>{new Date(actual.effectiveDate).toLocaleDateString()}</td><td>{actual.cashFlowSection}</td>
                      <td className="text-right">{actual.currency} {fmt(actual.amount)}</td><td>{actual.status}</td>
                      <td>{actual.status === "Draft" ? <button className="btn-secondary" onClick={() => submitCashActual(actual.id)}>Submit</button> : actual.status === "PendingCertification" ? <button className="btn-secondary" onClick={() => certifyCashActual(actual.id)}>Certify</button> : "—"}</td>
                    </tr>
                  ))}
                  {budgetExecutionActuals.length === 0 && <tr><td colSpan={7} className="text-center text-slate-500">No cash actuals captured for this financial year.</td></tr>}
                </tbody>
              </table>
            </div>
            {cashActualReconciliation && (
              <div className="border-t pt-3 space-y-2">
                <div className="flex items-center justify-between gap-2 flex-wrap">
                  <h4 className="font-semibold">IPSAS 24 reconciliation to Statement of Cash Flows</h4>
                  <span className={cashActualReconciliation.reconciled ? "text-sm font-medium text-green-700" : "text-sm font-medium text-amber-700"}>
                    {cashActualReconciliation.reconciled ? "Reconciled" : "Not ready for issue"}
                  </span>
                </div>
                <p className="text-xs text-slate-500">
                  Source coverage: {cashActualReconciliation.sourceCoverageComplete ? "complete" : "incomplete"} ·
                  Expected-source register: {cashActualReconciliation.expectedSourceRegisterPresent ? (cashActualReconciliation.expectedSourceRegisterApproved ? "approved" : "not approved") : "missing"} ·
                  Record-level reconciliations reviewed: {cashActualReconciliation.requiredSourceRecordReconciliationsReviewed} / {cashActualReconciliation.requiredExpectedSources} required sources ·
                  Register completeness certification: {cashActualReconciliation.expectedSourceRegisterCompletenessCertified ? "certified" : "not certified"} ·
                  {cashActualReconciliation.noSourceBackedCashActuals ? "No source-backed cash actuals or zero-activity certification captured · " : ""}
                  Uncaptured/unreconciled bank movements: {cashActualReconciliation.uncapturedOrUnreconciledPostedBankMovements} ·
                  Draft/pending actuals: {cashActualReconciliation.pendingOrDraftActuals} ·
                  Invalidated source/journal links: {cashActualReconciliation.invalidCertifiedActuals}
                </p>
                <div className="overflow-x-auto">
                  <table className="table-base">
                    <thead><tr><th>Cash-flow section</th><th className="text-right">Budget-basis actual</th><th className="text-right">Statement of Cash Flows</th><th className="text-right">Required adjustment</th><th className="text-right">Approved adjustment</th><th className="text-right">Unreconciled</th></tr></thead>
                    <tbody>{cashActualReconciliation.sections.map((section: any) => (
                      <tr key={section.section}>
                        <td>{section.section}</td><td className="text-right">{fmt(section.budgetBasisActual)}</td>
                        <td className="text-right">{fmt(section.statementOfCashFlowsActual)}</td>
                        <td className="text-right">{fmt(section.requiredAdjustment)}</td>
                        <td className="text-right">{fmt(section.approvedAdjustment)}</td>
                        <td className="text-right">{fmt(section.unreconciledDifference)}</td>
                      </tr>
                    ))}</tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
          {!budgetComparisonLoading && !budgetComparison && (
            <p className="text-sm text-slate-500">No comparison is available. An approved original budget version is required before the statement can be generated.</p>
          )}
          {budgetComparison && (
            <>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-3 text-sm">
                <div><span className="text-slate-500">Currency</span><p className="font-medium">{budgetComparison.currency}</p></div>
                <div><span className="text-slate-500">Budget basis</span><p className="font-medium">{budgetComparison.accountingBasis}</p></div>
                <div><span className="text-slate-500">Classification basis</span><p className="font-medium">{budgetComparison.classificationBasis}</p></div>
                <div><span className="text-slate-500">Entity scope</span><p className="font-medium">{budgetComparison.entityScope}</p></div>
                <div><span className="text-slate-500">Original budget</span><p className="font-medium">{budgetComparison.originalBudgetVersionId}</p></div>
                <div><span className="text-slate-500">Final budget</span><p className="font-medium">{budgetComparison.finalBudgetAvailable ? budgetComparison.finalBudgetVersionId : "Not approved"}</p></div>
              </div>
              {!budgetComparison.comparableActualsAvailable && (
                <div className="rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                  {budgetComparison.comparabilityMessage}
                </div>
              )}
              <div className="overflow-x-auto">
                <table className="table-base min-w-[1100px]">
                  <thead><tr><th>Treasury classification</th><th>GL SubType</th><th>Notes</th><th>Fund centre / level</th><th>Cost centre</th><th>Account</th><th className="text-right">Original</th><th className="text-right">Final</th><th className="text-right">Actual (comparable)</th><th className="text-right">Variance</th><th>Comment Ref</th><th>Original-to-final explanation</th><th>Final-to-actual explanation</th></tr></thead>
                  <tbody>
                    {budgetComparison.lines.map((line: any, index: number) => (
                      <tr key={`${line.accountCode}-${line.fundCentreCode}-${line.costCentreCode}-${index}`}>
                        <td>{line.treasuryClassificationCode ?? "—"}</td><td>{line.classificationCode}</td><td>{line.noteReference ?? "—"}</td>
                        <td><span className="font-mono">{line.fundCentreCode}</span><br />{line.fundCentreName}<div className="text-xs text-slate-500">{line.fundCentreLevel}</div></td>
                        <td>{line.costCentreCode ? <><span className="font-mono">{line.costCentreCode}</span><br />{line.costCentreName}</> : "—"}</td>
                        <td><span className="font-mono">{line.accountCode}</span><br />{line.accountName}</td>
                        <td className="text-right">{fmt(line.originalBudget)}</td>
                        <td className="text-right">{line.finalBudget == null ? "—" : fmt(line.finalBudget)}</td>
                        <td className="text-right">{line.actualOnComparableBasis == null ? "—" : fmt(line.actualOnComparableBasis)}</td>
                        <td className="text-right">{line.variance == null ? "—" : fmt(line.variance)}</td>
                        <td>{budgetComparison.varianceExplanations?.find((x: any) => x.budgetVersionLineId === line.budgetVersionLineId && x.varianceAmount === line.variance && x.status === "Approved")?.commentReference ?? budgetComparison.varianceExplanations?.find((x: any) => x.budgetVersionLineId === line.budgetVersionLineId && x.varianceAmount === line.variance && x.status === "Approved")?.evidenceReference ?? "—"}</td>
                        <td>{line.originalToFinalExplanation ?? "—"}</td>
                        <td>{budgetComparison.varianceExplanations?.find((x: any) => x.budgetVersionLineId === line.budgetVersionLineId && x.varianceAmount === line.variance && x.status === "Approved")?.explanation ?? (line.variance ? "Explanation required" : "—")}</td>
                      </tr>
                    ))}
                    {(() => {
                      const lines = budgetComparison.lines;
                      const income = lines.filter((line: any) => line.accountType === "Income");
                      const expense = lines.filter((line: any) => line.accountType === "Expense");
                      if (!lines.length || income.length + expense.length !== lines.length) return null;
                      const sum = (items: any[], field: string) => items.reduce((total, line) => total + Number(line[field] ?? 0), 0);
                      const original = sum(income, "originalBudget") - sum(expense, "originalBudget");
                      const finalAvailable = budgetComparison.finalBudgetAvailable && lines.every((line: any) => line.finalBudget != null);
                      const finalAmount = finalAvailable ? sum(income, "finalBudget") - sum(expense, "finalBudget") : null;
                      const actualAvailable = budgetComparison.comparableActualsAvailable && lines.every((line: any) => line.actualOnComparableBasis != null);
                      const actual = actualAvailable ? sum(income, "actualOnComparableBasis") - sum(expense, "actualOnComparableBasis") : null;
                      const difference = finalAmount == null || actual == null ? null : finalAmount - actual;
                      return <tr className="font-semibold border-t-2">
                        <td colSpan={6}>TOTAL SURPLUS / (DEFICIT) FOR THE YEAR</td>
                        <td className="text-right">{fmt(original)}</td>
                        <td className="text-right">{finalAmount == null ? "—" : fmt(finalAmount)}</td>
                        <td className="text-right">{actual == null ? "—" : fmt(actual)}</td>
                        <td className="text-right">{difference == null ? "—" : fmt(difference)}</td>
                        <td colSpan={3}>—</td>
                      </tr>;
                    })()}
                    {budgetComparison.lines.length === 0 && <tr><td colSpan={13} className="text-center text-slate-500">No budget lines are available.</td></tr>}
                  </tbody>
                </table>
              </div>

              <div className="border rounded-lg p-4 space-y-3">
                <div className="flex items-center justify-between gap-2 flex-wrap">
                  <h3 className="font-semibold">Final budget-to-actual variance explanations</h3>
                  <span className={budgetComparison.originalToFinalExplanationsComplete ? "text-sm text-green-700" : "text-sm text-amber-700"}>
                    {budgetComparison.originalToFinalExplanationsComplete ? "Original-to-final budget changes classified and explained" : "Original-to-final amendment classification outstanding"}
                  </span>
                  <span className={budgetComparison.varianceExplanationsComplete ? "text-sm text-green-700" : "text-sm text-amber-700"}>
                    {budgetComparison.varianceExplanationsComplete ? "All non-zero variances explained and approved" : "Independent explanations outstanding"}
                  </span>
                </div>
                <p className="text-xs text-slate-500">Until an approved entity-specific materiality threshold is configured, ApexERP applies a conservative rule: every non-zero comparable final-budget-to-actual variance requires an evidence-backed explanation and independent approval.</p>
                <form onSubmit={createVarianceExplanation} className="grid grid-cols-1 md:grid-cols-3 gap-2">
                  <select className="input" required value={varianceDraft.budgetVersionLineId} onChange={(e) => setVarianceDraft({ ...varianceDraft, budgetVersionLineId: e.target.value })}>
                    <option value="">Select unexplained variance</option>
                    {budgetComparison.lines.filter((line: any) => line.variance != null && Number(line.variance) !== 0 &&
                      !budgetComparison.varianceExplanations?.some((x: any) => x.budgetVersionLineId === line.budgetVersionLineId && x.varianceAmount === line.variance && x.status !== "Rejected")).map((line: any) => (
                      <option key={line.budgetVersionLineId} value={line.budgetVersionLineId}>{line.accountCode} · {line.fundCentreCode} · {fmt(line.variance)}</option>
                    ))}
                  </select>
                  <input className="input" required placeholder="Explanation of variance" value={varianceDraft.explanation} onChange={(e) => setVarianceDraft({ ...varianceDraft, explanation: e.target.value })} />
                  <input className="input" required placeholder="Supporting evidence reference" value={varianceDraft.evidenceReference} onChange={(e) => setVarianceDraft({ ...varianceDraft, evidenceReference: e.target.value })} />
                  <input className="input" required placeholder="Comment Ref (Appendix 5)" value={varianceDraft.commentReference} onChange={(e) => setVarianceDraft({ ...varianceDraft, commentReference: e.target.value })} />
                  <button className="btn-secondary md:col-span-3" disabled={!budgetComparison.comparableActualsAvailable || !varianceDraft.budgetVersionLineId}>Create variance explanation</button>
                </form>
                <div className="overflow-x-auto">
                  <table className="table-base"><thead><tr><th>Variance</th><th>Explanation</th><th>Comment Ref</th><th>Evidence</th><th>Status</th><th>Action</th></tr></thead>
                    <tbody>{budgetComparison.varianceExplanations?.map((item: any) => (
                      <tr key={item.id}>
                        <td>{fmt(item.varianceAmount)}</td><td>{item.explanation}</td><td>{item.commentReference || item.evidenceReference}</td><td>{item.evidenceReference}</td><td>{item.status}</td>
                        <td>{item.status === "Draft" ? <button type="button" className="btn-secondary" onClick={() => varianceExplanationAction(item.id, "submit")}>Submit</button> : item.status === "PendingReview" ? <div className="flex gap-1"><button type="button" className="btn-secondary" onClick={() => varianceExplanationAction(item.id, "review")}>Approve</button><button type="button" className="btn-secondary" onClick={() => varianceExplanationAction(item.id, "reject")}>Reject</button></div> : "—"}</td>
                      </tr>
                    ))}
                    {!budgetComparison.varianceExplanations?.length && <tr><td colSpan={6} className="text-center text-slate-500">No variance explanations recorded.</td></tr>}</tbody>
                  </table>
                </div>
              </div>
              <div className="border rounded-lg p-4 space-y-3">
                <h3 className="font-semibold">Reconciliation of budget and financial-statement actuals</h3>
                <p className="text-xs text-slate-500">Record basis, timing, entity or presentation differences with supporting evidence. Accrual budgets may reconcile revenue, expense and the three net cash-flow sections; cash budgets may reconcile only operating, investing and financing cash flows. Every row requires independent workflow approval.</p>
                <form onSubmit={createBudgetReconciliation} className="grid grid-cols-1 md:grid-cols-3 gap-2">
                  <select className="input" required value={budgetReconciliationDraft.type}
                    onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, type: e.target.value })}>
                    <option value="Basis">Basis difference</option><option value="Timing">Timing difference</option>
                    <option value="Entity">Entity difference</option><option value="Presentation">Presentation difference</option>
                  </select>
                  <select className="input" required value={budgetReconciliationDraft.statementSection}
                    onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, statementSection: e.target.value })}>
                    {budgetComparison?.accountingBasis === "Cash" ? <>
                      <option value="Operating">Net cash flows — operating</option>
                      <option value="Investing">Net cash flows — investing</option>
                      <option value="Financing">Net cash flows — financing</option>
                    </> : <>
                      <option value="Revenue">Total revenue</option><option value="Expense">Total expense</option>
                      <option value="Operating">Net cash flows — operating</option>
                      <option value="Investing">Net cash flows — investing</option>
                      <option value="Financing">Net cash flows — financing</option>
                    </>}
                  </select>
                  <input className="input" required placeholder="Description of difference"
                    value={budgetReconciliationDraft.description}
                    onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, description: e.target.value })} />
                  {budgetComparison?.accountingBasis !== "Cash" && <>
                    <input className="input" type="number" step="0.01" placeholder="Revenue adjustment"
                      value={budgetReconciliationDraft.revenueAmount}
                      onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, revenueAmount: Number(e.target.value) })} />
                    <input className="input" type="number" step="0.01" placeholder="Expense adjustment"
                      value={budgetReconciliationDraft.expenseAmount}
                      onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, expenseAmount: Number(e.target.value) })} />
                  </>}
                  <input className="input" type="number" step="0.01" placeholder="Operating cash-flow adjustment"
                    value={budgetReconciliationDraft.operatingAmount}
                    onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, operatingAmount: Number(e.target.value) })} />
                  <input className="input" type="number" step="0.01" placeholder="Investing cash-flow adjustment"
                    value={budgetReconciliationDraft.investingAmount}
                    onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, investingAmount: Number(e.target.value) })} />
                  <input className="input" type="number" step="0.01" placeholder="Financing cash-flow adjustment"
                    value={budgetReconciliationDraft.financingAmount}
                    onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, financingAmount: Number(e.target.value) })} />
                  <input className="input md:col-span-2" required placeholder="Supporting workpaper / evidence reference"
                    value={budgetReconciliationDraft.supportingReference}
                    onChange={(e) => setBudgetReconciliationDraft({ ...budgetReconciliationDraft, supportingReference: e.target.value })} />
                  <button className="btn-secondary md:col-span-3"
                    disabled={!budgetComparison?.finalBudgetAvailable || !budgetReconciliationDraft.description.trim() || !budgetReconciliationDraft.supportingReference.trim()}>
                    Create reconciliation adjustment
                  </button>
                </form>
                <div className="overflow-x-auto">
                  <table className="table-base">
                    <thead><tr><th>Type</th><th>Description</th><th>Section</th><th>Revenue</th><th>Expense</th><th>Operating</th><th>Investing</th><th>Financing</th><th>Status</th><th>Action</th></tr></thead>
                    <tbody>{budgetReconciliations.map((item: any) => (
                      <tr key={item.id}>
                        <td>{item.type}</td><td>{item.description}</td><td>{item.statementSection}</td>
                        <td>{fmt(item.revenueAmount)}</td><td>{fmt(item.expenseAmount)}</td>
                        <td>{fmt(item.operatingAmount)}</td><td>{fmt(item.investingAmount)}</td>
                        <td>{fmt(item.financingAmount)}</td><td>{item.status}</td>
                        <td>{item.status === "Draft"
                          ? <button type="button" className="btn-secondary" onClick={() => budgetReconciliationAction(item.id, "submit")}>Submit</button>
                          : item.status === "PendingApproval"
                            ? <button type="button" className="btn-secondary" onClick={() => budgetReconciliationAction(item.id, "finalise")}>Finalise approved workflow</button>
                            : "—"}</td>
                      </tr>
                    ))}
                    {!budgetReconciliations.length && <tr><td colSpan={10} className="text-center text-slate-500">No reconciliation adjustments recorded.</td></tr>}</tbody>
                  </table>
                </div>
                <p className="text-xs text-slate-500">For pending rows, complete the independent approval in the Approvals workspace first, then finalise it here. Draft and pending rows are not included in the published comparison.</p>
              </div>
              {budgetComparison.reconciliations?.length > 0 && (
                <div>
                  <h3 className="font-semibold mb-2">Approved reconciliation adjustments included in comparison</h3>
                  <table className="table-base">
                    <thead><tr><th>Difference type</th><th>Description</th><th>Statement section</th><th className="text-right">Revenue</th><th className="text-right">Expense</th><th className="text-right">Operating</th><th className="text-right">Investing</th><th className="text-right">Financing</th></tr></thead>
                    <tbody>{budgetComparison.reconciliations.map((item: any) => (
                      <tr key={item.id}><td>{item.type}</td><td>{item.description}</td><td>{item.statementSection}</td><td className="text-right">{fmt(item.revenueAmount)}</td><td className="text-right">{fmt(item.expenseAmount)}</td><td className="text-right">{fmt(item.operatingAmount)}</td><td className="text-right">{fmt(item.investingAmount)}</td><td className="text-right">{fmt(item.financingAmount)}</td></tr>
                    ))}</tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </section>
      )}

      {tab === "assets" && (
        <div className="card overflow-x-auto">
          <h2 className="font-semibold mb-3">Verification Batches</h2>
          <table className="table-base">
            <thead><tr><th>Batch #</th><th>Date</th><th>Location</th><th>Complete</th><th>Scans</th></tr></thead>
            <tbody>{batches.map((b: any) => (<tr key={b.id}><td className="font-mono">{b.batchNo}</td><td>{new Date(b.verificationDate).toLocaleDateString()}</td><td>{b.location}</td><td>{b.isComplete ? "Yes" : "No"}</td><td>{b.scans?.length ?? 0}</td></tr>))}</tbody>
          </table>
          <p className="text-xs text-slate-400 mt-3">Barcode/RFID hardware scanning itself isn't integrated in this build — start a batch and submit each scanned tag value via the API to record Found/Missing/Condition-Changed results.</p>
        </div>
      )}
    </AppShell>
  );
}
