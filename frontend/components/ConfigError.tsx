/** Shown instead of a broken app when the web app was built without a usable API address. */
export default function ConfigError({ message }: { message: string }) {
  return (
    <div className="min-h-screen flex items-center justify-center px-4 bg-slate-50">
      <div className="card max-w-lg space-y-3" role="alert">
        <h1 className="text-lg font-bold text-red-700">ApexERP can&apos;t reach its server</h1>
        <p className="text-sm text-slate-700">{message}</p>
        <p className="text-xs text-slate-500">
          This is a deployment setting, not something you did. Send this message to whoever manages the ApexERP hosting.
        </p>
      </div>
    </div>
  );
}
