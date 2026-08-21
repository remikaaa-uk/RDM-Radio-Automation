# RDM HTTP API Documentation

A guide for anyone who wants to build against RDM's HTTP and WebSocket API — external dashboards, automation scripts, alternate control surfaces, integrations with playout/traffic systems, and so on.

> **Not what you're after?** If you want to run logic *inside* RDM itself (triggered by pads, macros, or a schedule), see the separate [JavaScript Scripting Guide](JavaScript_Scripting_Guide.md) — that's an embedded Jint sandbox, unrelated to this HTTP API.

---

## Table of Contents

1. [Overview](#1-overview)
2. [Getting Started](#2-getting-started)
3. [Authentication](#3-authentication)
4. [Conventions](#4-conventions)
5. [WebSocket API (real-time events)](#5-websocket-api-real-time-events)
6. [REST API Reference](#6-rest-api-reference)
   - [6.1 Assets](#61-assets)
   - [6.2 Import & Scan](#62-import--scan)
   - [6.3 Waveform](#63-waveform)
   - [6.4 Playback (main player)](#64-playback-main-player)
   - [6.5 Playlist Items (live queue)](#65-playlist-items-live-queue)
   - [6.6 Saved Playlists](#66-saved-playlists)
   - [6.7 Aux Players](#67-aux-players)
   - [6.8 Cartwall](#68-cartwall)
   - [6.9 Microphone](#69-microphone)
   - [6.10 Recording](#610-recording)
   - [6.11 Encoder / Streaming](#611-encoder--streaming)
   - [6.12 Streams (internet radio assets)](#612-streams-internet-radio-assets)
   - [6.13 Categories, Subcategories & Genres](#613-categories-subcategories--genres)
   - [6.14 Formats](#614-formats)
   - [6.15 Now Playing](#615-now-playing)
   - [6.16 Event Log](#616-event-log)
   - [6.17 Scheduled Events](#617-scheduled-events)
7. [Known Quirks & Inconsistencies](#7-known-quirks--inconsistencies)
8. [Quick Reference: curl Examples](#8-quick-reference-curl-examples)

---

## 1. Overview

RDM (Radio Digital Manager) exposes a REST + WebSocket API covering everything the desktop application itself can do: library management, the main player, the live queue, saved playlists, aux players, the cartwall, the microphone/ducking path, recording, streaming encoders, scheduling, and a real-time event feed.

Important architectural point: **the API is not a separate server process**. It's an ASP.NET Core web app (project `RDM.API`) hosted *in-process* inside the RDM desktop application (`RDM.UI`, built on Avalonia). It only runs while the desktop app is running, on the same machine, and by default only listens on `localhost`. There is no public cloud endpoint, no built-in multi-tenant hosting, and no Swagger/OpenAPI UI shipped — this document is the primary reference.

## 2. Getting Started

- **Base URL:** `http://localhost:9300` by default (configurable — see below).
- **API root path:** every REST endpoint lives under `/api/v1/...`.
- **The app must be running.** Since the API is hosted inside RDM.UI, if the desktop app is closed, the API is unreachable.

### Where the base URL comes from

The port (and, in principle, the host) is read from `rdm.config.json → api.base_url` at startup:

```json
{
  "api": { "base_url": "http://localhost:9300" }
}
```

The port can be changed from the RDM Settings UI (clamped to 1024–65535); this rewrites `api.base_url` in the config file. There is no UI control for the host part — if you need the API reachable from other machines on the network, you'll need to edit `base_url` by hand (and be aware of the authentication implications in the next section before you do).

### A first request

```bash
curl -u operator:yourpassword http://localhost:9300/api/v1/nowplaying
```

If everything's configured, you'll get back a JSON `NowPlayingDto`. If you get a `401`, see [Authentication](#3-authentication) below.

## 3. Authentication

RDM uses **HTTP Basic Authentication** — no OAuth, no API keys, no bearer tokens.

```
Authorization: Basic base64(username:password)
```

Example:

```bash
curl -u operator:yourpassword http://localhost:9300/api/v1/nowplaying
# curl builds the Authorization: Basic ... header for you from -u
```

### How the auth check actually works

A single global middleware (`AuthMiddleware`) gates **every** request, including the WebSocket upgrade — there are no `[Authorize]` attributes anywhere in the codebase; this is the only gate. It checks, in order:

1. **If the "Require login" setting is switched off** (`ApiAuthEnabled = false` in Settings) — every request is treated as authenticated with the **Admin** role. No credentials needed at all.
2. **Else, if "Allow local without login" is on** (`ApiAnonymousLocal = true`) **and** the request comes from the loopback address (`127.0.0.1`/`::1`) — it's treated as authenticated with the **Operator** role, no credentials needed. This deliberately does **not** extend to other machines on your LAN, only to processes on the same machine as RDM itself.
3. **Otherwise, a valid `Authorization: Basic` header is required.** It's checked against two sources, in order:
   - the single fallback API user configured in Settings (username + BCrypt-hashed password) → role **Operator**;
   - the studio's user accounts table (the same Admin/Operator accounts used for the desktop app's multi-user login) → role taken from that user's record.

### Roles

There are exactly two roles: **`Admin`** and **`Operator`**. Almost every endpoint accepts both roles equally — RDM's API is intentionally permissive for day-to-day remote control. A small number of *management* endpoints (as opposed to *operational* ones) are restricted to Admin only; these are called out individually in the reference below. As a summary, Admin-only actions are:

- Creating, updating, or deleting **encoder/streaming profiles** (starting/stopping an existing profile is open to Operators).
- Creating, updating, or deleting **scheduled events** (an Operator *can* still flip the `skip_next` flag on an existing event via `PATCH`, just not touch its `enabled` state or anything else).

### Failure response

Missing or invalid credentials get:

```
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Basic realm="RDM API"
Content-Type: application/json

{ "errorCode": "UNAUTHORIZED", "message": "Valid credentials required.", "traceId": "..." }
```

An authenticated-but-under-privileged request to an Admin-only endpoint gets `403 Forbidden` with the same envelope shape and a message such as *"Only Administrators are allowed to manage streaming profiles."*

## 4. Conventions

### Base path & versioning

Every controller is mounted under **`/api/v1/...`**. There is no other version in use yet; the version segment is a fixed literal, not content-negotiated.

Two endpoints break the pattern deliberately (they live under `/api/v1/track/{id}` — singular — rather than `/api/v1/assets/{id}`): full-detail asset **read** and **update**. Everything else asset-related uses `/api/v1/assets/...`.

### JSON casing

Responses are serialised with **camelCase** property names (ASP.NET Core's default `System.Text.Json` behaviour — nothing custom is configured). Request bodies bind case-insensitively, so sending camelCase is the safe convention on the way in too.

**Two documented exceptions**, both worth remembering:
- The response body of `POST /api/v1/assets/{id}/waveform/request` uses **snake_case** (`asset_id`, not `assetId`) — it was written as a raw anonymous object rather than through a DTO record.
- **All WebSocket event payloads use snake_case** field names (`asset_id`, `duration_ms`, etc.) — see [§5](#5-websocket-api-real-time-events). The outer envelope (`eventId`, `eventType`, `timestamp`) is camelCase; only the inner `payload` object is snake_case.

### Standard error shape

Whenever an endpoint fails — whether it's an explicit validation error or an unhandled exception bubbling out of the server — the response body has this shape:

```
ErrorResponseDto {
  errorCode: string
  message:   string
  traceId:   string
}
```

Unhandled-exception → status/code mapping (from the global error-handling middleware):

| Exception type | Status | `errorCode` |
|---|---|---|
| `DuplicateAssetException` | 409 Conflict | `ASSET_ALREADY_EXISTS` |
| `KeyNotFoundException` | 404 Not Found | `RESOURCE_NOT_FOUND` |
| `ArgumentException` | 400 Bad Request | `BAD_REQUEST` |
| `UnauthorizedAccessException` | 401 Unauthorized | `UNAUTHORIZED` |
| `InvalidOperationException` | 409 Conflict | `BAD_REQUEST` *(status is 409, but the code is still `BAD_REQUEST` — as coded, not a typo on our part)* |
| anything else | 500 Internal Server Error | `INTERNAL_ERROR` *(generic message; no internal details are leaked)* |

Individual endpoints also return their own explicit `errorCode` values for expected failure cases (`ASSET_NOT_FOUND`, `NO_PLAYLIST`, `SLOT_EMPTY`, and so on) — these are listed per-endpoint below.

> One controller doesn't follow this convention — see [§7](#7-known-quirks--inconsistencies).

### Pagination

A handful of list endpoints share a `limit`/`offset` query-parameter convention (rather than page numbers):

| Endpoint | `limit` range | default `limit` | default `offset` |
|---|---|---|---|
| `GET /api/v1/assets` | 1–1000 | 50 | 0 |
| `GET /api/v1/nowplaying/history` | 1–500 | 50 | 0 |
| `GET /api/v1/events/log` | 1–500 | 50 | *(no offset)* |

Out-of-range values return `400 Bad Request`.

## 5. WebSocket API (real-time events)

RDM pushes live events over a plain (non-SignalR) ASP.NET Core WebSocket.

- **Connect to:** `ws://localhost:9300/api/v1/ws` (or `wss://` if you've put TLS in front of it yourself).
- **Auth:** the same `AuthMiddleware` Basic-Auth gate applies to the upgrade request as to any REST call — send your `Authorization` header (or connect from loopback with `ApiAnonymousLocal` enabled).
- **Direction:** effectively **server → client only**. The server reads incoming frames only to detect a Close frame; it does not process any client-sent message content. There is no subscribe/filter protocol — you receive every event.
- **Delivery:** each connection has a bounded internal queue (capacity 50). A slow or stalled client silently **drops the oldest events** once the queue fills — there is no replay or backfill on reconnect, so treat the feed as best-effort and re-sync via REST (e.g. `GET /nowplaying`) after reconnecting.

### Message envelope

```
WebSocketFrameDto {
  eventId:   string   // GUID
  eventType: string   // see table below
  timestamp: string   // ISO-8601 UTC
  payload:   object   // event-specific — snake_case keys, see note in §4
}
```

### Event catalogue

| `eventType` | `payload` fields |
|---|---|
| `TRACK_STARTED` | `asset_id, title, artist, duration_ms, scheduled_at (nullable), vu_offset_db` |
| `TRACK_ENDED` | `asset_id, reason, ended_at` |
| `ASSET_IMPORTED` | `asset_id, title, artist` |
| `LOUDNESS_ANALYZED` | `asset_id, lufs, true_peak` |
| `SCHEDULE_CHANGED` | `event_id, name, change_type ("FIRED"\|"SKIPPED"), result` *(result present only when `change_type` is `FIRED`)* |
| `PLAYLIST_MODE_CHANGED` | `previous_mode, mode` |
| `PLAYLIST_UPDATED` | `playlist_id` |
| `PLAYLIST_STOPPED` | `playlist_id, reason` |
| `DEAD_AIR_WARNING` | `silence_ms, mode` |
| `WAVEFORM_READY` | `asset_id` |
| `PFL_ENDED` | `asset_id` |
| `CART_TRIGGERED` | `slot_id, duration_ms, label` *(label is currently always `null`)* |
| `CART_STOPPED` | `slot_id` |
| `STREAM_META_CHANGED` | `asset_id, stream_title` |

### Example client (JavaScript)

```javascript
const auth = btoa("operator:yourpassword");
const ws = new WebSocket("ws://localhost:9300/api/v1/ws", [], { headers: { Authorization: `Basic ${auth}` } });
// Note: browser WebSocket clients can't set arbitrary headers — use a WS library that
// supports Basic Auth in the URL (ws://user:pass@host:port/...) or a server-side client instead.

ws.onmessage = (msg) => {
  const frame = JSON.parse(msg.data);
  console.log(frame.eventType, frame.payload);
};
```

## 6. REST API Reference

Unless stated otherwise, every endpoint below requires authentication as described in [§3](#3-authentication) and accepts either role.

### 6.1 Assets

Base path: `/api/v1/assets` (plus two absolute overrides at `/api/v1/track/{id}`).

#### `GET /api/v1/assets`
Search/list library assets.

- **Query:** `q?` (text search), `asset_type?`, `format_id?`, `status?` (`ACTIVE`\|`DISABLED`\|`PENDING_REVIEW`\|`ALL`), `genre?`, `subcategory_id?`, `limit` (1–1000, default 50), `offset` (default 0), `sort?`, `sort_dir?` (`desc` or ascending).
- **Response `200`:**
  ```
  AssetSearchEnvelopeDto {
    total: int, limit: int, offset: int
    items: AssetDto[]
  }
  AssetDto {
    assetId, assetType, formatName?, subcategoryName?
    title, artist?, album?, durationMs: uint
    bpm?: decimal, year?: int, rating?: byte, status
    loudnessLufs?: decimal, mood?, gender?, language?, genre?
    playCount: uint, createdAt: DateTime
    cueMarkers?: CueMarkersDto
  }
  ```
- **Errors:** `400` on an out-of-range `limit`/`offset` or an unrecognised `asset_type`/`status`.

#### `GET /api/v1/assets/{id}`
Fetch a single asset (summary shape, same `AssetDto` as above). `404 ASSET_NOT_FOUND` if missing.

#### `GET /api/v1/assets/by-path?path=...`
Look up an asset by its on-disk file path. `400` if `path` is blank; bare `404` if not found.

#### `POST /api/v1/assets/analyze-loudness`
Queue loudness (LUFS) analysis for one or more assets.
- **Body:** `{ assetIds: string[] }` — `400` if empty.
- **Response `202`:** `{ queued: int, message }`.

#### `POST /api/v1/assets/analyze-cue`
Queue automatic cue-point detection.
- **Body:** `{ assetIds: string[], startDb: double, nextStartDb: double, endDb: double }` — `400` if `assetIds` is empty.
- **Response `202`:** `{ queued: int, message }`.

#### `GET /api/v1/track/{id}`
Full detail record for a single asset (everything editable in the track editor). `404 ASSET_NOT_FOUND`.
```
AssetDetailDto {
  assetId, assetType, formatId?, subcategoryId?
  title, artist?, album?, durationMs: uint
  rdmFilePath?, streamUrl?, imagePath?
  bpm?: decimal, year?: int, rating?: byte
  mood?, gender?, language?, genre?, comments?
  isDamaged: bool, isVariableDuration: bool, status
  startDate?, endDate?: DateTime, playLimit?: uint, playCount: uint
  lastPlayedAt?: DateTime
  loudnessLufs?: decimal, loudnessPeak?: decimal
  cueMarkers?: CueMarkersDto
}
CueMarkersDto {  // all double?, in seconds
  start, intro, ramp2, ramp3, outro, startNext, fadeOut, fadeEnd,
  end, hookIn, hookFade, hookOut, loopIn, loopOut, anchor
}
```

#### `PUT /api/v1/track/{id}`
Full update of an asset's metadata (also writes a best-effort `.rdm` sidecar file next to the audio file).
- **Body:** `UpdateAssetRequestDto` — `title` required; `status` required (`ACTIVE`\|`DISABLED`\|`PENDING_REVIEW`); `assetType?` if present must be `Track`\|`Sweeper`\|`InternetStream`; plus `artist?, album?, formatId?, subcategoryId?, bpm?, year?, rating?, mood?, gender?, language?, genre?, comments?, startDate?, endDate?, playLimit?, cueMarkers?, imagePath?, streamUrl?, isVariableDuration?`.
- **Response `200`:** `{ assetId, updatedAt }`. **Errors:** `400` on a blank title/bad enum, `404 ASSET_NOT_FOUND`.

#### `PATCH /api/v1/assets/{id}/status`
Change only the status field.
- **Body:** `{ status }` (`ACTIVE`\|`DISABLED`\|`PENDING_REVIEW`).
- **Response `200`:** `{ assetId, status, updatedAt }`. **Errors:** `400`, `404`.

#### `DELETE /api/v1/assets/{id}`
- **Query:** `deleteFile` (bool, default `false`) — also delete the underlying audio file.
- **Response `200`:** `{ assetId, fileDeleted: bool }`. **Errors:** `404`.

#### `POST /api/v1/assets/batch-delete`
- **Body:** `{ assetIds: string[], deleteFiles: bool }`.
- **Response `200`:** `{ deleted: int, filesDeleted: int }`.

#### `POST /api/v1/assets/purge-orphans`
Removes database rows whose backing file no longer exists on disk. No body.
- **Response `200`:** `{ deleted: int, deletedTitles: string[] }`.

#### `POST /api/v1/assets/optimize`
Runs database maintenance/optimisation. No body.
- **Response `200`:** `{ success: bool, durationMs: long }`.

### 6.2 Import & Scan

Both mounted under `/api/v1/assets`.

#### `POST /api/v1/assets/import`
Import a single file into the library (background job).
- **Body:** `{ filePath, assetType, formatId?, subcategoryId?, readRdm: bool, readMmd: bool, readWfrm: bool, readId3: bool }` — `filePath` required.
- **Response `202`:** `{ importId, status: "QUEUED", filePath }`.

#### `GET /api/v1/assets/import/{importId}`
Poll import job status.
- **Response `200`:** `{ importId, status, assetId?, title?, artist?, completedAt?, isDuplicate: bool }`. **Errors:** `404`.

#### `POST /api/v1/assets/id3/peek`
Read ID3/file tags without importing.
- **Body:** `{ filePath }`. **Errors:** `400` blank path, `404` file not found, `422` tags unreadable.
- **Response `200`:** `{ title?, artist?, album?, year?, bpm?, genre?, durationMs?, pictureBase64?, pictureMimeType? }`.

#### `POST /api/v1/assets/scan`
Scan multiple files for new-track candidates (background job).
- **Body:** `{ filePaths: string[] }` — `400` if empty.
- **Response `202`:** `{ scanId, status: "QUEUED" }`.

#### `GET /api/v1/assets/scan/{scanId}`
Poll scan job status.
- **Response `200`:** `{ scanId, status, done: int, total: int, completedAt? }`. **Errors:** `404`.

#### `GET /api/v1/assets/scan/{scanId}/results`
Fetch results once the scan job has finished.
- **Response `200`:** array of `{ filePath, filename, artist?, title?, durationMs?, folder? }`.
- **Errors:** `404` unknown job, `409 SCAN_NOT_COMPLETED` if still running.

### 6.3 Waveform

Base path: `/api/v1/assets/{id}/waveform`.

#### `GET /api/v1/assets/{id}/waveform`
Download a pre-generated waveform.
- **Response `200`:** `application/octet-stream` — a gzip-compressed, byte-quantised waveform (**not JSON**).
- **Errors:** `404 WAVEFORM_NOT_FOUND` if none has been generated yet — call the endpoint below first.

#### `POST /api/v1/assets/{id}/waveform/request`
Queue waveform generation for an asset.
- **Response `202`:** `{ asset_id, message }` — **note the snake_case**, see [§4](#4-conventions).
- **Errors:** `404 ASSET_NOT_FOUND`, `422 FILE_NOT_ACCESSIBLE` if the audio file isn't reachable from this machine.

### 6.4 Playback (main player)

Base path: `/api/v1/playlist`.

#### `POST /api/v1/playlist/play` · `POST /pause` · `POST /stop`
No body. **Response `200`:** `PlaybackStatusResponseDto { status: "OK", state }`.

#### `POST /api/v1/playlist/loop`
Toggle repeat of the current queue item (playlist-level repeat, not an audio-engine loop point).
- **Body:** `{ enabled: bool }`. **Response:** `PlaybackStatusResponseDto` with `state` `LOOP_ON`/`LOOP_OFF`.

#### `POST /api/v1/playlist/next`
Advance to the next queue item.
- **Response `200`:** `{ status, assetId, title, artist? }`. **Response `204`:** if nothing is now playing (queue exhausted).

#### `POST /api/v1/playlist/reset`
- **Response `200`:** `{ status, positionMs: uint }`.

#### `POST /api/v1/playlist/play/{assetId}`
Jump straight to a specific library asset.
- **Response `200`:** `{ status, assetId, title, artist? }`. **Errors:** `404 ASSET_NOT_FOUND`, `422 ASSET_DAMAGED`.

#### `POST /api/v1/playlist/pfl/{assetId}`
Pre-fade-listen a library asset (cue to headphones/monitor, not the main output).
- **Response `200`:** `PlaybackStatusResponseDto { state: "PFL_PLAYING" }`. **Errors:** bare `404` if the asset or its file is missing.

#### `POST /api/v1/playlist/pfl/file`
PFL an arbitrary file straight from disk.
- **Body:** `{ filePath }`. **Errors:** `404 FILE_NOT_FOUND`.

#### `POST /api/v1/playlist/pfl/stop`
**Response:** `state: "PFL_STOPPED"`.

#### `POST /api/v1/playlist/pfl/seek`
- **Query:** `offset_ms: int`. **Response:** `state: "PFL_SEEK"`.

### 6.5 Playlist Items (live queue)

Base path: `/api/v1/playlist` (shared with §6.4 — different sub-paths).

#### `GET /api/v1/playlist/items`
```
PlaylistItemsEnvelopeDto {
  items: PlaylistItemDto[]
  mode: "AUTO" | "LIVE_ASSIST" | "MANUAL"
  state: "IDLE" | "PLAYING" | "PAUSED"
  currentItemId?
}
PlaylistItemDto {
  itemId, assetId?, position: uint, itemType: "ASSET" | "DUMMY"
  externalFilePath?, dummyLabel?, dummyNote?, dummyDurationMs?: uint
  crossfadeMs?: uint, leadInMs?: int, trimStartMs?: uint, trimEndMs?: uint
  segueType: "AUTO" | "MANUAL" | "TIMED", scheduledAt?: DateTime
  autoLinkNext: bool
  title?, artist?, durationMs?: uint, assetType?, formatName?, status?
  isDamaged?: bool, comments?, cueMarkers?: CueMarkersDto, volumeEnvelope?
}
```

#### `POST /api/v1/playlist/items`
Add a library asset to the queue.
- **Body:** `{ assetId?, externalFilePath?, position: int, itemType: string = "ASSET" }` — exactly one of `assetId`/`externalFilePath` must be set. If `externalFilePath` is given, the file is imported first.
- **Response `200`:** `{ itemId, position: uint }`.
- **Errors:** `400` if neither/both of `assetId`/`externalFilePath` are set, `404 ASSET_NOT_FOUND`, `409 NO_PLAYLIST`, `422 IMPORT_FAILED`.

#### `POST /api/v1/playlist/items/external`
Add a file to the queue that plays straight from disk without ever being imported into the library (title/artist/duration are read from ID3 tags server-side if omitted).
- **Body:** `{ filePath, position: int, title?, artist?, durationMs? }`.
- **Errors:** `400` blank path, `404 FILE_NOT_FOUND`, `409 NO_PLAYLIST`.

#### `DELETE /api/v1/playlist/items`
Clear the entire queue. **Response:** `204`.

#### `DELETE /api/v1/playlist/items/{itemId}`
Remove a single queue item. **Response:** `204`. **Errors:** `409 NO_PLAYLIST`.

#### `DELETE /api/v1/playlist/current`
Remove the currently-playing item. **Response:** `204`.

#### `PATCH /api/v1/playlist/items/reorder`
- **Body:** `{ itemId, newPosition: int }`. **Response:** `204`.

#### `PATCH /api/v1/playlist/items/{itemId}`
Partial update of a queue item's playout parameters.
- **Body (all optional):** `{ crossfadeMs?, leadInMs?, trimStartMs?, trimEndMs?, segueType?, autoLinkNext?, volumeEnvelope? }`. **Response:** `204`.

#### `POST /api/v1/playlist/mode`
Switch playlist mode.
- **Body:** `{ mode }` (`AUTO`\|`LIVE_ASSIST`\|`MANUAL`). **Response:** `PlaybackStatusResponseDto`.

### 6.6 Saved Playlists

Base path: `/api/v1/playlists`.

#### `GET /api/v1/playlists`
```
SavedPlaylistsEnvelopeDto { items: SavedPlaylistSummaryDto[] }
SavedPlaylistSummaryDto { playlistId, name, createdAt, itemCount: int }
```

#### `GET /api/v1/playlists/{playlistId}`
```
SavedPlaylistDetailDto { playlistId, name, createdAt, items: PlaylistItemDto[] }
```
(`cueMarkers` is always `null` on items returned here.) **Errors:** `404 PLAYLIST_NOT_FOUND`.

#### `POST /api/v1/playlists`
Save the current (or a constructed) list of items as a named playlist.
- **Body:** `{ name, items: PlaylistItemSaveDto[] }` — `name` required.
  ```
  PlaylistItemSaveDto {
    assetId?, itemType, dummyLabel?, dummyNote?, dummyDurationMs?: uint
    crossfadeMs?: uint, trimStartMs?: uint, trimEndMs?: uint
    segueType, autoLinkNext: bool, volumeEnvelope?
  }
  ```
- **Response `200`:** `{ playlistId, name, createdAt }`.

#### `PUT /api/v1/playlists/{playlistId}`
Full replace (same body as `POST`; existing items are cleared and re-added). **Errors:** `404`.

#### `DELETE /api/v1/playlists/{playlistId}`
**Response:** `204`. **Errors:** `404`.

### 6.7 Aux Players

Base path: `/api/v1/aux/{index}` — `index` is `0`–`7` (400 `Aux index must be between 0 and 7.` otherwise).

All responses are `PlaybackStatusResponseDto { status: "OK", state }` unless noted.

| Endpoint | Body | Resulting `state` |
|---|---|---|
| `POST /{index}/load` | `{ filePath }` | → `AuxLoadedDto { durationMs, waveformPath, startMs, endMs }` |
| `POST /{index}/play` | — | `ACTIVE` |
| `POST /{index}/pause` | — | `PAUSED` |
| `POST /{index}/stop` | — | `IDLE` (uses the configured aux fade-out) |
| `POST /{index}/eject` | — | `IDLE` |
| `POST /{index}/loop` | `{ enabled: bool }` | `LOOP_ON` / `LOOP_OFF` |
| `POST /{index}/volume` | `{ gain: float }` (0.0–1.0 linear) | `ACTIVE` |
| `POST /{index}/route` | `{ on: bool, pfl: bool }` | `ACTIVE` |

### 6.8 Cartwall

Base path: `/api/v1` — note the routes themselves carry the resource name (`cartwalls`, `cartwall`), they are **not** further nested under `/api/v1/cartwalls`.

#### `GET /api/v1/cartwalls`
Lists every cartwall "page". Self-healing: if the studio has none, a default page is created automatically; slot rows are auto-provisioned up to the configured slots-per-page.
```
CartwallsEnvelopeDto { items: CartwallDto[] }
CartwallDto { cartwallId, name, pageOrder: int, hotkey?, slots: CartSlotDto[] }
CartSlotDto {
  slotId, cartwallId, assetId?, slotNumber: int
  label?, color? ("#RRGGBB"), hotkey?, loop: bool
  fadeoutMs?: uint, outputGainDb: decimal
  title?, artist?, durationMs?: uint   // denormalised from the assigned asset
}
```

#### `POST /api/v1/cartwalls`
- **Body:** `{ name }` — `400` if blank. **Response:** a new `CartwallDto` with empty `slots`.

#### `DELETE /api/v1/cartwalls/{cartwallId}`
Deletes a page and renumbers the remaining pages' order.
- **Errors:** `422 LAST_CARTWALL` (can't delete the only page), `404 CARTWALL_NOT_FOUND`. **Response:** `204`.

#### `PATCH /api/v1/cartwalls/{cartwallId}`
- **Body:** `{ name?, pageOrder? }`. **Response:** `204`. **Errors:** `400` (null body), `404`.

#### `POST /api/v1/cartwall/{slotId}/trigger`
Play a cartwall slot (singular "cartwall" in the path, by design).
- **Response `200`:** `{ slotId, status: "TRIGGERED", durationMs? }`.
- **Errors:** `404 SLOT_NOT_FOUND` / `ASSET_NOT_FOUND`, `422 SLOT_EMPTY` / `ASSET_NO_FILE` / `ASSET_DAMAGED`.

#### `POST /api/v1/cartwall/{slotId}/stop`
- **Response `200`:** `{ slotId, status: "STOPPED", durationMs: null }`. **Errors:** `404 SLOT_NOT_FOUND`.

#### `PATCH /api/v1/cartwall/{slotId}`
Assign/edit a slot.
- **Body:** `{ assetId?, label?, color?, hotkey?, loop?, clearAsset: bool = false }`. **Response:** `204`. **Errors:** `400`, `404 SLOT_NOT_FOUND`.

#### `POST /api/v1/cartwalls/{cartwallId}/mode`
- **Body:** `{ mode }` (`ON`\|`PFL`\|`OFF`, case-insensitive). **Response:** `PlaybackStatusResponseDto`. **Errors:** `400`, `404 CARTWALL_NOT_FOUND`.

### 6.9 Microphone

Base path: `/api/v1/mic`.

> This is the mic **ducking trigger**, matching the desktop app's mic button — not a raw gain/level control. See the project's ducking invariants if you're integrating hardware: ducking is button-triggered only, there is no voice-activity detection.

#### `POST /api/v1/mic/start` · `POST /api/v1/mic/stop`
**Response:** `PlaybackStatusResponseDto { state: "ACTIVE" | "IDLE" }`.

#### `GET /api/v1/mic/level`
Live VU meter reading. **Response:** `{ levelDb: double }`.

#### `GET /api/v1/mic/status`
**Response:** `{ isActive: bool }`.

#### `GET /api/v1/mic/fx`
List the effects chain. **Response:** array of `{ slotId, fxType, parameters: Record<string, float> }`.

#### `GET /api/v1/mic/fx/params/{fxType}`
Get the valid parameter ranges for an effect type (useful for building a parameter editor UI).
- **Response:** array of `{ key, min: float, max: float, default: float, unit }`. **Errors:** `400` (unknown `fxType`).

#### `PUT /api/v1/mic/fx/{slotId}`
- **Body:** `{ parameters: Record<string, float> }`. **Response:** `204`. **Errors:** `404` (slot not found).

#### `POST /api/v1/mic/fx`
- **Body:** `{ fxType }`. **Response:** `{ slotId: int }`. **Errors:** `400` (unknown `fxType`).

#### `DELETE /api/v1/mic/fx/{slotId}`
**Response:** `204`.

#### `GET /api/v1/mic/vst`
List loaded VST plugins on the mic path. **Response:** array of `{ slotId, pluginName, dllPath }`.

#### `POST /api/v1/mic/vst`
- **Body:** `{ dllPath }`. **Response:** `{ slotId: int }`.

#### `DELETE /api/v1/mic/vst/{slotId}`
**Response:** `204`.

> ⚠️ This controller's error responses use a different shape/casing than the rest of the API — see [§7](#7-known-quirks--inconsistencies).

### 6.10 Recording

Base path: `/api/v1/recording`. Records the program bus; one recording at a time; open to any authenticated user by design (no Admin restriction).

#### `POST /api/v1/recording/start`
- **Body:** `{ directory, format: string = "MP3", bitrateKbps: int = 192, sampleRateHz?, channels: byte = 2, namePrefix? }`.
- **Validation:** `directory` required; `format` must be a known encoder format; bitrate 32–320; channels 1 or 2.
- **Errors:** `400` on the above, `422 FORMAT_UNAVAILABLE` (codec not installed), `422 RECORDING_FAILED` (e.g. unwritable folder).
- **Response:** `RecordingStatusDto`.

#### `POST /api/v1/recording/stop`
**Response:** `RecordingStatusDto`.

#### `GET /api/v1/recording/status`
```
RecordingStatusDto { state, filePath?, startedAt?: DateTime, error?, bytesWritten: long }
```

### 6.11 Encoder / Streaming

Base path: `/api/v1/encoder`. **Profile management (create/update/delete) is Admin-only**; starting, stopping, and reading status is open to any authenticated user.

#### `GET /api/v1/encoder`
**Response:** `{ profiles: EncoderProfileDto[] }`.
```
EncoderProfileDto {
  profileId, name, format, bitrateKbps: int, sampleRateHz?, channels: byte
  serverType, host, port: int, mount?, username?, streamId?
  hasPassword: bool           // the password itself is never returned, ever
  useSsl: bool, usePut: bool, streamName?, genre?, streamUrl?, description?
  isPublic: bool, enabled: bool, armed: bool, autoStart: bool
  reconnectDelaySeconds: int, titleMode, titleText?
}
```

#### `GET /api/v1/encoder/{id}`
**Errors:** `404 PROFILE_NOT_FOUND`.

#### `POST /api/v1/encoder` — **Admin only** (`403` otherwise)
- **Body `EncoderProfileCreateDto`:** `{ name, format, bitrateKbps, serverType, host, port, mount?, username?, streamId?, password?, sampleRateHz?, channels = 2, useSsl = false, usePut = false, streamName?, genre?, streamUrl?, description?, isPublic = false, enabled = true, armed = false, autoStart = false, reconnectDelaySeconds = 10, titleMode = "NOW_PLAYING", titleText? }`.
- **Validation (`400` unless noted):** `name` required; `format` must be a known encoder format (`422 FORMAT_UNAVAILABLE` if the codec add-on isn't installed); bitrate 32–320; channels 1 or 2; `serverType` one of `SHOUTCAST`\|`SHOUTCAST2`\|`ICECAST`; `host` required; `port` 1–65535; `reconnectDelaySeconds` 2–1800; Icecast requires `mount`; `titleMode` one of `NOW_PLAYING`\|`STATIC`\|`NONE`, and `STATIC` requires non-blank `titleText`.
- The password is sent **in plain text over this call** (protect the connection — it's `localhost` by default for a reason) and is encrypted at rest on the server before storage; it is never echoed back in any response.
- **Response `200`:** `{ profileId }`.

#### `PUT /api/v1/encoder/{id}` — **Admin only**
Same body/validation as `POST`. Passing `password: null` leaves the stored password unchanged; passing `""` explicitly clears it. **Errors:** `404`.

#### `DELETE /api/v1/encoder/{id}` — **Admin only**
Stops any running session first, then deletes. **Response:** `204`. **Errors:** `404`.

#### `POST /api/v1/encoder/{id}/start`
- **Errors:** `404 PROFILE_NOT_FOUND`, `422 PROFILE_DISABLED`, `422 FORMAT_UNAVAILABLE`, `422 PASSWORD_UNREADABLE` (the stored password is machine-bound; a profile copied from another PC can't be decrypted — you'll need to re-enter it).
- **Response:** `EncoderStatusDto`.

#### `POST /api/v1/encoder/{id}/stop`
Always succeeds (synthesises a `STOPPED` status even if nothing was running). **Response:** `EncoderStatusDto`.

#### `POST /api/v1/encoder/start-armed`
Starts every profile flagged as armed/auto-start; one failing profile doesn't block the rest. **Response:** `{ statuses: EncoderStatusDto[] }`.

#### `POST /api/v1/encoder/stop-all`
Stops every running session. **Response:** `{ statuses: EncoderStatusDto[] }`.

#### `GET /api/v1/encoder/status`
All current sessions. **Response:** `{ statuses: EncoderStatusDto[] }`.

#### `GET /api/v1/encoder/{id}/status`
```
EncoderStatusDto {
  profileId, profileName, state, error?
  connectedAt?: DateTime, retryAttempt: int, nextRetryAt?: DateTime
  listenerCount?: int
}
```
**Errors:** `404 SESSION_NOT_FOUND` (the profile has never been started).

### 6.12 Streams (internet radio assets)

Base path: `/api/v1/streams`.

#### `POST /api/v1/streams`
Registers an internet radio stream as a library asset.
- **Body:** `{ name, streamUrl }` — `name` required; `streamUrl` must start with `http://` or `https://`.
- Deduplicated by a checksum of the URL: a duplicate usually returns the existing record with `200`, or `409 DUPLICATE` on a race.
- **Response `201`** (`Location: /api/v1/assets/{id}`): `{ assetId, name, streamUrl }`.

### 6.13 Categories, Subcategories & Genres

Base path: `/api/v1/categories` (plus `/genres` and `subcategories/{id}` under the same prefix). "Categories" here are backed by the same underlying table as [Formats](#614-formats) — see [§7](#7-known-quirks--inconsistencies).

#### `GET /api/v1/categories`
**Response `200`:** a **bare array** `CategoryDto[]` — `{ formatId, name, description? }` (not wrapped in an envelope, unlike most list endpoints).

#### `POST /api/v1/categories`
- **Body:** `{ name }` — `400` if blank. **Response `201`:** `{ formatId, name }`.

#### `PUT /api/v1/categories/{id}`
- **Body:** `{ name }`. **Errors:** `400`, `404`.

#### `DELETE /api/v1/categories/{id}`
If assets still reference the category, this does **not** error — it returns `200` with `{ formatId, deleted: false, reason: "Category has N assigned track(s)..." }`. Otherwise `{ formatId, deleted: true, reason: null }`. **Errors:** `404` if the category itself doesn't exist.

#### `GET /api/v1/categories/{id}/subcategories`
**Response:** `{ formatId, items: SubcategoryDto[] }`; `SubcategoryDto { subcategoryId, formatId, name, sortOrder: int }`.

#### `POST /api/v1/categories/{id}/subcategories`
- **Body:** `{ name }`. **Response `201`:** `{ subcategoryId, formatId, name }`. **Errors:** `400`, `404` (parent category missing).

#### `PUT /api/v1/categories/subcategories/{subcategoryId}`
- **Body:** `{ name }`. **Response:** `{ subcategoryId, formatId, name }` — note `formatId` currently comes back as an empty string here rather than the real parent ID; don't rely on it from this particular call.

#### `DELETE /api/v1/categories/subcategories/{subcategoryId}`
Same soft-refusal behaviour as category delete when assets still reference it. **Response:** `{ subcategoryId, deleted: bool, reason? }`.

#### `GET /api/v1/categories/genres`
**Response:** `{ items: GenreDto[] }`; `GenreDto { genreId, name, sortOrder: int }`.

#### `POST /api/v1/categories/genres`
- **Body:** `{ name }`. **Response `201`:** `{ genreId, name }`.

#### `PUT /api/v1/categories/genres/{genreId}`
- **Body:** `{ name }`. **Response:** `{ genreId, name }`.

#### `DELETE /api/v1/categories/genres/{genreId}`
**Response:** `200` with an **empty body** — unlike the category/subcategory deletes, there's no confirmation DTO here.

### 6.14 Formats

Base path: `/api/v1/formats`.

#### `GET /api/v1/formats`
Read-only view of the same table [Categories](#613-categories-subcategories--genres) manages.
**Response:** `{ items: AssetFormatDto[] }`; `AssetFormatDto { formatId, name, description? }`.

### 6.15 Now Playing

Base path: `/api/v1/nowplaying`.

#### `GET /api/v1/nowplaying`
```
NowPlayingDto {
  nowPlaying?: CurrentTrackDto
  nextTrack?: NextTrackDto
  playlistMode, state, loopCurrent: bool
}
CurrentTrackDto {
  assetId, title, artist?, durationMs: uint
  positionMs: uint, remainingMs: uint, startedAt?: DateTime
  cueMarkers?: CueMarkersDto
}
NextTrackDto { assetId, title, artist?, durationMs: uint, scheduledAt?: DateTime }
```

#### `GET /api/v1/nowplaying/next`
**Response `200`:** `NextTrackDto`. **Response `204`:** if there's no resolvable next item.

#### `GET /api/v1/nowplaying/history`
- **Query:** `limit` (1–500, default 50), `offset` (default 0).
- **Response:** `{ history: PlayoutLogDto[], total: int }`; `PlayoutLogDto { assetId?, title?, artist?, startedAt: DateTime, endedAt?: DateTime, sourceType }`.

### 6.16 Event Log

Base path: `/api/v1/events/log`.

#### `GET /api/v1/events/log`
- **Query:** `limit` (1–500, default 50).
- **Response:** `{ items: EventLogEntryDto[] }`; `EventLogEntryDto { eventId, eventName, firedAt: DateTime, result: "SUCCESS" | "PARTIAL" | "FAILED" }`.
  > As currently implemented, `result` is always reported as `"SUCCESS"` — per-entry outcome tracking doesn't appear to be fully wired up yet. Don't rely on this field to detect failures.

### 6.17 Scheduled Events

Base path: `/api/v1/events` (distinct sub-paths from §6.16, no collision). **Create/update/delete are Admin-only.**

#### `GET /api/v1/events`
```
ScheduledEventResponseEnvelopeDto { items: ScheduledEventDto[] }
ScheduledEventDto {
  eventId, name, eventType, category, enabled: bool
  eventHour?: string ("hh:mm:ss"), days: string[], hours: int[]
  smartTiming: bool, actions: ScheduledEventActionDto[]
  lastFiredAt?: DateTime, skipNext: bool
  onlyOnDate?: string ("yyyy-MM-dd")   // only set for ONE_TIME events
}
ScheduledEventActionDto { type: string, payload: object }  // shape depends on `type`
```

#### `GET /api/v1/events/{id}`
**Errors:** `404 EVENT_NOT_FOUND`.

#### `POST /api/v1/events` — **Admin only**
- **Body:** `{ name, eventType, category, enabled, eventHour?, days: string[], hours: int[], smartTiming, actions: ScheduledEventActionDto[], onlyOnDate? }` — `name` required; `eventType` must be a known type; `eventHour`, if given, must parse as `hh:mm:ss`; `onlyOnDate` is **required** when `eventType` is `ONE_TIME`.
- **Response `201`:** `{ eventId, name, createdAt }`.

#### `PUT /api/v1/events/{id}` — **Admin only**
Same body/validation as `POST`, full replace. **Errors:** `404`.

#### `PATCH /api/v1/events/{id}`
Partial update — `{ enabled?: bool, skipNext?: bool }`. **Operators may only set `skipNext`**: an Operator request that includes `enabled` gets `403`. **Errors:** `400` (null body), `404`.

#### `DELETE /api/v1/events/{id}` — **Admin only**
**Response:** `204`. **Errors:** `404`.

#### `GET /api/v1/events/next`
**Response `200`:** `{ eventId, name, firesAt: DateTime, remainingMs: uint, skipNext: bool }`. **Response `204`:** nothing scheduled.

---

## 7. Known Quirks & Inconsistencies

The API grew organically alongside the desktop app, so a few corners aren't perfectly uniform. None of these are bugs that will be silently "fixed" out from under you without notice, but they're worth knowing about up front:

1. **`POST /api/v1/assets/{id}/waveform/request`** returns `{ asset_id, message }` in snake_case, while virtually everything else in the REST API is camelCase.
2. **All WebSocket event `payload` objects are snake_case** (`asset_id`, `duration_ms`, ...); only the outer envelope (`eventId`/`eventType`/`timestamp`) is camelCase.
3. **`MicController`'s error responses** use a different `errorCode` style (`InvalidFxType`, `FxSlotNotFound` — PascalCase-ish) and always send an empty `traceId`, unlike the `SCREAMING_SNAKE_CASE` codes and populated trace IDs everywhere else.
4. **`GET /api/v1/categories`** returns a bare JSON array rather than an `{ items: [...] }` envelope, and **`DELETE /api/v1/categories/genres/{id}`** returns `200` with no body at all — both differ from their sibling endpoints in the same controller.
5. **Categories and Formats overlap:** `/api/v1/categories` (read/write) and `/api/v1/formats` (read-only) both operate on the same underlying table. If you're writing an integration, treat `/categories` as the source of truth and `/formats` as a convenience read alias.
6. **`PUT /api/v1/categories/subcategories/{id}`** currently returns an empty string for `formatId` in its response rather than the real parent category ID — don't parse that field from this specific call.
7. There is **no OpenAPI/Swagger endpoint** anywhere in the application — this document is the authoritative reference; if in doubt, the controller source under `src/RDM.API/Controllers/` is the ground truth.

## 8. Quick Reference: curl Examples

Replace `operator:yourpassword` with real credentials (or drop `-u ...` entirely if you've enabled anonymous local access and you're calling from the same machine).

```bash
# What's playing right now?
curl -u operator:yourpassword http://localhost:9300/api/v1/nowplaying

# Start the main player
curl -u operator:yourpassword -X POST http://localhost:9300/api/v1/playlist/play

# Search the library for tracks by an artist
curl -u operator:yourpassword "http://localhost:9300/api/v1/assets?q=Coldplay&limit=20"

# Trigger cartwall slot with ID "abc123"
curl -u operator:yourpassword -X POST http://localhost:9300/api/v1/cartwall/abc123/trigger

# Turn the mic on (ducks the music bed)
curl -u operator:yourpassword -X POST http://localhost:9300/api/v1/mic/start

# Queue an internet radio stream as a library asset
curl -u operator:yourpassword -X POST http://localhost:9300/api/v1/streams \
  -H "Content-Type: application/json" \
  -d '{"name":"BBC Radio 1","streamUrl":"https://stream.example.com/radio1"}'

# Add a track to the live queue
curl -u operator:yourpassword -X POST http://localhost:9300/api/v1/playlist/items \
  -H "Content-Type: application/json" \
  -d '{"assetId":"<asset-guid>","position":0,"itemType":"ASSET"}'

# Poll an import job until it finishes
curl -u operator:yourpassword -X POST http://localhost:9300/api/v1/assets/import \
  -H "Content-Type: application/json" \
  -d '{"filePath":"C:\\Music\\new_track.mp3","assetType":"Track","readId3":true}'
# → { "importId": "...", "status": "QUEUED", ... }
curl -u operator:yourpassword http://localhost:9300/api/v1/assets/import/<importId>
```

---

**Last updated**: 2026-08-18
