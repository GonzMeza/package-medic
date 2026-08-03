"use client";

/* eslint-disable @next/next/no-img-element */
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useMemo, useState } from "react";
import { assetPath, product } from "../product";
import { docsNavigation } from "./navigation";

export default function DocsShell({ children }: Readonly<{ children: React.ReactNode }>) {
  const pathname = usePathname();
  const [query, setQuery] = useState("");
  const [menuOpen, setMenuOpen] = useState(false);

  const visibleItems = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase();
    if (!normalizedQuery) {
      return docsNavigation;
    }

    return docsNavigation.filter((item) =>
      `${item.label} ${item.description} ${item.keywords}`
        .toLowerCase()
        .includes(normalizedQuery),
    );
  }, [query]);

  return (
    <div className="docs-site">
      <header className="docs-topbar">
        <Link className="brand-lockup" href="/" aria-label="PackageMedic home">
          <img src={assetPath("packagemedic-icon.png")} alt="" width="34" height="34" />
          <span>PackageMedic</span>
          <small>Docs</small>
        </Link>

        <div className="docs-topbar-actions">
          <span className="docs-version">v{product.version}</span>
          <a href="https://github.com/GonzMeza/package-medic" target="_blank" rel="noreferrer">
            GitHub <span aria-hidden="true">↗</span>
          </a>
        </div>

        <button
          className="docs-menu-button"
          type="button"
          aria-expanded={menuOpen}
          aria-controls="docs-sidebar"
          onClick={() => setMenuOpen((current) => !current)}
        >
          {menuOpen ? "Close" : "Menu"}
        </button>
      </header>

      <div className="docs-layout">
        <aside className={`docs-sidebar${menuOpen ? " open" : ""}`} id="docs-sidebar">
          <label className="docs-search">
            <span>Search documentation</span>
            <div>
              <span aria-hidden="true">⌕</span>
              <input
                type="search"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="Search commands, rules…"
              />
            </div>
          </label>

          <nav aria-label="Documentation navigation">
            <span className="docs-nav-label">PackageMedic 0.4</span>
            {visibleItems.map((item) => {
              const active = pathname === item.href || pathname === `${item.href}/`;
              return (
                <Link
                  href={item.href}
                  key={item.href}
                  className={active ? "active" : undefined}
                  aria-current={active ? "page" : undefined}
                  onClick={() => setMenuOpen(false)}
                >
                  <strong>{item.label}</strong>
                  <small>{item.description}</small>
                </Link>
              );
            })}
            {visibleItems.length === 0 && (
              <p className="docs-empty-search">No matching section. Try a command or diagnostic code.</p>
            )}
          </nav>

          <div className="docs-sidebar-note">
            <span className="status-pixel" />
            <div>
              <strong>Stable documentation</strong>
              <small>Matches PackageMedic {product.version}</small>
            </div>
          </div>
        </aside>

        <main className="docs-main">{children}</main>
      </div>
    </div>
  );
}
