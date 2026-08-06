export type DocsNavigationItem = {
  href: string;
  label: string;
  description: string;
  keywords: string;
};

export const docsNavigation: DocsNavigationItem[] = [
  {
    href: "/docs",
    label: "Overview",
    description: "Choose the right PackageMedic workflow.",
    keywords: "introduction overview start workflow 0.6",
  },
  {
    href: "/docs/getting-started",
    label: "Getting started",
    description: "Install the tool and run the first diagnosis.",
    keywords: "install update requirements first scan path solution project",
  },
  {
    href: "/docs/commands",
    label: "Commands",
    description: "Doctor, audit, diff, simulate, baseline, and clean.",
    keywords: "cli doctor audit deprecated diff simulate init rules explain baseline clean options exit",
  },
  {
    href: "/docs/configuration",
    label: "Configuration",
    description: "Version repository policy as code.",
    keywords: "config json schema rules severity exclude suppressions timeout parallelism",
  },
  {
    href: "/docs/baselines",
    label: "Baselines",
    description: "Adopt PackageMedic without CI shock.",
    keywords: "baseline new existing resolved update gradual adoption fail-on-new",
  },
  {
    href: "/docs/impact-gate",
    label: "Impact Gate",
    description: "Causal paths, blast radius, and source trust.",
    keywords: "impact gate PMI001 PMI002 PMI003 PMI004 PMI005 PMI006 PMI007 PMI008 PMI009 PMI010 dependency path blast radius provenance content hash sha512 source mapping locked mode",
  },
  {
    href: "/docs/time-machine",
    label: "Time Machine",
    description: "Restore-validate one exact package candidate.",
    keywords: "simulate time machine candidate version snapshot restore what if upgrade rollback lock file credentials",
  },
  {
    href: "/docs/verified-experiments",
    label: "Verified experiments",
    description: "Restore, build, test, SBOM, and evidence workflows.",
    keywords: "0.6 verify restore build test trx sbom cyclonedx provenance in-toto self-hosted immutable snapshots",
  },
  {
    href: "/docs/github-action",
    label: "GitHub Action",
    description: "Annotations, artifacts, and code scanning.",
    keywords: "github action ci yaml sarif annotations artifact permissions pull request",
  },
  {
    href: "/docs/reports",
    label: "Reports",
    description: "Text, JSON, SARIF, and exit codes.",
    keywords: "report output json sarif text deterministic schema code scanning exit codes",
  },
  {
    href: "/docs/diagnostics",
    label: "Diagnostics",
    description: "Reference for PM001 through PM008.",
    keywords: "PM001 PM002 PM003 PM004 PM005 PM006 PM007 PM008 deprecated rules warning error",
  },
  {
    href: "/docs/security",
    label: "Safety & security",
    description: "Read-only boundaries and network behavior.",
    keywords: "security privacy safety read-only telemetry restore audit credentials bounds symlink",
  },
  {
    href: "/docs/troubleshooting",
    label: "Troubleshooting",
    description: "Resolve common restore, audit, and CI failures.",
    keywords: "troubleshooting error restore assets operational diff git history config sarif",
  },
];
