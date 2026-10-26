# wistlet

A small social network written in ASP.NET Core (.NET 10, Razor Pages, EF Core + SQLite),
styled after cohost as it looked in 2022: the cherry header bar, off-white page, Atkinson
Hyperlegible, post boxes with tinted header and footer strips, big avatars beside the feed,
profile header images with the cherry project box, longan titled boxes and tag pills.

The cohost styles come from the stylesheet preserved by
[cohost-dl](https://github.com/cpsdqs/cohost-dl) (MIT), vendored in
`src/Wistlet/wwwroot/lib/cohost/`. Wistlet's own additions (navigation, forms, menus,
composer, admin pages) are in `wwwroot/css/site.css` and use the same colour tokens.

## Features

**Posts, the cohost way**
- Markdown with raw HTML and inline CSS ("CSS crimes"), up to 200 000 characters, an optional
  headline and an optional image
- A preview button in the composer that renders the draft through the same sanitizer posts use
- A `<!--more-->` line splits off the part shown in feeds ("read more"); very tall posts (over
  twice the window height) are collapsed automatically
- Tags in a separate field, shown in order below the post (`#tag` pages and trending tags)
- Content warnings and an 18+ flag: such posts start collapsed, and other people's 18+ posts only
  appear for viewers who opted in under Settings → Privacy
- Shares (with or without your own post on top), share chains, and a "deleted by its original
  author" tombstone when the shared post is gone
- Comments as nested threads on the post page (not in feeds), editing your own posts and comments
- `@mentions`, likes and saved posts

**Social**
- Follow / unfollow, home timeline, explore (latest, top this week, media), people directory
- Profiles with picture (squircle, circle, rounded square, egg or capsule shape), header image (banner),
  tagline, pronouns, a Markdown description, location and website; posts / comments / media / likes /
  followers / following tabs
- Notifications for likes, comments, shares, mentions, new followers and moderator messages
- Direct messages, with a per-user privacy setting (everyone / people I follow / nobody)
- Blocking, reporting posts and accounts, search across people, tags and posts

**Admin panel** (`/admin`, for moderators and admins)
- Dashboard: counts, sign-ups and posts per day, newest members, system information
- Site settings (admins only): site name, tagline, about text, footer, announcement banner,
  open/closed registration, guest browsing, maintenance mode, maximum post length, posts per page,
  image uploads and size limit, banned words, direct messages on/off
- User management: search and filter, edit name/e-mail/role/verified badge, temporary or permanent
  bans with a reason, password reset, sign out all sessions, remove picture, clear profile,
  remove banner, moderator messages, delete all posts, delete account
- Post management: search and filter (reported, hidden, pinned, images), hide, pin to Explore, delete
- Reports queue: dismiss, resolve, hide or delete the post, jump to the author
- Audit log (admins only) of every moderation action

**Roles:** `User`, `Moderator` (users, posts, reports) and `Admin` (everything). The first account
registered on a fresh install becomes the admin, or one can be created from environment variables
(see below). The last remaining admin can't be demoted or deleted.

**Security:** PBKDF2 password hashing, antiforgery tokens on every form, cookies re-validated on each
request (bans, password changes and role changes take effect immediately), rate limiting on log-in,
sign-up and posting, uploads checked by file signature and stored under random names, a strict
Content-Security-Policy and other security headers.

## CSS crimes

Posts keep what cohost allowed and drop what it removed. The rules follow
[prechoster](https://github.com/cpsdqs/prechoster)'s documentation of cohost's renderer, and are
implemented in `Services/PostRenderer.cs` (Markdig for Markdown, AngleSharp for the HTML pass):

- Kept: `div`, `span`, `details`/`summary`, tables, images, audio and video, headings, lists,
  `ruby`, `mark`, `kbd` and the other text-level elements, with their `style` attributes.
- Removed with their content: `script`, `style`, `iframe`, `svg`, `canvas`, `form` fields and the
  like. Unknown elements are unwrapped so their text stays.
- `class` and event handler attributes are dropped. `id`s get a `user-content-` prefix and `#links`
  are rewritten to match. `<input>` becomes a disabled checkbox.
- In `style`: no custom properties (`--x`), no `!important`, `position: fixed` becomes `static`, and
  `url()` only takes http(s), relative or `data:image/`/`data:font/` URLs. The site's colour tokens
  (`rgb(var(--color-cherry))` …) and the `spin` and `bounce` keyframes are available.
- Links get `rel="nofollow noopener noreferrer ugc"`; only http(s), mailto and relative links survive.
- The post body is a contained box (`isolation`, `contain: paint`), so nothing escapes onto the page.

Posts are rendered once when saved. When the rules change, `PostRenderer.Version` is bumped and
older posts are re-rendered on start-up.

## Deploying with Docker Compose and Caddy

Docker runs only the app. Caddy runs **on the host** (installed from your distribution's packages or
the official binary), so Docker never pulls a Caddy image. The app is published on `127.0.0.1:8080`,
which means it can only be reached through Caddy.

1. Install Caddy on the server: <https://caddyserver.com/docs/install>.
2. Configure and start the app:
   ```sh
   cp .env.example .env        # optional: set an admin account, port, …
   docker compose up -d --build
   ```
3. Point Caddy at it: edit the domain in `Caddyfile`, then
   ```sh
   sudo cp Caddyfile /etc/caddy/Caddyfile
   sudo systemctl reload caddy
   ```
   Caddy obtains and renews the HTTPS certificate automatically.

All data (SQLite database, uploads, cookie encryption keys) lives in the `wistlet-data` volume,
mounted at `/data`. To back it up:

```sh
docker run --rm -v wistlet_wistlet-data:/data -v "$PWD":/backup busybox tar czf /backup/wistlet-backup.tgz -C /data .
```

### Configuration

| Variable (`.env`)        | Meaning                                                        | Default |
|--------------------------|----------------------------------------------------------------|---------|
| `WISTLET_PORT`           | Host port on 127.0.0.1 that Caddy proxies to                   | `8080`  |
| `WISTLET_ADMIN_USER`     | Create (or promote) this admin account on start-up            | —       |
| `WISTLET_ADMIN_PASSWORD` | Password for that account (only used when it's created)        | —       |
| `WISTLET_ADMIN_EMAIL`    | E-mail for that account                                        | —       |
| `WISTLET_SEED_DEMO`      | Add a few demo users and posts to an empty site                | `false` |

Everything else is changed from the admin panel at runtime.

If you change the port, change it in the `Caddyfile` as well.

## Development

Requires the .NET 10 SDK.

```sh
cd src/Wistlet
dotnet run
```

In development the app stores its data in `src/Wistlet/data/` and seeds a few demo users and posts.
Register an account and it becomes the admin.

Database schema changes go through EF Core migrations:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Wistlet -o Data/Migrations
```

Migrations are applied automatically when the app starts.

The sanitizer has unit tests:

```sh
dotnet test
```

## Project layout

```
src/Wistlet/
  Data/            EF Core context, migrations, start-up initialisation
  Models/          Entities and view models
  Services/        Accounts, posts and the post renderer, notifications, messaging, uploads, site settings, audit log
  Infrastructure/  Auth cookie validation, middleware, tag helpers, formatting helpers
  Pages/           Razor Pages (Admin/, Account/, Messages/ and the social pages)
  wwwroot/         site.css, site.js, vendored cohost styles, Atkinson Hyperlegible and League Mono
tests/Wistlet.Tests/  Unit tests for the post renderer
Dockerfile, docker-compose.yml, Caddyfile
```

## Licences

These are vendored under `src/Wistlet/wwwroot/lib/` together with their licence files:

- `cohost/`: the cohost stylesheet and prose styles from cohost-dl (MIT). Wistlet's copy
  points the fonts at the self-hosted files, removes the dark-mode rules (cohost was light-only
  in 2022) and uses its own egg and capsule mask shapes.
- `atkinson-hyperlegible/` and `league-mono/`: fonts under the SIL Open Font Licence.

Wistlet is not affiliated with cohost or Anti Software Software Club.
