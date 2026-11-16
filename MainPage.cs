namespace MauiGeolocationRepro;

/// <summary>
/// Repro: on Android 14+, Geolocation.StartListeningForegroundAsync(GeolocationAccuracy.Best) never turns the GPS
/// receiver on. MAUI registers LocationManager's "fused" provider with the legacy RequestLocationUpdates overload,
/// which carries no quality, and Android records it as BALANCED (Wi-Fi/cell only).
///
/// Run outdoors with nothing else using location (close Google Maps). Tap Start and wait 2 minutes.
/// Expected: "GNSS engine" on, satellites used > 0, accuracy within about 10 m, about 1 update per second.
/// Actual:   "GNSS engine" never starts, 0 satellites, accuracy about 100 m, a handful of updates.
/// While it runs: adb shell dumpsys location | grep -A3 "fused provider:"  shows  Request[@+1s0ms BALANCED]
/// </summary>
public sealed class MainPage : ContentPage
{
	readonly Label _status = new() { FontSize = 18 };
	readonly Button _button = new() { Text = "Start listening (Best, 1 s)" };
	readonly DateTime _appStart = DateTime.Now;
	DateTime _startedAt;
	int _updates;
	double? _lastAccuracy, _bestAccuracy;
	string _gnss = "not reported";

	public MainPage()
	{
		_button.Clicked += OnButtonClicked;
		Geolocation.Default.LocationChanged += (_, e) =>
		{
			_updates++;
			_lastAccuracy = e.Location.Accuracy;
			if (_bestAccuracy is null || _lastAccuracy < _bestAccuracy)
				_bestAccuracy = _lastAccuracy;
			Log($"LocationChanged #{_updates}: accuracy {e.Location.Accuracy:0.0} m");
		};
		Geolocation.Default.ListeningFailed += (_, e) => Log($"ListeningFailed: {e.Error}");

		Content = new VerticalStackLayout { Padding = 24, Spacing = 16, Children = { _button, _status } };
		Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () => { Refresh(); return true; });
#if ANDROID
		GnssMonitor.Start(s => _gnss = s);
#endif
	}

	async void OnButtonClicked(object? sender, EventArgs e)
	{
		if (Geolocation.Default.IsListeningForeground)
		{
			Geolocation.Default.StopListeningForeground();
			_button.Text = "Start listening (Best, 1 s)";
			Log("Stopped");
			return;
		}

		if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
		{
			Log("Location permission not granted");
			return;
		}

		_updates = 0;
		_lastAccuracy = _bestAccuracy = null;
		_startedAt = DateTime.Now;
		var started = await Geolocation.Default.StartListeningForegroundAsync(
			new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(1)));
		_button.Text = "Stop";
		Log($"StartListeningForegroundAsync(Best, 1 s) returned {started}");
	}

	void Refresh()
	{
		var listening = Geolocation.Default.IsListeningForeground;
		_status.Text =
			$"Android {DeviceInfo.Current.VersionString}, {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}\n" +
			$"Listening: {listening}" + (listening ? $" for {(DateTime.Now - _startedAt).TotalSeconds:0} s" : "") + "\n" +
			$"LocationChanged events: {_updates}\n" +
			$"Last accuracy: {Format(_lastAccuracy)}\n" +
			$"Best accuracy: {Format(_bestAccuracy)}\n" +
			$"GNSS engine: {_gnss}";
	}

	static string Format(double? metres) => metres is null ? "-" : $"±{metres:0.0} m";

	static void Log(string message)
	{
#if ANDROID
		Android.Util.Log.Info("GeoRepro", message);
#endif
	}
}

#if ANDROID
/// <summary>Diagnostics only (not part of the bug): reports whether the GNSS receiver is running and satellites used.</summary>
static class GnssMonitor
{
	public static void Start(Action<string> report)
	{
		var lm = (Android.Locations.LocationManager)Android.App.Application.Context.GetSystemService(Android.Content.Context.LocationService)!;
		lm.RegisterGnssStatusCallback(new Callback(report), new Android.OS.Handler(Android.OS.Looper.MainLooper!));
	}

	sealed class Callback(Action<string> report) : Android.Locations.GnssStatus.Callback
	{
		bool _on;
		public override void OnStarted() { _on = true; report("ON (started)"); Android.Util.Log.Info("GeoRepro", "GNSS engine started"); }
		public override void OnStopped() { _on = false; report("off (stopped)"); Android.Util.Log.Info("GeoRepro", "GNSS engine stopped"); }

		public override void OnSatelliteStatusChanged(Android.Locations.GnssStatus status)
		{
			var used = 0;
			for (var i = 0; i < status.SatelliteCount; i++)
				if (status.UsedInFix(i))
					used++;
			report($"{(_on ? "ON" : "?")}, satellites used {used} of {status.SatelliteCount} in view");
		}
	}
}
#endif
