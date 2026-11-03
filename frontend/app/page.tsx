"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth";
import ConfigError from "@/components/ConfigError";
import LoadingState from "@/components/LoadingState";

export default function Home() {
  const { user, loading, configError } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (!loading && !configError) router.replace(user ? "/dashboard" : "/login");
  }, [loading, user, configError, router]);

  if (configError) return <ConfigError message={configError} />;
  return <LoadingState />;
}
