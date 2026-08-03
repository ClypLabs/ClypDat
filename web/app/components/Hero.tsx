import Image from "next/image";
import appPreview from "@/public/screenshots/app-preview.webp";
import { DOWNLOAD_URL, GITHUB_URL } from "./Header";

export default function Hero() {
  return (
    <section className="relative overflow-hidden px-6 pt-20 pb-24 sm:pt-28 sm:pb-32">
      {/* Ambient glow. Sits behind everything, never intercepts a click, and
          animates only where the OS allows motion - see globals.css. */}
      <div
        aria-hidden
        className="pointer-events-none absolute inset-x-0 -top-56 -z-10 flex justify-center"
      >
        <div className="relative h-[620px] w-full max-w-5xl">
          <div className="animate-drift-a absolute left-1/2 top-0 h-[440px] w-[680px] -translate-x-1/2 rounded-full bg-emerald-500/25 blur-[130px]" />
          <div className="animate-drift-b absolute left-[18%] top-24 h-[360px] w-[460px] rounded-full bg-teal-400/20 blur-[120px]" />
          <div className="animate-drift-c absolute right-[14%] top-10 h-[380px] w-[420px] rounded-full bg-cyan-500/15 blur-[120px]" />
        </div>
      </div>

      <div className="mx-auto flex max-w-3xl flex-col items-center text-center">
        <div className="animate-rise mb-6 inline-flex items-center gap-2 rounded-full border border-white/10 bg-white/5 px-3 py-1 text-xs text-zinc-300">
          <span className="relative flex h-1.5 w-1.5">
            <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75 motion-reduce:hidden" />
            <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-emerald-400" />
          </span>
          Free &amp; open source, GPLv3
        </div>

        <h1
          className="animate-rise text-4xl font-semibold tracking-tight text-balance sm:text-6xl"
          style={{ animationDelay: "60ms" }}
        >
          Never miss the clip.
        </h1>
        <p
          className="animate-rise mt-6 max-w-xl text-lg text-zinc-400 text-balance"
          style={{ animationDelay: "120ms" }}
        >
          ClypDat is always recording in the background. The moment something
          worth keeping happens, press one key &mdash; the last few minutes are
          already saved. No process hook, so anti-cheat has nothing to object
          to.
        </p>

        <div
          className="animate-rise mt-10 flex flex-col items-center gap-3 sm:flex-row"
          style={{ animationDelay: "180ms" }}
        >
          <a
            href={DOWNLOAD_URL}
            className="flex h-12 w-full items-center justify-center gap-2 rounded-full bg-emerald-400 px-6 text-sm font-semibold text-emerald-950 transition-all hover:bg-emerald-300 hover:shadow-lg hover:shadow-emerald-500/25 sm:w-auto"
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
        <p
          className="animate-rise mt-4 text-xs text-zinc-500"
          style={{ animationDelay: "240ms" }}
        >
          Windows 10/11, x64. Installer needs no admin approval.
        </p>
      </div>

      <div
        className="animate-rise relative mx-auto mt-16 max-w-5xl"
        style={{ animationDelay: "300ms" }}
      >
        <div className="relative overflow-hidden rounded-2xl border border-white/10 bg-white/[0.03] shadow-2xl shadow-black/50">
          {/* Static import, so Next knows the dimensions at build time and
              generates the blur placeholder itself - no layout shift, and
              something on screen before the full image arrives. priority
              because this is the LCP element. */}
          <Image
            src={appPreview}
            alt="The ClypDat library, showing captured clips grouped by day"
            placeholder="blur"
            priority
            sizes="(min-width: 1280px) 1024px, 100vw"
            className="w-full"
          />
          <div
            aria-hidden
            className="pointer-events-none absolute inset-0 overflow-hidden"
          >
            <div className="animate-sheen h-full w-1/3 bg-gradient-to-r from-transparent via-white/[0.07] to-transparent" />
          </div>
        </div>
      </div>
    </section>
  );
}
