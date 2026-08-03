# ClypDat marketing site

Landing page for [ClypDat](https://github.com/ClypDat/ClypDat), built with
Next.js (App Router) + Tailwind CSS 4.

## Developing

```bash
pnpm install
pnpm dev
```

Open [http://localhost:3000](http://localhost:3000).

## Structure

- `app/page.tsx` assembles the page from `app/components/`.
- Download links point at `github.com/ClypDat/ClypDat/releases/latest/download/<asset>`,
  which always resolves to the current release - no version to keep in sync here.
- Assets (icon, game covers) are copied from the main repo's `/assets`.

## Building

```bash
pnpm build
```

Produces a static-friendly Next.js build in `.next/`.
