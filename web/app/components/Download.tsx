const GITHUB_URL = "https://github.com/ClypDat/ClypDat";
const RELEASES_URL = `${GITHUB_URL}/releases/latest`;

const downloads = [
  {
    label: "Installer",
    file: "ClypDat-Setup.exe",
    description: "Recommended. Installs to your user folder, no admin needed.",
  },
  {
    label: "Portable",
    file: "ClypDat-Portable.exe",
    description: "Self-extracting, no install step.",
  },
  {
    label: "Zip",
    file: "ClypDat-win-x64.zip",
    description: "Raw build folder.",
  },
  {
    label: "MSI",
    file: "ClypDat.msi",
    description: "For managed/enterprise deployment.",
  },
];

export default function Download() {
  return (
    <section
      id="download"
      className="relative overflow-hidden border-t border-white/10 px-6 py-24 text-center"
    >
      <div
        aria-hidden
        className="pointer-events-none absolute inset-x-0 -bottom-40 -z-10 flex justify-center"
      >
        <div className="animate-drift-a h-[380px] w-[620px] rounded-full bg-emerald-500/15 blur-[130px]" />
      </div>

      <div className="mx-auto max-w-2xl">
        <h2 className="text-3xl font-semibold tracking-tight sm:text-4xl">
          Get ClypDat
        </h2>
        <p className="mt-4 text-zinc-400">
          Windows 10 or 11, x64. The native capture backend works on NVIDIA,
          AMD, and as a software fallback, any GPU-less machine.
        </p>

        <div className="mt-10 grid gap-3 sm:grid-cols-2">
          {downloads.map((item) => (
            <a
              key={item.file}
              href={`${RELEASES_URL}/download/${item.file}`}
              className="group flex flex-col items-start rounded-xl border border-white/10 bg-white/[0.03] px-5 py-4 text-left transition-all duration-300 hover:-translate-y-0.5 hover:border-emerald-400/40 hover:bg-white/[0.05] motion-reduce:hover:translate-y-0"
            >
              <span className="text-sm font-semibold text-zinc-100 transition-colors group-hover:text-emerald-300">
                {item.label}
              </span>
              <span className="mt-1 text-xs text-zinc-500">
                {item.description}
              </span>
            </a>
          ))}
        </div>

        <a
          href={RELEASES_URL}
          target="_blank"
          rel="noopener noreferrer"
          className="mt-8 inline-block text-sm text-zinc-500 underline decoration-white/20 underline-offset-4 transition-colors hover:text-zinc-300"
        >
          All releases on GitHub
        </a>
      </div>
    </section>
  );
}
