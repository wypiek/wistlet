# wistlet

A small social network written in ASP.NET Core (.NET 10, Razor Pages, EF Core + SQLite).
The page layout follows the cohost style — a cherry header, an off-white page, a left-hand
navigation column and a single feed of posts. Form controls, tabs, menus, tables, fieldsets,
progress bars and balloons come from [7.css](https://khang-nd.github.io/7.css/).

## Features

**Social**
- Posts with optional image upload, replies (threads), likes and saved posts
- `#hashtags` (tag pages, trending tags) and `@mentions`
- Follow / unfollow, home timeline, explore (latest, top this week, media), people directory
- Profiles with picture, bio, location and website; posts / replies / media / likes / followers / following tabs
- Notifications for likes, replies, mentions, new followers and moderator messages
- Direct messages, with a per-user privacy setting (everyone / people I follow / nobody)
- Blocking, reporting posts and accounts, search across people, tags and posts

**Admin panel** (`/admin`, for moderators and admins)
- Dashboard: counts, sign-ups and posts per day, newest members, system information
- Site settings (admins only): site name, tagline, about text, footer, announcement banner,
  open/closed registration, guest browsing, maintenance mode, maximum post length, posts per page,
  image uploads and size limit, banned words, direct messages on/off
- User management: search and filter, edit name/e-mail/role/verified badge, temporary or permanent
  bans with a reason, password reset, sign out all sessions, remove picture, clear profile,
  moderator messages, delete all posts, delete account
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

## Project layout

```
src/Wistlet/
  Data/            EF Core context, migrations, start-up initialisation
  Models/          Entities and view models
  Services/        Accounts, posts, notifications, messaging, uploads, site settings, audit log
  Infrastructure/  Auth cookie validation, middleware, tag helpers, formatting helpers
  Pages/           Razor Pages (Admin/, Account/, Messages/ and the social pages)
  wwwroot/         site.css, site.js, vendored 7.css and Atkinson Hyperlegible
Dockerfile, docker-compose.yml, Caddyfile
```

## Licences

7.css (MIT) and Atkinson Hyperlegible (SIL Open Font Licence) are vendored under
`src/Wistlet/wwwroot/lib/` together with their licence files.
