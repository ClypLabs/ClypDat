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

const siteUrl = "https://clypdat.app";

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: "ClypDat - Instant replay buffer for Windows",
  description:
    "ClypDat records a rolling buffer of your gameplay and saves the last N seconds when you hit a hotkey. No process hook, built-in editor, CS2 auto-clipping.",
  icons: {
    icon: "/favicon.ico",
  },
  openGraph: {
    title: "ClypDat - Instant replay buffer for Windows",
    description:
      "ClypDat records a rolling buffer of your gameplay and saves the last N seconds when you hit a hotkey.",
    url: siteUrl,
    siteName: "ClypDat",
    images: ["/icon.png"],
    type: "website",
  },
  twitter: {
    card: "summary",
    title: "ClypDat - Instant replay buffer for Windows",
    description:
      "ClypDat records a rolling buffer of your gameplay and saves the last N seconds when you hit a hotkey.",
    images: ["/icon.png"],
  },
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="min-h-full flex flex-col bg-background text-foreground">
        {children}
      </body>
    </html>
  );
}
