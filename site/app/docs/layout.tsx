import type { Metadata } from "next";
import DocsShell from "./docs-shell";

export const metadata: Metadata = {
  title: "Documentation — PackageMedic",
  description:
    "Install, configure, automate, and troubleshoot PackageMedic for .NET dependency graphs.",
};

export default function DocsLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <DocsShell>{children}</DocsShell>;
}
