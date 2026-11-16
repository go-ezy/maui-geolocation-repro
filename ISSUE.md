<!--
Draft for https://github.com/dotnet/maui/issues/new?template=bug-report.yml
Title: [Android] Geolocation on API 34+ registers the fused provider as BALANCED, so GPS never turns on (Best/High ignored)
Paste each section into the matching field of the form. Attach the two screenshots from evidence/.
-->

### Description

On Android 14+ (API 34), `Geolocation.StartListeningForegroundAsync` and `GetLocationAsync` never turn the
GPS/GNSS receiver on, even with `GeolocationAccuracy.Best` or `High`. Locations come only from Wi-Fi and cell
towers (about ±100 m). With `Best`, MAUI's own 50 m accuracy filter then drops all of them, so `LocationChanged`
never fires.

`adb shell dumpsys location` shows MAUI's registration on the framework `fused` provider as BALANCED, with the GPS
provider off:

```
fused provider:
  service: ProviderRequest[@+1s0ms, WorkSource{10491 com.example.mauigeolocationrepro}]
    10491/com.example.mauigeolocationrepro/B2261153 Request[@+1s0ms BALANCED, WorkSource{...}]
gps provider:
  service: ProviderRequest[OFF]
  mStarted=false
```

**Cause** (`src/Essentials/src/Geolocation/Geolocation.android.cs` on `main`):

- `SelectProvider` returns `LocationManager.FusedProvider` on API 34+ for every `GeolocationAccuracy`
  (introduced by #37976).
- The updates are then requested with the legacy overload
  `LocationManager.RequestLocationUpdates(provider, minTime, minDistance, listener, looper)` (lines 104 and
  185). That overload carries no quality. For the `gps` provider this never mattered, but for the `fused`
  provider Android registers the request as BALANCED.
- So `DesiredAccuracy` never reaches Android. It is only used for MAUI's own accuracy filter.

**Suggested fix:** on API 31+, call `RequestLocationUpdates(provider, LocationRequest, Executor, listener)` with
`new LocationRequest.Builder(minTime).SetQuality(...)`, mapping High/Best to `HIGH_ACCURACY`, Medium/Default to
`BALANCED_POWER_ACCURACY`, and Low/Lowest to `LOW_POWER`. I tested this request (the fused provider with
`QUALITY_HIGH_ACCURACY`, outside MAUI) on the same device: GNSS turned on, about 1 update per second, median
±8.7 m.

Real-world impact: our field app's location is unusable on Android 14 tablets unless Google Maps is running
alongside it, because Maps' HIGH_ACCURACY request on the shared fused provider is what turns GNSS on. This is
probably the cause behind #28962 and discussion #32345.

**Note on how this was found:** I'm an app developer, not familiar with MAUI's internals. I tracked this down
with the help of Claude Code (an AI coding tool), which also wrote the repro app and the suggested fix. The
measurements, screenshots and `dumpsys` output are all from a real device and I've checked them myself, but I
can't vouch for the fix beyond the test described above. Please treat it as a pointer, not a reviewed change.

### Steps to Reproduce

1. Use an Android 14+ device with Google Play Services (tested on a Samsung Galaxy Tab A8 SM-X200, Android 14).
   Go outdoors with a clear sky, and close Google Maps and any other app using location.
2. Run the repro app and tap **Start listening (Best, 1 s)**. It calls
   `StartListeningForegroundAsync(new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(1)))`.
3. Wait 2 minutes. The app shows the number of `LocationChanged` events, the accuracy, and whether the GNSS
   engine is running (from `GnssStatus.Callback`).
4. Optionally run `adb shell dumpsys location` while it is listening.
5. Change `<MauiVersion>` from 10.0.120 to 10.0.20 and repeat.

**Expected:** the GNSS engine turns on, with regular updates at about 10 m accuracy.

**Actual:**

| MAUI | `LocationChanged` events in 2 min | GNSS engine | dumpsys |
|---|---|---|---|
| 10.0.120 | **0** | never started | `fused` `BALANCED`, `gps` OFF |
| 10.0.20 | 2 (±20 m, then ±1.7 m) | on, 24 of 24 satellites used | `gps` `HIGH_ACCURACY` + `network` `BALANCED` |

(The low 10.0.20 count is the separate `minDistance` issue #22683, fixed since.)

### Link to public reproduction project repository

<!-- push the MauiGeolocationRepro folder to a public GitHub repo and put the link here -->

### Version with bug

10.0.120 (also present on `main`)

### Is this a regression from previous behavior?

Yes, this used to work in .NET MAUI

### Last version that worked well

10.0.20 (it engages GPS, apart from the separate distance issue #22683)

### Affected platforms

Android

### Affected platform versions

Android 14 (API 34) and later

### Did you find any workaround?

Bypass MAUI Geolocation on Android and request the fused provider with an explicit quality:

```csharp
var request = new Android.Locations.LocationRequest.Builder(1000)
    .SetQuality((int)Android.Locations.LocationRequestQuality.HighAccuracy)
    .Build();
locationManager.RequestLocationUpdates(LocationManager.FusedProvider, request, context.MainExecutor, listener);
```

### Relevant log output

```
# MAUI 10.0.120 – adb shell dumpsys location, ~60 s after StartListeningForegroundAsync(Best, 1 s)
fused provider:
  service: ProviderRequest[@+1s0ms, WorkSource{10491 com.example.mauigeolocationrepro}]
    10491/com.example.mauigeolocationrepro/B2261153 Request[@+1s0ms BALANCED, WorkSource{10491 com.example.mauigeolocationrepro}]
gps provider:
  service: ProviderRequest[OFF]
  mStarted=false

# MAUI 10.0.20 – same app, same device
network provider:
  10491/com.example.mauigeolocationrepro/41BA03E5 Request[@+1s0ms BALANCED, minUpdateDistance=50.0, ...]
fused provider:
  service: ProviderRequest[OFF]
gps provider:
  service: ProviderRequest[@+1s0ms, HIGH_ACCURACY, WorkSource{10491 com.example.mauigeolocationrepro}]
  mStarted=true
```
