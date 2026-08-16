# Media timecode synchronization API

This document describes the HTTP API used by XivMediaPlayer clients to share
media playback state. A Discord bot can use the same API to act as a DJ or to
observe a room. The default server URL shipped by the plugin is
`http://24.77.70.65:5000`; users may configure a different base URL.

## Room identity

`locationKey` is the room's stable synchronization key. Use the exact key
reported by the client/server; do not derive it from a display name. URL-encode
it in the request path.

## Read current state

```http
GET /api/rooms/{locationKey}/media
```

`200 OK` returns the current state. `404 Not Found` means no media state exists
for that location.

Example:

```json
{
  "locationKey": "house_57_1376_7_27_0_16045533961846811",
  "currentUrl": "https://example.invalid/video.mp4",
  "timecodeMs": 12500,
  "isPlaying": true,
  "timestampUtc": "2026-08-16T12:00:00Z",
  "playlistJson": "[\"https://example.invalid/next.mp4\"]",
  "ownerId": "8a3d3f3d-6ab3-4f62-a0b9-2b2b4e9ed6d4",
  "durationMs": 180000,
  "dataAgeMs": 842,
  "idleTimeMs": 1200
}
```

The server calculates `dataAgeMs`; clients should calculate the current
playhead as:

```text
effectiveTimeMs = isPlaying ? timecodeMs + dataAgeMs : timecodeMs
```

`timestampUtc`, `dataAgeMs`, and `idleTimeMs` are server-owned values. Do not
use a client's wall clock to advance playback.

## Publish state / control playback

```http
POST /api/rooms/{locationKey}/media
Content-Type: application/json
```

The body uses the same fields as the read response. The minimum useful body is:

```json
{
  "currentUrl": "https://example.invalid/video.mp4",
  "timecodeMs": 12500,
  "isPlaying": true,
  "playlistJson": "[]",
  "ownerId": "8a3d3f3d-6ab3-4f62-a0b9-2b2b4e9ed6d4",
  "durationMs": 180000,
  "isBackgroundSync": false,
  "bypassLock": false
}
```

The server overwrites `locationKey` from the URL and stamps `timestampUtc`
with server UTC. `isBackgroundSync` and `bypassLock` are control hints, not
persistent state fields.

Control operations are represented by state updates:

- Play/resume: `isPlaying: true`, with the desired `timecodeMs`.
- Pause: `isPlaying: false`, with the pause position in `timecodeMs`.
- Seek: preserve `currentUrl`, set `timecodeMs`, and set `isPlaying` to the
  desired post-seek state.
- Stop: send an empty `currentUrl` using a foreground update.
- Queue: `playlistJson` is a JSON-encoded array of URL strings. The current URL
  is separate from the queue.

Use integer milliseconds. `durationMs` may be `null` when unknown (especially
for live streams). Live streams should generally not be timecode-controlled.

## Ownership and responses

Every controller should persist a stable random `ownerId` (UUID) and reuse it.
The current media owner is returned as `ownerId`.

- `200 OK`: accepted; use the returned state as authoritative.
- `400 Bad Request`: URL or playlist is rejected, commonly by server
  blacklist policy.
- `403 Forbidden`: a TV/media lock prevents this owner from controlling the
  room.
- `409 Conflict`: a background update arrived from a client that is no longer
  the owner. Stop background heartbeats and re-read state.

Foreground updates (`isBackgroundSync: false`) are user-intent commands. A
bot should use them when a Discord command is explicitly issued. Background
updates are periodic heartbeats and must not overwrite a newer owner.

## Recommended bot loop

1. `GET` the room state before issuing a command.
2. For a command, use the returned `ownerId` only as an observation; send the
   bot's own stable `ownerId` in the POST body.
3. POST the command as a foreground update.
4. Treat the returned JSON as authoritative and report `403`/`409` clearly.
5. For display-only status, poll `GET` about every 2–5 seconds. Derive the
   playhead using `dataAgeMs`; do not repeatedly POST just to keep time moving.

The plugin itself polls room media roughly every ten seconds and corrects
seekable VODs when drift exceeds about 2.5 seconds. A bot does not need to
imitate that polling cadence to control playback.

## Server clock

```http
GET /api/rooms/time
```

Returns Unix UTC milliseconds as a JSON number. This is useful for measuring
client/server clock offset, although normal playback display should use the
`dataAgeMs` supplied by the media-state endpoint.

## Security and compatibility notes

The current API has no bot-specific authentication layer. Anyone who can reach
the server and knows a location key can attempt control, subject to room/TV
locks. Deployments should protect the API at the reverse proxy or add bot
authentication before exposing it publicly. Do not log media URLs containing
API keys.

The API transports original media URLs, not the plugin's local VLC proxy URLs
(`127.0.0.1` URLs are ephemeral and must never be shared).
