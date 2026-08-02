import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  metadataBase: new URL("https://gonzmeza.github.io/package-medic/"),
  title: "PackageMedic — A dependency doctor for .NET projects",
  description:
    "Find unused NuGet packages, version drift, Central Package Management bypasses, and restore problems.",
  icons: {
    icon: "/packagemedic-icon.png",
    shortcut: "/packagemedic-icon.png",
  },
  openGraph: {
    type: "website",
    title: "PackageMedic — A dependency doctor for .NET projects",
    description:
      "Read-only NuGet dependency diagnostics for SDK-style .NET projects.",
    images: [{ url: "/og.png", width: 1280, height: 640, alt: "PackageMedic" }],
  },
  twitter: {
    card: "summary_large_image",
    title: "PackageMedic — A dependency doctor for .NET projects",
    description:
      "Read-only NuGet dependency diagnostics for SDK-style .NET projects.",
    images: ["/og.png"],
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body className={`${geistSans.variable} ${geistMono.variable}`}>
        {children}
      </body>
    </html>
  );
}
