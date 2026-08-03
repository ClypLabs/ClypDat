const editorPoints = [
  "Trim start/end with a scrubbable thumbnail preview and waveform view",
  "Per-track audio volume, including separate game/chat/mic tracks",
  "Export mixes every track down to one file that plays anywhere",
  "Save Trim re-encodes in place and keeps tracks separately editable",
  "GPU export via NVENC (H.264/H.265/AV1) with automatic CPU fallback",
];

export default function Editor() {
  return (
    <section id="editor" className="border-t border-white/10 px-6 py-24">
      <div className="mx-auto grid max-w-6xl gap-12 lg:grid-cols-2 lg:items-center">
        <div>
          <h2 className="text-3xl font-semibold tracking-tight sm:text-4xl">
            A real editor, not an afterthought.
          </h2>
          <p className="mt-4 text-zinc-400">
            Every clip opens straight into ClypDat&apos;s built-in editor.
            Trim it, balance the audio tracks, and export - no round trip
            through another app.
          </p>
          <ul className="mt-8 space-y-4">
            {editorPoints.map((point) => (
              <li key={point} className="flex items-start gap-3">
                <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-emerald-400" />
                <span className="text-sm leading-6 text-zinc-300">
                  {point}
                </span>
              </li>
            ))}
          </ul>
        </div>

        <div className="rounded-2xl border border-white/10 bg-white/[0.03] p-8">
          <p className="text-sm text-zinc-500">Right-click a clip to</p>
          <ul className="mt-3 grid grid-cols-2 gap-3 text-sm text-zinc-200">
            {[
              "Rename",
              "Export",
              "Delete",
              "Open file location",
              "Filter by game",
              "Filter by clip type",
            ].map((action) => (
              <li
                key={action}
                className="rounded-lg border border-white/10 bg-white/[0.02] px-3 py-2.5"
              >
                {action}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </section>
  );
}
