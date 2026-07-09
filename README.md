# ApexEnterpriseMS

> **A modern, enterprise-grade Enterprise Resource Planning (ERP) platform built with ASP.NET Core 8, PostgreSQL 16, and Next.js 14.**

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet)
![Next.js](https://img.shields.io/badge/Next.js-14-black?style=for-the-badge&logo=next.js)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?style=for-the-badge&logo=postgresql)
![TypeScript](https://img.shields.io/badge/TypeScript-5-blue?style=for-the-badge&logo=typescript)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

---

## Overview

ApexEnterpriseMS is a comprehensive Enterprise Resource Planning (ERP) solution designed for businesses, government institutions, NGOs, manufacturers, and multi-company enterprises.

The platform integrates finance, operations, supply chain, manufacturing, human resources, customer relationship management, and business intelligence into one secure, scalable, and modern application.

Built on **ASP.NET Core 8**, **PostgreSQL 16**, and **Next.js 14**, ApexEnterpriseMS delivers real business functionality with enterprise architecture, supporting IFRS-compliant financial reporting and Zimbabwean regulatory requirements.

---

## Key Features

### Financial Management
- General Ledger
- Accounts Receivable
- Accounts Payable
- Cash & Bank Management
- Multi-Currency Support
- Budgeting
- Cost Centres
- Inter-Company Accounting
- Consolidation

### Inventory & Supply Chain
- Inventory Management
- FIFO Costing
- AVCO Costing
- Purchase Orders
- Goods Receipts
- Vendor Management
- Warehouse Management
- Stock Adjustments
- Stock Transfers

### Manufacturing
- Bill of Materials (BOM)
- Work Orders
- Production Costing
- Material Consumption
- Finished Goods

### Payroll & HR
- Employee Management
- Payroll Processing
- PAYE
- NSSA
- AIDS Levy
- Departments
- Leave Management
- Payslips

### Customer Management
- CRM
- Leads
- Opportunities
- Sales Orders
- Customer Database

### Fixed Assets
- Asset Register
- Depreciation
- Asset Disposal
- IAS 16 Revaluation
- Barcode/RFID Support

### Reporting
- Balance Sheet
- Income Statement
- Cash Flow Statement
- Statement of Changes in Equity
- Financial Notes
- Trial Balance
- Budget Reports
- Dashboard Analytics

### Government & NGO
- Grant Management
- Donor Reporting
- Public Sector Budgeting
- Commitment Accounting
- Tender Management

### Administration
- JWT Authentication
- Role-Based Access Control (RBAC)
- Multi-Tenant Architecture
- Audit Logs
- Approval Workflows

---

## IFRS Financial Statements

ApexEnterpriseMS automatically generates:

- Statement of Financial Position
- Statement of Profit or Loss & OCI
- Statement of Changes in Equity
- Statement of Cash Flows
- Notes to Financial Statements

All reports are generated directly from live accounting data.

---

## Technology Stack

### Backend
- ASP.NET Core 8
- C#
- Entity Framework Core
- PostgreSQL 16
- JWT Authentication
- BCrypt
- REST API
- Docker

### Frontend
- Next.js 14
- React
- TypeScript
- Tailwind CSS
- Axios

### Infrastructure
- Docker Compose
- PostgreSQL
- EF Core Migrations

---

## Project Structure

```text
ApexEnterpriseMS
│
├── backend
│   ├── src
│   │   ├── ApexERP.Domain
│   │   ├── ApexERP.Application
│   │   ├── ApexERP.Infrastructure
│   │   └── ApexERP.API
│   └── database
│
├── frontend
│   ├── app
│   ├── components
│   ├── lib
│   └── public
│
├── scripts
│
├── docker-compose.yml
│
└── README.md
```

---

## System Architecture

```text
                Next.js Frontend
                        │
                        │
                ASP.NET Core API
                        │
        ┌───────────────┼───────────────┐
        │               │               │
 Authentication   Business Logic    Reporting
        │               │               │
        └───────────────┼───────────────┘
                        │
                Entity Framework Core
                        │
                 PostgreSQL Database
```

---

## ERP Modules

- Authentication & Security
- General Ledger
- Accounts Receivable
- Accounts Payable
- Cash & Bank
- Inventory Management
- Fixed Assets
- Payroll & HR
- Tax Compliance
- Manufacturing
- Procurement
- CRM
- Project Management
- Reporting & BI
- Grant Management
- Public Sector Management
- Administration

---

## Getting Started

### Prerequisites
- Docker Desktop
- .NET 8 SDK
- Node.js 20+
- PostgreSQL 16

### Clone the Repository
```bash
git clone https://github.com/munyaradzichiondegwa/ApexEnterpriseMS.git
cd ApexEnterpriseMS
```

### Docker Deployment
```bash
docker compose up --build
```

**Application URLs**

| Service  | URL                             |
|----------|---------------------------------|
| Web      | http://localhost:3000           |
| API      | http://localhost:8080           |
| Swagger  | http://localhost:8080/swagger   |

### Backend
```bash
cd backend
dotnet restore
dotnet build
dotnet run --project src/ApexERP.API
```

### Frontend
```bash
cd frontend
npm install
npm run dev
```

---

## Database

Generate migrations:
```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

Seed data:
```bash
docker exec -i apexerp-postgres psql -U apexerp -d apexerp < backend/database/seed.sql
```

---

## Security

- JWT Authentication
- BCrypt Password Hashing
- RBAC Authorization
- Multi-Tenant Data Isolation
- Audit Trail
- Approval Workflow Engine

---

## Compliance

Supports:
- IFRS Financial Reporting
- IAS 1
- IAS 16
- Zimbabwe PAYE
- VAT
- NSSA
- AIDS Levy
- Withholding Tax

---

## External Integrations

Ready for integration with:
- ZIMRA FDMS
- ZIMRA e-Filing
- EcoCash
- OneMoney
- PayNow
- ZimSwitch
- SMS Gateway
- RFID Devices
- Barcode Scanners
- AWS S3
- SignalR
- RabbitMQ

---

## Roadmap

- Mobile Applications
- AI Financial Assistant
- Predictive Analytics
- Workflow Automation
- Business Intelligence Dashboard
- Vendor Portal
- API Marketplace

---

## Contributing

Contributions are welcome.

1. Fork the repository
2. Create a feature branch:
   ```bash
   git checkout -b feature/new-feature
   ```
3. Commit your changes:
   ```bash
   git commit -m "Add new feature"
   ```
4. Push:
   ```bash
   git push origin feature/new-feature
   ```
5. Open a Pull Request.

---

## License

This project is licensed under the MIT License.

---

## 👨‍💻 Author

**Munyaradzi Chiondegwa**  

ApexEnterpriseMS was architected and developed by **Munyaradzi Chiondegwa**, a multidisciplinary software developer, financial systems specialist, and accounting professional with expertise in enterprise software, financial management, ERP systems, automation, governance, and regulatory compliance. He holds an MBA, a Bachelor of Accounting, and is pursuing a Bachelor of Applied Science in Software Development. His work focuses on building scalable business systems that integrate accounting, finance, and modern software engineering practices.

### Connect
- GitHub: [munyaradzichiondegwa](https://github.com/munyaradzichiondegwa)
- Portfolio: https://munyaradzichiondegwa.netlify.app/
- LinkedIn: https://www.linkedin.com/in/munyaradzi-chiondegwa/
- Email: chiondegwabm@gmail.com

---

*Built with ❤️ using ASP.NET Core, PostgreSQL, Next.js, and TypeScript.*
```
