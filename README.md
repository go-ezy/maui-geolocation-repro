# MAUI Geolocation repro: Android 14+ never engages GPS

A one-page .NET MAUI app (Android only) that calls

```csharp
await Geolocation.Default.StartListeningForegroundAsync(
    new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(1)));
```

and shows how many `LocationChanged` events arrive, their accuracy, and whether the device's GNSS receiver is
running (from `GnssStatus.Callback`, used for diagnostics only).

## Steps

1. Use an Android 14+ device with Google Play Services, outdoors with a clear sky.
2. Close Google Maps and any other app using location, so nothing else turns GNSS on.
3. Build and run: `dotnet build -t:Run -f net10.0-android`
4. Leave the accuracy picker on **Best** (or pick any other `GeolocationAccuracy`), tap **Start listening**,
   then wait 2 minutes. Stop, pick another accuracy and repeat to see that none of them turn GNSS on.
5. Optional, while it runs:
   `adb shell dumpsys location | grep -B2 -A4 "com.example.mauigeolocationrepro"`
6. To compare, change `<MauiVersion>` in the csproj to `10.0.20` and repeat.

## Expected

The GNSS engine turns on, satellites are used, accuracy is within about 10 m, with about one update per second.

## Actual (measured on a Samsung Galaxy Tab A8 SM-X200, Android 14, outdoors, Google Maps closed)

| MAUI | `LocationChanged` events in 2 min | GNSS engine | Registration in `dumpsys location` |
|---|---|---|---|
| **10.0.120** | **0** | **never started** | `fused` provider `Request[@+1s0ms BALANCED]`, `gps` `ProviderRequest[OFF]` |
| 10.0.20 | 2 (±20 m, then ±1.7 m) | ON, 24 of 24 satellites used | `gps` `HIGH_ACCURACY` plus `network` `BALANCED` (both `minUpdateDistance=50`) |

On 10.0.120 the BALANCED fused provider only produces Wi-Fi/cell fixes (about ±100 m). MAUI's own `Best`
threshold (50 m) then discards all of them, so the app receives nothing. With `High` or lower (100 m threshold
or more) the app gets a few ±100 m fixes instead. So the thing to watch is the **GNSS engine** line: it never
starts, at any accuracy. Opening Google Maps at the same time "fixes" it, because Maps asks the shared
fused provider for HIGH_ACCURACY. (The 10.0.20 row's low event count is the separate distance issue #22683,
fixed since.)

Screenshots and `dumpsys` excerpts for both versions are in [evidence/](evidence/).

## Cause

`src/Essentials/src/Geolocation/Geolocation.android.cs`:

- `SelectProvider` returns `LocationManager.FusedProvider` on API 34+ for every `GeolocationAccuracy`.
  This came from PR #37976.
- `StartListeningForegroundAsync` and `GetLocationAsync` then call the legacy overload
  `LocationManager.RequestLocationUpdates(provider, minTime, minDistance, listener, looper)`, which has no
  quality parameter. For the `fused` provider, Android registers it as BALANCED, so `GeolocationAccuracy.Best`
  and `High` never reach Android.

Suggested fix: on API 31+, use `RequestLocationUpdates(provider, LocationRequest, Executor, listener)` with
`LocationRequest.Builder.SetQuality(...)` mapped from `GeolocationAccuracy` (High/Best → HIGH_ACCURACY).
