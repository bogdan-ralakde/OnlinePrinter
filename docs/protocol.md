# Relay HTTP protocol

Everything below is implemented by `src/LabelBridge.Core/RelayServer.cs` and used by
`RelaySender` / `RelayPoller` in `Transports.cs`. You only need this if you want to integrate
another system, script the relay, or debug with `curl`.

- Base URL: `https://print.example.com` (or `http://host:8080` on a trusted LAN)
- Bodies are raw printer bytes (`application/octet-stream`) or small JSON objects
- All paths are under `/api/v1`

## Headers

| Header | Used by | Meaning |
| --- | --- | --- |
| `X-LB-Token` | host, client | channel token (host token or client token) |
| `X-LB-Admin` | admin | relay admin secret, only for channel creation |
| `X-LB-Job-Id` | client → relay, relay → host | opaque job id, `[A-Za-z0-9_-]{8,64}` |
| `X-LB-Job-Name` | client → relay, relay → host | base64url-encoded UTF-8 label name |

Tokens are compared in constant time against SHA-256 hashes stored in `channel.json`. There are two
independent tokens per channel, so a client cannot ack, delete or inspect the queue.

## Endpoints

### `GET /health`

No auth. Plain text: `ok channels=<n> jobs=<accepted since start> uptime=<h>h<mm>m`

### `GET /`

No auth. Small HTML status page.

### `POST /api/v1/channels`  *(admin)*

Creates a printer channel. Requires `X-LB-Admin` when the relay was started with `--admin`.

```bash
curl -X POST -H "X-LB-Admin: $SECRET" -H 'Content-Type: application/json' \
     -d '{"name":"zd420-office"}' https://print.example.com/api/v1/channels
```

```json
{ "channelId": "e0c71e59caf7652689975911",
  "hostToken": "ixcncgGXoiUH91zRZF9qG8jRn…",
  "clientToken": "i-Q_TTopdidMAAi-58wgLOHHL8pcXEcPqYyqgfBKFCM" }
```

`201` on success, `401` bad/missing admin secret, `507` channel limit reached.

### `POST /api/v1/channels/{channelId}/jobs`  *(client)*

Upload a job. Body = raw bytes (max `LB_MAX_MB`, default 64 MB).

```bash
curl -X POST -H "X-LB-Token: $CLIENT_TOKEN" \
     -H 'X-LB-Job-Id: 6f1c2a…' -H 'X-LB-Job-Name: VGVzdCBsYWJlbA' \
     --data-binary @label.zpl \
     https://print.example.com/api/v1/channels/$CH/jobs
```

`202 {"id":"…"}` accepted, `400` empty body, `401` bad token, `507` queue full or body too large.

The job is fsynced to disk before the reply, so a relay crash cannot lose an accepted job.

### `GET /api/v1/channels/{channelId}/jobs/next?wait=25`  *(host)*

Long poll. Returns the oldest job and moves it to `inflight`.

- `200` + body = job bytes, `X-LB-Job-Id` and `X-LB-Job-Name` set
- `204` nothing within `wait` seconds (`wait` clamped to 0…50)
- `401` bad host token

Without an ack, the job returns to the queue after a relay restart or 10 minutes. If the host
disconnects mid-body, the relay requeues it immediately.

### `POST /api/v1/channels/{channelId}/jobs/{jobId}/ack`  *(host)*

Confirms that the job reached the printer; deletes it from `inflight`. Idempotent: `200 ok`.

### `GET /api/v1/channels/{channelId}/status`  *(host)*

```json
{ "pending": 3, "oldestUtc": "2026-11-18T09:12:44.1234567Z", "channelId": "e0c7…" }
```

### `DELETE /api/v1/channels/{channelId}`  *(host)*

Deletes the channel, its queued jobs and both tokens. `200 deleted` / `404`.

## Status codes

| Code | Meaning |
| --- | --- |
| `200` / `202` / `204` | success (204 = empty long poll) |
| `400` | malformed request (empty job) |
| `401` | missing/invalid token or admin secret |
| `404` | unknown path or channel |
| `405` | wrong method |
| `500` | unexpected relay error (details in the relay log) |
| `507` | queue full / payload too large |

## Pairing code

The `LB1:` code is base64url-encoded JSON wrapped at 96 characters:

```json
{
  "Mode": "relay",
  "RelayUrl": "https://print.example.com",
  "ChannelId": "e0c71e59caf7652689975911",
  "ClientToken": "i-Q_TTopdidMAAi-58wgLOHHL8pcXEcPqYyqgfBKFCM",
  "PrinterName": "LabelBridge",
  "Label": "PRINT-PC",
  "IssuedUtc": "2026-11-18T09:10:00Z"
}
```

Direct mode uses `Mode:"direct"`, `Host`, `Port`, `Key` (32-byte base64url PSK) and
`Fingerprint` (SHA-256 of the host certificate, base64url) instead of the relay fields.

Production tip: generate the code on the host, send it over a channel you trust, and rotate the
channel if it leaks.
