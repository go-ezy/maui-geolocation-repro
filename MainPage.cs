namespace MauiGeolocationRepro;

/// <summary>
/// Repro: on Android 14+, Geolocation.StartListeningForegroundAsync(GeolocationAccuracy.Best) never turns the GPS
/// receiver on. MAUI registers LocationManager's "fused" provider with the legacy RequestLocationUpdates overload,
/// which carries no quality, and Android records it as BALANCED (Wi-Fi/cell only).
///
/// Run outdoors with nothing else using location (close Google Maps). Pick an accuracy, tap Start, wait 2 minutes.
/// Expected: "GNSS engine" on, satellites used > 0, accuracy within about 10 m, about 1 update per second.
/// Actual, at every accuracy: "GNSS engine" never starts. With Best there are 0 events (MAUI's 50 m filter drops
/// every ±100 m Wi-Fi/cell fix); with High or lower there are a few events at about ±100 m.
/// While it runs: adb shell dumpsys location | grep -A3 "fused provider:"  shows  Request[@+1s0ms BALANCED]
/// </summary>
public sealed class MainPage : ContentPage
{
	readonly Label _status = new() { FontSize = 18 };
	readonly Picker _accuracy = new() { Title = "Accuracy", ItemsSource = Enum.GetNames<GeolocationAccuracy>() };
	readonly Button _button = new();
	DateTime _startedAt;
	GeolocationAccuracy _listeningAccuracy;
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

		_accuracy.SelectedItem = nameof(GeolocationAccuracy.Best);
		_accuracy.SelectedIndexChanged += (_, _) => Refresh();

		Content = new VerticalStackLayout { Padding = 24, Spacing = 16, Children = { _accuracy, _button, _status } };
		Refresh();
		Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () => { Refresh(); return true; });
	}

	GeolocationAccuracy SelectedAccuracy =>
		Enum.TryParse<GeolocationAccuracy>(_accuracy.SelectedItem as string, out var a) ? a : GeolocationAccuracy.Best;

	async void OnButtonClicked(object? sender, EventArgs e)
	{
		if (Geolocation.Default.IsListeningForeground)
		{
			Geolocation.Default.StopListeningForeground();
			Log("Stopped");
			Refresh();
			return;
		}

		if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
		{
			Log("Location permission not granted");
			return;
		}

#if ANDROID
		// Needs the location permission, so it starts here rather than at launch.
		GnssMonitor.Start(s => _gnss = s);
#endif

		_updates = 0;
		_lastAccuracy = _bestAccuracy = null;
		_startedAt = DateTime.Now;
		_listeningAccuracy = SelectedAccuracy;
		var started = await Geolocation.Default.StartListeningForegroundAsync(
			new GeolocationListeningRequest(_listeningAccuracy, TimeSpan.FromSeconds(1)));
		Log($"StartListeningForegroundAsync({_listeningAccuracy}, 1 s) returned {started}");
		Refresh();
	}

	void Refresh()
	{
		var listening = Geolocation.Default.IsListeningForeground;
		_accuracy.IsEnabled = !listening;
		_button.Text = listening ? "Stop" : $"Start listening ({SelectedAccuracy}, 1 s)";
		_status.Text =
			$"Android {DeviceInfo.Current.VersionString}, {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}\n" +
			$"Listening: {listening}" + (listening ? $" ({_listeningAccuracy}) for {(DateTime.Now - _startedAt).TotalSeconds:0} s" : "") + "\n" +
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
	static bool _started;

	public static void Start(Action<string> report)
	{
		if (_started)
			return;
		_started = true;
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
