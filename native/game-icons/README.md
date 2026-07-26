# Sidebar game icons

Square badge art for games that have no Steam app at all, so neither the store
search nor a curated Steam app ID can resolve them. `../game-icons.json` points
at these files by raw.githubusercontent.com URL, which is one of the hosts
`GameIconService` is allowed to download from.

Only add a file here when a game genuinely can't be covered any other way -
a `steamAppIds` entry in `game-icons.json` is preferred, since that keeps the
art coming from Steam's own CDN.

Format: 256x256 PNG, artwork edge to edge on an opaque brand-coloured tile.
The sidebar draws them at 30px inside a rounded square, so a wordmark that
needs reading at full size is fine - colour and shape are what identify the
game at that size.
