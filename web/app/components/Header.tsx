import Image from "next/image";
import Link from "next/link";

const GITHUB_URL = "https://github.com/ClypDat/ClypDat";
const DOWNLOAD_URL = `${GITHUB_URL}/releases/latest/download/ClypDat-Setup.exe`;

export default function Header() {
  return (
    <header className="sticky top-0 z-50 border-b border-white/10 bg-background/80 backdrop-blur">
      <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
        <Link href="/" className="flex items-center gap-2.5">
          <Image src="/icon.png" alt="" width={28} height={28} className="rounded-md" />
          <span className="text-lg font-semibold tracking-tight">ClypDat</span>
        </Link>
        <nav className="hidden items-center gap-8 text-sm text-zinc-400 sm:flex">
          <a href="#features" className="transition-colors hover:text-zinc-100">
            Features
          </a>
          <a href="#editor" className="transition-colors hover:text-zinc-100">
            Editor
          </a>
          <a
            href={GITHUB_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="transition-colors hover:text-zinc-100"
          >
            GitHub
          </a>
        </nav>
        <a
          href={DOWNLOAD_URL}
          className="rounded-full bg-emerald-400 px-4 py-2 text-sm font-semibold text-emerald-950 transition-colors hover:bg-emerald-300"
        >
          Download
        </a>
      </div>
    </header>
  );
}

export { GITHUB_URL, DOWNLOAD_URL };
