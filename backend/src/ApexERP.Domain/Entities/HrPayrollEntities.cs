using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    public class Department : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        public Guid? ManagerId { get; set; }
        public Guid? CostCentreId { get; set; }
        /// <summary>Public Sector (Module 16): fund centre this department's payroll cost is charged against.</summary>
        public Guid? FundCentreId { get; set; }
        /// <summary>Grants & NGO (Module 15): grant this department's payroll cost is charged against.</summary>
        public Guid? GrantId { get; set; }
    }

    public class Employee : TenantEntity
    {
        public string EmployeeNo { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? IdNo { get; set; }
        public string? NssaNo { get; set; }
        public string? Tin { get; set; }
        public Guid? DepartmentId { get; set; }
        public string? Position { get; set; }
        public DateTime StartDate { get; set; }
        public decimal BasicSalary { get; set; }
        public string Currency { get; set; } = "USD";
        public string? BankAccountNo { get; set; }
        public bool IsActive { get; set; } = true;
        public int LeaveDaysBalance { get; set; } = 22;
    }

    public class PayrollRun : TenantEntity
    {
        public int PeriodMonth { get; set; }
        public int PeriodYear { get; set; }
        public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;
        public Guid? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public Guid? JournalEntryId { get; set; }

        public ICollection<PayrollLine> Lines { get; set; } = new List<PayrollLine>();
    }

    public class PayrollLine : BaseEntity
    {
        public Guid PayrollRunId { get; set; }
        public Guid EmployeeId { get; set; }
        public decimal BasicPay { get; set; }
        public decimal Allowances { get; set; }
        public decimal GrossPay { get; set; }
        public decimal Paye { get; set; }
        public decimal NssaEmployee { get; set; }
        public decimal NssaEmployer { get; set; }
        public decimal AidsLevy { get; set; }
        public decimal OtherDeductions { get; set; }
        public decimal NetPay { get; set; }
    }

    public class LeaveRequest : TenantEntity
    {
        public Guid EmployeeId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int Days { get; set; }
        public string LeaveType { get; set; } = "Annual";
        public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;
        public string? Reason { get; set; }
    }
}
