import Image from "next/image";

const games = [
  { name: "Counter-Strike 2", src: "/screenshots/cs2-cover.jpg" },
  { name: "Dota 2", src: "/screenshots/dota2-cover.jpg" },
  { name: "League of Legends", src: "/screenshots/league-cover.jpg" },
];

export default function GamesStrip() {
  return (
    <section className="border-t border-white/10 px-6 py-20">
      <div className="mx-auto max-w-6xl text-center">
        <p className="text-sm text-zinc-500">
          Detected automatically, plus every game in your Steam library
        </p>
        <div className="mt-8 grid grid-cols-3 gap-4 sm:gap-6">
          {games.map((game) => (
            <div
              key={game.name}
              className="group relative aspect-[16/9] overflow-hidden rounded-xl border border-white/10"
            >
              <Image
                src={game.src}
                alt={game.name}
                fill
                sizes="(min-width: 640px) 33vw, 100vw"
                className="object-cover transition-transform duration-300 group-hover:scale-105"
              />
              <div className="absolute inset-0 flex items-end bg-gradient-to-t from-black/80 via-black/0 to-black/0 p-3">
                <span className="text-xs font-medium text-zinc-100 sm:text-sm">
                  {game.name}
                </span>
              </div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
