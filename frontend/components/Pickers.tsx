"use client";

import { SelectField } from "@/components/ui";
import { useLoad } from "@/lib/hooks";

type Item = Record<string, any>;

/** A dropdown filled from an API list. Shows its own loading/error text so forms never silently lack options. */
export function RemoteSelect({ label, endpoint, value, onChange, map, hint, required }: {
  label: string; endpoint: string; value: string; onChange: (v: string) => void; map: (item: Item) => { value: string; label: string }; hint?: string; required?: boolean;
}) {
  const { data, error, loading } = useLoad<Item[]>(endpoint, []);
  return (
    <SelectField
      label={label} value={value} required={required} onChange={(e) => onChange(e.target.value)}
      options={Array.isArray(data) ? data.map(map) : []} placeholder={loading ? "Loading…" : error ? "Could not load" : "Select…"}
      hint={error ?? hint}
    />
  );
}

export const AccountSelect = (p: { label: string; value: string; onChange: (v: string) => void; hint?: string; required?: boolean }) =>
  <RemoteSelect {...p} endpoint="/gl/accounts" map={(a) => ({ value: a.id, label: `${a.code} - ${a.name}` })} />;

export const SupplierSelect = (p: { label?: string; value: string; onChange: (v: string) => void; required?: boolean }) =>
  <RemoteSelect label={p.label ?? "Supplier"} {...p} endpoint="/ap/suppliers" map={(s) => ({ value: s.id, label: `${s.code} - ${s.name}` })} />;

export const CustomerSelect = (p: { label?: string; value: string; onChange: (v: string) => void; required?: boolean }) =>
  <RemoteSelect label={p.label ?? "Customer"} {...p} endpoint="/ar/customers" map={(c) => ({ value: c.id, label: `${c.code} - ${c.name}` })} />;

export const ItemSelect = (p: { label?: string; value: string; onChange: (v: string) => void; required?: boolean }) =>
  <RemoteSelect label={p.label ?? "Inventory item"} {...p} endpoint="/inventory/items" map={(i) => ({ value: i.id, label: `${i.sku ?? i.code ?? ""} ${i.name}`.trim() })} />;

export const AssetSelect = (p: { label?: string; value: string; onChange: (v: string) => void; required?: boolean }) =>
  <RemoteSelect label={p.label ?? "Fixed asset"} {...p} endpoint="/fixed-assets/assets" map={(a) => ({ value: a.id, label: `${a.assetNo} - ${a.name ?? a.description ?? ""}` })} />;

export const BankAccountSelect = (p: { label?: string; value: string; onChange: (v: string) => void; required?: boolean }) =>
  <RemoteSelect label={p.label ?? "Bank account"} {...p} endpoint="/cashbank/accounts" map={(b) => ({ value: b.id, label: `${b.name ?? b.accountName ?? b.accountNumber}` })} />;

export const UserSelect = (p: { label?: string; value: string; onChange: (v: string) => void; required?: boolean }) =>
  <RemoteSelect label={p.label ?? "User"} {...p} endpoint="/admin/users" map={(u) => ({ value: u.id, label: `${u.firstName ?? ""} ${u.lastName ?? ""} (${u.email})`.trim() })} />;
