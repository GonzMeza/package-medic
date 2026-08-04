import type { Metadata } from "next";
import localFont from "next/font/local";
import "./globals.css";

const pagesBasePath = process.env.NEXT_PUBLIC_BASE_PATH ?? "";
const siteUrl = "https://gonzmeza.github.io/package-medic/";

const geistSans = localFont({
  src: "./fonts/geist-latin.woff2",
  variable: "--font-geist-sans",
  display: "swap",
});

const geistMono = localFont({
  src: "./fonts/geist-mono-latin.woff2",
  variable: "--font-geist-mono",
  display: "swap",
});

export const metadata: Metadata = {
  metadataBase: new URL("https://gonzmeza.github.io/package-medic/"),
  title: "PackageMedic — A dependency doctor for .NET projects",
  description:
    "Trace NuGet dependency impact, blast radius, source trust, and risk changes through text, JSON, SARIF, and GitHub pull-request checks.",
  icons: {
    icon: `${pagesBasePath}/packagemedic-icon.png`,
    shortcut: `${pagesBasePath}/packagemedic-icon.png`,
  },
  openGraph: {
    type: "website",
    title: "PackageMedic — A dependency doctor for .NET projects",
    description:
      "Read-only NuGet Impact Gate, causal dependency paths, vulnerability and deprecation audit, and deterministic SARIF.",
    images: [{ url: `${siteUrl}og.png`, width: 1280, height: 640, alt: "PackageMedic" }],
  },
  twitter: {
    card: "summary_large_image",
    title: "PackageMedic — A dependency doctor for .NET projects",
    description:
      "Read-only NuGet Impact Gate, causal dependency paths, vulnerability and deprecation audit, and deterministic SARIF.",
    images: [`${siteUrl}og.png`],
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" data-scroll-behavior="smooth">
      <body className={`${geistSans.variable} ${geistMono.variable}`}>
        {children}
      </body>
    </html>
  );
}
