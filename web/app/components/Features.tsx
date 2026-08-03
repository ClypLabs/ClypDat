type Feature = {
  title: string;
  description: string;
};

const features: Feature[] = [
  {
    title: "No process hook",
    description:
      "Built directly on DXGI Desktop Duplication instead of injecting into the game, so anti-cheat has nothing to flag. True per-window capture keeps recording through alt-tabs and overlays.",
  },
  {
    title: "GPU-accelerated everything",
    description:
      "GPU-side downscaling and NVENC encoding, falling back to AMD AMF and then software libx264 - it isn't NVIDIA-only, and it isn't going to tank your frame rate.",
  },
  {
    title: "CS2 auto-clipping",
    description:
      "Listens to CS2's own Game State Integration feed - no screen or voice analysis - and saves a clip on kills, headshots, or multi-kills automatically. (Experimental)",
  },
  {
    title: "Full session recording",
    description:
      "Optionally record the entire session to one file alongside the rolling clip buffer. Audio resyncs every 60s so multi-hour sessions don't drift out of sync.",
  },
  {
    title: "Smart game detection",
    description:
      "Foreground-window scanning against a catalog of known games and your installed Steam library, updated from GitHub. Clips are named after the game automatically.",
  },
  {
    title: "Import from Medal",
    description:
      "Scans Medal's local database (or its clips folder, as a fallback) and copies your existing clips straight into your ClypDat library with the original titles intact.",
  },
];

export default function Features() {
  return (
    <section id="features" className="border-t border-white/10 px-6 py-24">
      <div className="mx-auto max-w-6xl">
        <div className="max-w-2xl">
          <h2 className="text-3xl font-semibold tracking-tight sm:text-4xl">
            Built for how you actually play.
          </h2>
          <p className="mt-4 text-zinc-400">
            Two capture backends, switchable in Settings - ClypDat&apos;s own
            native engine by default, with Windows Capture as a fallback.
            Neither one hooks into your game.
          </p>
        </div>

        <div className="mt-14 grid gap-x-8 gap-y-12 sm:grid-cols-2 lg:grid-cols-3">
          {features.map((feature) => (
            <div key={feature.title}>
              <h3 className="text-base font-semibold text-zinc-100">
                {feature.title}
              </h3>
              <p className="mt-2 text-sm leading-6 text-zinc-400">
                {feature.description}
              </p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
