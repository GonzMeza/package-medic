"use client";

import { useState } from "react";

export default function CodeBlock({
  children,
  label = "Terminal",
}: Readonly<{
  children: string;
  label?: string;
}>) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(children.trim());
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1600);
  }

  return (
    <div className="docs-code">
      <div className="docs-code-bar">
        <span>{label}</span>
        <button type="button" onClick={copy} aria-label={`Copy ${label} example`}>
          {copied ? "Copied" : "Copy"}
        </button>
      </div>
      <pre><code>{children.trim()}</code></pre>
    </div>
  );
}
