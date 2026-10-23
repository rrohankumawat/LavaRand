# LavaRand: lava-lamp-style randomness in ASP.NET Core

## Run it

```bash
dotnet run
```

Open the URL printed in the console (for example http://localhost:5000).

## API

| Endpoint | What it does |
|---|---|
| `GET /api/random/bytes?length=32` | Random bytes as hex and base64 (1 to 1024) |
| `GET /api/random/int?min=1&max=7` | Unbiased integer in [min, max) |
| `GET /api/random/token?length=32` | URL-safe token |
| `POST /api/entropy/frame` | Push raw bytes (camera frame etc.) into the pool |
| `GET /api/entropy/stats` | Samples mixed, bytes mixed, per-source counts |

## Entropy sources

- Server: timing jitter, GC and thread pool counters, clock, request timings
- Browser: virtual lamp pixels, pointer timings, optional webcam frames
- Optional (appsettings.json, `Entropy` section):
  - `CameraSnapshotUrl`: an IP camera snapshot URL pointed at a real lamp
  - `UseDrandBeacon`: mix in the public drand beacon

## How it works

1. Every sample is folded in: `state = SHA-512(state || counter || sample)`.
2. Every output is `HKDF(state, salt = fresh OS randomness)`, so the OS CSPRNG always participates.
3. After each output the state ratchets forward, so a later leak can't rebuild earlier outputs.

This is a learning project and a defense-in-depth layer. For real security, `RandomNumberGenerator` on its own is already the right primitive.
