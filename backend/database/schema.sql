-- ApexERP reference schema (PostgreSQL 16)
-- This file mirrors the EF Core entity model 1:1 for documentation and manual-bootstrap purposes.
-- The authoritative source of truth is the C# entity classes under backend/src/ApexERP.Domain/Entities.
-- In a real .NET dev environment, generate and apply the actual migration instead:
--   cd backend && dotnet ef migrations add InitialCreate -p src/ApexERP.Infrastructure -s src/ApexERP.API
--   dotnet ef database update -p src/ApexERP.Infrastructure -s src/ApexERP.API
-- This script is provided so the database can be bootstrapped even without the .NET SDK installed.

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

CREATE TABLE companies (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name VARCHAR(255) NOT NULL,
    registration_no VARCHAR(100),
    vat_no VARCHAR(100),
    base_currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    functional_currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    fiscal_year_start_month INT NOT NULL DEFAULT 1,
    address TEXT,
    logo_url TEXT,
    timezone VARCHAR(64) NOT NULL DEFAULT 'Africa/Harare',
    sector VARCHAR(100),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID
);

CREATE TABLE roles (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    name VARCHAR(100) NOT NULL,
    permissions_json JSONB NOT NULL DEFAULT '[]',
    is_system_role BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ
);

CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    email VARCHAR(255) NOT NULL,
    phone VARCHAR(50),
    password_hash TEXT NOT NULL,
    role_id UUID NOT NULL REFERENCES roles(id),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    totp_secret VARCHAR(255),
    totp_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    last_login TIMESTAMPTZ,
    first_name VARCHAR(100),
    last_name VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    UNIQUE (company_id, email)
);

CREATE TABLE gl_accounts (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    code VARCHAR(20) NOT NULL,
    name VARCHAR(255) NOT NULL,
    type VARCHAR(20) NOT NULL, -- Asset/Liability/Equity/Income/Expense
    sub_type VARCHAR(50),
    parent_id UUID REFERENCES gl_accounts(id),
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_bank_account BOOLEAN NOT NULL DEFAULT FALSE,
    is_control_account BOOLEAN NOT NULL DEFAULT FALSE,
    current_balance NUMERIC(20,6) NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    UNIQUE (company_id, code)
);

CREATE TABLE accounting_periods (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    year INT NOT NULL,
    period_no INT NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Open',
    closed_by UUID,
    closed_at TIMESTAMPTZ,
    UNIQUE (company_id, year, period_no)
);

CREATE TABLE cost_centres (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    code VARCHAR(20) NOT NULL,
    name VARCHAR(255) NOT NULL,
    parent_id UUID REFERENCES cost_centres(id),
    is_active BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE journal_entries (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    entry_no VARCHAR(50) NOT NULL,
    description TEXT,
    entry_date DATE NOT NULL,
    period_id UUID NOT NULL REFERENCES accounting_periods(id),
    source VARCHAR(20) NOT NULL,
    reference VARCHAR(100),
    status VARCHAR(20) NOT NULL DEFAULT 'Draft',
    reversal_of_id UUID REFERENCES journal_entries(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (company_id, entry_no)
);

CREATE TABLE journal_lines (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    journal_entry_id UUID NOT NULL REFERENCES journal_entries(id) ON DELETE CASCADE,
    gl_account_id UUID NOT NULL REFERENCES gl_accounts(id),
    debit NUMERIC(20,6) NOT NULL DEFAULT 0,
    credit NUMERIC(20,6) NOT NULL DEFAULT 0,
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    exchange_rate NUMERIC(20,6) NOT NULL DEFAULT 1,
    base_debit NUMERIC(20,6) NOT NULL DEFAULT 0,
    base_credit NUMERIC(20,6) NOT NULL DEFAULT 0,
    cost_centre_id UUID REFERENCES cost_centres(id),
    narrative TEXT
);
CREATE INDEX idx_journal_lines_account ON journal_lines(gl_account_id);
CREATE INDEX idx_journal_lines_entry ON journal_lines(journal_entry_id);

CREATE TABLE tax_codes (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    code VARCHAR(20) NOT NULL,
    name VARCHAR(100) NOT NULL,
    rate NUMERIC(5,4) NOT NULL,
    type VARCHAR(10) NOT NULL, -- VAT / WHT
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    effective_from DATE NOT NULL
);

CREATE TABLE paye_tax_bands (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    lower_bound NUMERIC(20,6) NOT NULL,
    upper_bound NUMERIC(20,6),
    rate NUMERIC(5,4) NOT NULL,
    deduct NUMERIC(20,6) NOT NULL DEFAULT 0,
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    effective_from DATE NOT NULL,
    effective_to DATE
);

CREATE TABLE customers (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    code VARCHAR(30) NOT NULL,
    name VARCHAR(255) NOT NULL,
    email VARCHAR(255),
    phone VARCHAR(50),
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    credit_limit NUMERIC(20,6) NOT NULL DEFAULT 0,
    ar_account_id UUID REFERENCES gl_accounts(id),
    vat_no VARCHAR(100),
    payment_terms_days INT NOT NULL DEFAULT 30,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE (company_id, code)
);

CREATE TABLE ar_invoices (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    invoice_no VARCHAR(50) NOT NULL,
    customer_id UUID NOT NULL REFERENCES customers(id),
    invoice_date DATE NOT NULL,
    due_date DATE NOT NULL,
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    exchange_rate NUMERIC(20,6) NOT NULL DEFAULT 1,
    subtotal NUMERIC(20,6) NOT NULL DEFAULT 0,
    vat_amount NUMERIC(20,6) NOT NULL DEFAULT 0,
    total NUMERIC(20,6) NOT NULL DEFAULT 0,
    outstanding NUMERIC(20,6) NOT NULL DEFAULT 0,
    status VARCHAR(20) NOT NULL DEFAULT 'Draft',
    journal_entry_id UUID REFERENCES journal_entries(id),
    UNIQUE (company_id, invoice_no)
);

CREATE TABLE suppliers (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    code VARCHAR(30) NOT NULL,
    name VARCHAR(255) NOT NULL,
    email VARCHAR(255),
    phone VARCHAR(50),
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    ap_account_id UUID REFERENCES gl_accounts(id),
    vat_no VARCHAR(100),
    payment_terms_days INT NOT NULL DEFAULT 30,
    wht_applicable BOOLEAN NOT NULL DEFAULT FALSE,
    wht_rate NUMERIC(5,4) NOT NULL DEFAULT 0.10,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE (company_id, code)
);

CREATE TABLE ap_invoices (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    invoice_no VARCHAR(50) NOT NULL,
    supplier_id UUID NOT NULL REFERENCES suppliers(id),
    invoice_date DATE NOT NULL,
    due_date DATE NOT NULL,
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    exchange_rate NUMERIC(20,6) NOT NULL DEFAULT 1,
    subtotal NUMERIC(20,6) NOT NULL DEFAULT 0,
    vat_amount NUMERIC(20,6) NOT NULL DEFAULT 0,
    wht_amount NUMERIC(20,6) NOT NULL DEFAULT 0,
    total NUMERIC(20,6) NOT NULL DEFAULT 0,
    outstanding NUMERIC(20,6) NOT NULL DEFAULT 0,
    status VARCHAR(20) NOT NULL DEFAULT 'Draft',
    journal_entry_id UUID REFERENCES journal_entries(id),
    UNIQUE (company_id, invoice_no)
);

CREATE TABLE bank_accounts (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    name VARCHAR(255) NOT NULL,
    account_no VARCHAR(100) NOT NULL,
    bank_name VARCHAR(100) NOT NULL,
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    gl_account_id UUID NOT NULL REFERENCES gl_accounts(id),
    current_balance NUMERIC(20,6) NOT NULL DEFAULT 0
);

CREATE TABLE inventory_items (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    sku VARCHAR(50) NOT NULL,
    name VARCHAR(255) NOT NULL,
    description TEXT,
    unit_of_measure VARCHAR(20) NOT NULL DEFAULT 'EA',
    reorder_level NUMERIC(20,6) NOT NULL DEFAULT 0,
    reorder_quantity NUMERIC(20,6) NOT NULL DEFAULT 0,
    valuation_method VARCHAR(10) NOT NULL DEFAULT 'AVCO',
    asset_account_id UUID REFERENCES gl_accounts(id),
    cogs_account_id UUID REFERENCES gl_accounts(id),
    sales_account_id UUID REFERENCES gl_accounts(id),
    quantity_on_hand NUMERIC(20,6) NOT NULL DEFAULT 0,
    average_cost NUMERIC(20,6) NOT NULL DEFAULT 0,
    standard_sales_price NUMERIC(20,6) NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_manufactured BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE (company_id, sku)
);

CREATE TABLE fixed_assets (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    asset_no VARCHAR(50) NOT NULL,
    name VARCHAR(255) NOT NULL,
    category_id UUID NOT NULL,
    acquisition_date DATE NOT NULL,
    acquisition_cost NUMERIC(20,6) NOT NULL,
    depreciation_method VARCHAR(20) NOT NULL,
    useful_life_months INT NOT NULL,
    residual_value NUMERIC(20,6) NOT NULL DEFAULT 0,
    accumulated_depreciation NUMERIC(20,6) NOT NULL DEFAULT 0,
    current_nbv NUMERIC(20,6) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Active',
    disposal_date DATE,
    disposal_proceeds NUMERIC(20,6)
);

CREATE TABLE employees (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    employee_no VARCHAR(30) NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    id_no VARCHAR(50),
    nssa_no VARCHAR(50),
    tin VARCHAR(50),
    department_id UUID,
    position VARCHAR(100),
    start_date DATE NOT NULL,
    basic_salary NUMERIC(20,6) NOT NULL DEFAULT 0,
    currency VARCHAR(10) NOT NULL DEFAULT 'USD',
    bank_account_no VARCHAR(100),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE (company_id, employee_no)
);

CREATE TABLE payroll_runs (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    period_month INT NOT NULL,
    period_year INT NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Draft',
    approved_by UUID,
    approved_at TIMESTAMPTZ,
    journal_entry_id UUID REFERENCES journal_entries(id)
);

CREATE TABLE payroll_lines (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    payroll_run_id UUID NOT NULL REFERENCES payroll_runs(id) ON DELETE CASCADE,
    employee_id UUID NOT NULL REFERENCES employees(id),
    gross_pay NUMERIC(20,6) NOT NULL,
    paye NUMERIC(20,6) NOT NULL,
    nssa_employee NUMERIC(20,6) NOT NULL,
    nssa_employer NUMERIC(20,6) NOT NULL,
    aids_levy NUMERIC(20,6) NOT NULL,
    net_pay NUMERIC(20,6) NOT NULL
);

CREATE TABLE audit_log (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    company_id UUID NOT NULL REFERENCES companies(id),
    user_id UUID,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT now(),
    ip_address VARCHAR(64),
    action VARCHAR(100) NOT NULL,
    module VARCHAR(50) NOT NULL,
    entity_type VARCHAR(100) NOT NULL,
    entity_id UUID NOT NULL,
    before_value JSONB,
    after_value JSONB
);
-- audit_log is intentionally append-only: no UPDATE/DELETE grants should be issued to application roles.

-- Note: this file covers the primary financial and master-data tables for readability.
-- The remaining ~20 operational tables (bom_headers, work_orders, purchase_orders, crm_leads,
-- sales_orders, projects, timesheets, workflow_requests, etc.) follow the same column layout
-- as their corresponding C# entity in backend/src/ApexERP.Domain/Entities and are created
-- automatically by the EF Core migration.
