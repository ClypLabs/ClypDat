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
    "ClypDat is always recording in the background. Press one key and the last few minutes of your gameplay are already saved. No process hook, built-in editor, CS2 auto-clipping.",
  // No explicit icon entry - app/favicon.ico is picked up automatically by
  // Next's file convention, and duplicating it here just risks the two
  // drifting if one gets swapped and not the other.
  openGraph: {
    title: "ClypDat - Instant replay buffer for Windows",
    description:
      "ClypDat is always recording in the background. Press one key and the last few minutes of your gameplay are already saved.",
    url: siteUrl,
    siteName: "ClypDat",
    images: ["/icon.png"],
    type: "website",
  },
  twitter: {
    card: "summary",
    title: "ClypDat - Instant replay buffer for Windows",
    description:
      "ClypDat is always recording in the background. Press one key and the last few minutes of your gameplay are already saved.",
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
