# Wistlet federation protocol (version 1)

Wistlet instances can follow, like and comment on each other's accounts. The protocol is
Wistlet's own: it is not ActivityPub, and an instance only federates with servers that
present themselves as Wistlet (see *Discovery*). Implementation: `src/Wistlet/Services/Federation/`.

## Identity

An instance is identified by its **domain**: the host (and port, if not the default) of
`Wistlet:PublicUrl`. Accounts are written `@name@domain`. On first start, each instance makes an
ECDSA P-256 key pair and keeps it in `data/federation-key.pem`.

## Discovery

`GET /.well-known/wistlet`

```json
{ "software": "wistlet", "protocol": 1, "domain": "a.example", "name": "Site name",
  "publicKey": "<SubjectPublicKeyInfo, base64>", "inbox": "/federation/inbox" }
```

An instance talks to another only if this document exists, says `"software": "wistlet"` and
protocol 1, and names the domain it was fetched from. The key is **pinned** on first contact. A
later request signed with a different key is refused until an admin chooses "Forget key".

## Public documents

| Request | Answer |
|---|---|
| `GET /federation/users/{name}` | The profile: name, displayName, dek, pronouns, bio (Markdown), avatar, avatarShape, banner, adultPage, url |
| `GET /federation/users/{name}/posts` | The account's 20 newest posts (not shares or hidden posts) |

Banned accounts and pages hidden from logged-out visitors are not served (404), and their posts
are never sent anywhere.

A **post** is sent as Markdown, never as HTML. The receiver renders it with its own sanitizer, so
the same CSS-crime rules apply everywhere. Fields: `id` (the post's URL on its instance), `author`,
`createdAt`, `editedAt`, `headline`, `markdown`, `tags`, `contentWarnings`, `adult`, `image`,
`audio` {`url`, `title`, `artist`}, and for comments `post` and `parent`. Image, audio, avatar and
banner URLs must be on the sending instance under `/uploads/`, or they are dropped.

## Signed requests

Activities are `POST`ed as JSON to the receiver's `/federation/inbox` (1 MB at most) with:

| Header | Value |
|---|---|
| `Wistlet-Instance` | sender domain |
| `Wistlet-Date` | Unix time in seconds |
| `Wistlet-Digest` | base64 SHA-256 of the body |
| `Wistlet-Signature` | base64 ECDSA-P256-SHA256 signature of the signing string |

The signing string is these lines joined with `\n`:
`wistlet-v1`, `POST`, `/federation/inbox`, receiver domain, sender domain, date, digest.

The receiver checks, in order:
1. The date is within 5 minutes of its clock.
2. The digest matches the body.
3. The sender is a known (or discoverable) Wistlet instance that isn't blocked.
4. The signature verifies with the pinned key.
5. The same signature hasn't been seen in the last 10 minutes.

Naming both domains in the signing string means a request can't be replayed to another instance.

## Activities

```json
{ "type": "...", "actor": "<user name on the sender>", "target": ..., "object": ..., "post": ..., "profile": ... }
```

An instance can only act for its own accounts: the actor is always `actor@sender-domain`.

| type | fields | effect on the receiver |
|---|---|---|
| `follow` | `target`: local user name | The actor follows the local account. 403 if either has blocked the other, 404 if the account doesn't federate. |
| `unfollow` | `target` | Removes the follow. |
| `post` | `post` | Stores or updates a post by the actor. Ignored (202) unless someone on the receiver follows the actor. |
| `comment` | `post` with `post` (a receiver post URL) and `parent` | Adds or updates a comment by the actor on a local post's thread. |
| `delete` | `object`: post URL | Deletes the actor's post or comment. |
| `like` / `unlike` | `object`: a receiver post URL | Adds or removes the actor's like. |
| `profile` | `profile` | Updates the actor's cached profile. |
| `delete-account` | | Removes the actor's account and everything stored for it. |

`follow` is sent straight away, so the person gets an answer. Everything else goes through a queue
(`FederationDeliveries`), retried with backoff (30 s, doubling, up to 6 h, 12 tries). A 4xx answer
other than 408 or 429 is not retried.

## What goes where

- A local post goes to every instance where someone follows its author. Shares stay local.
- Likes and comments on a post from another instance go back to that instance. Comments are not
  passed on to third instances: each instance shows the comments its own users wrote, and the
  original post shows them all.
- Notifications, direct messages and the Explore page stay local.

## Safety

- Federation is off until an admin turns it on (Site settings), and needs `Wistlet:PublicUrl`.
- Outgoing requests go only to public IP addresses, checked on the address actually connected to.
  Redirects aren't followed, and responses are capped at 1 MB with a 15 s timeout.
- Remote text is held to the local limits (length, banned words). Remote accounts can't log in,
  receive direct messages or appear in the People directory.
- Admins can block an instance (Admin → Federation). Nothing is accepted from or sent to it, and
  its content is hidden.

## Testing two instances on one machine

```sh
Wistlet__PublicUrl=http://localhost:6969 Wistlet__Federation__AllowHttp=true \
Wistlet__Federation__AllowPrivateAddresses=true ASPNETCORE_URLS=http://127.0.0.1:6969 dotnet run
```

Start a second one the same way on another port with its own `Wistlet__DataPath`. Turn
federation on in both admin panels, then search for `@name@localhost:6970`. Never use the two
testing options on a public server.
