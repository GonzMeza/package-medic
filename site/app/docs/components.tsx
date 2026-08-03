import Link from "next/link";

export function DocPage({
  eyebrow,
  title,
  description,
  children,
}: Readonly<{
  eyebrow: string;
  title: string;
  description: string;
  children: React.ReactNode;
}>) {
  return (
    <article className="docs-article">
      <header className="docs-page-header">
        <span>{eyebrow}</span>
        <h1>{title}</h1>
        <p>{description}</p>
      </header>
      {children}
    </article>
  );
}

export function Callout({
  children,
  tone = "info",
  title,
}: Readonly<{
  children: React.ReactNode;
  tone?: "info" | "warning" | "success";
  title: string;
}>) {
  return (
    <aside className={`docs-callout ${tone}`}>
      <strong>{title}</strong>
      <div>{children}</div>
    </aside>
  );
}

export function PageLinks({
  previous,
  next,
}: Readonly<{
  previous?: { href: string; label: string };
  next?: { href: string; label: string };
}>) {
  return (
    <nav className="docs-page-links" aria-label="Documentation pages">
      {previous ? (
        <Link href={previous.href}>
          <small>Previous</small>
          <strong>← {previous.label}</strong>
        </Link>
      ) : <span />}
      {next && (
        <Link href={next.href}>
          <small>Next</small>
          <strong>{next.label} →</strong>
        </Link>
      )}
    </nav>
  );
}

export function OptionTable({
  headers,
  rows,
}: Readonly<{
  headers: string[];
  rows: React.ReactNode[][];
}>) {
  return (
    <div className="docs-table-wrap">
      <table>
        <thead>
          <tr>{headers.map((header) => <th key={header}>{header}</th>)}</tr>
        </thead>
        <tbody>
          {rows.map((row, rowIndex) => (
            <tr key={rowIndex}>
              {row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
