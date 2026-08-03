import { DOWNLOAD_URL, GITHUB_URL } from "./Header";

export default function Hero() {
  return (
    <section className="relative overflow-hidden px-6 pt-20 pb-24 sm:pt-28 sm:pb-32">
      <div
        aria-hidden
        className="pointer-events-none absolute inset-x-0 -top-40 -z-10 flex justify-center"
      >
        <div className="h-[480px] w-[780px] rounded-full bg-emerald-500/20 blur-[140px]" />
      </div>

      <div className="mx-auto flex max-w-3xl flex-col items-center text-center">
        <div className="mb-6 inline-flex items-center gap-2 rounded-full border border-white/10 bg-white/5 px-3 py-1 text-xs text-zinc-300">
          <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
          Free &amp; open source, GPLv3
        </div>

        <h1 className="text-4xl font-semibold tracking-tight text-balance sm:text-6xl">
          Never miss the clip.
        </h1>
        <p className="mt-6 max-w-xl text-lg text-zinc-400 text-balance">
          ClypDat records a rolling buffer of your gameplay on Windows and
          saves the last N seconds the moment you hit a hotkey. No process
          hook, so anti-cheat has nothing to object to.
        </p>

        <div className="mt-10 flex flex-col items-center gap-3 sm:flex-row">
          <a
            href={DOWNLOAD_URL}
            className="flex h-12 w-full items-center justify-center gap-2 rounded-full bg-emerald-400 px-6 text-sm font-semibold text-emerald-950 transition-colors hover:bg-emerald-300 sm:w-auto"
          >
            Download for Windows
          </a>
          <a
            href={GITHUB_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="flex h-12 w-full items-center justify-center rounded-full border border-white/15 px-6 text-sm font-semibold text-zinc-200 transition-colors hover:border-white/30 hover:bg-white/5 sm:w-auto"
          >
            View source on GitHub
          </a>
        </div>
        <p className="mt-4 text-xs text-zinc-500">
          Windows 10/11, x64. Installer needs no admin approval.
        </p>
      </div>

      <div className="relative mx-auto mt-16 max-w-5xl">
        <div className="overflow-hidden rounded-2xl border border-white/10 bg-white/[0.03] shadow-2xl shadow-black/40">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src="https://github.com/user-attachments/assets/0436efa1-f47b-4a4b-9f8c-15c255c32fd9"
            alt="ClypDat library and editor preview"
            width={1404}
            height={914}
            className="w-full"
          />
        </div>
      </div>
    </section>
  );
}
