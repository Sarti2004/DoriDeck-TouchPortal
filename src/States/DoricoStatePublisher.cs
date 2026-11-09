using System.Globalization;
using DoriDeck.Actions;
using DoriDeck.TouchPortal;
using Microsoft.Extensions.Hosting;

namespace DoriDeck.States;

/// Mirrors session values into TP states once paired, sending only values that changed.
internal sealed class DoricoStatePublisher : IHostedService
{
	private const string Prefix = "com.sarti2004.dorideck.state.";

	// Ids must match the states declared in entry.tp.
	private static readonly (string Id, Func<DoricoSession, string> Read)[] StateSpecs =
	[
		(Prefix + "dorico_connected", s => Bool(s.IsConnected)),
		(Prefix + "dorico_flow_id", s => s.CurrentFlowId),
		(Prefix + "dorico_flow_name", s => s.CurrentFlowName),
		(Prefix + "dorico_flow_count", s => s.FlowsCount.ToString(CultureInfo.InvariantCulture)),
		(Prefix + "dorico_mode", s => s.WindowMode),
		(Prefix + "dorico_mode_raw", s => s.WindowModeRaw),
		(Prefix + "dorico_has_score", s => Bool(s.HasScore)),
		(Prefix + "dorico_hasSelection", s => Bool(s.HasSelection)),
		(Prefix + "dorico_activeOpenScoreID", s => s.ActiveOpenScoreId.ToString(CultureInfo.InvariantCulture)),
		(Prefix + "dorico_noteInputMode", s => s.NoteInputMode),
		(Prefix + "dorico_accidental", s => s.Accidental),
		(Prefix + "dorico_noteInputActive", s => Bool(s.NoteInputActive)),
		(Prefix + "dorico_restMode", s => Bool(s.RestMode)),
		(Prefix + "dorico_recent_file", s => s.RecentFileName(0)),
		(Prefix + "dorico_previous_file", s => s.RecentFileName(1)),
		(Prefix + "dorico_duration", DurationName),
		(Prefix + "dorico_rhythmDots", s => s.RhythmDots.ToString(CultureInfo.InvariantCulture)),
		(Prefix + "dorico_inPlayback", s => Bool(s.InPlayback)),
		(Prefix + "dorico_isRecording", s => Bool(s.IsRecording)),
	];

	private static IReadOnlyList<(string Name, string DoricoValue)> Durations => DurationPadKeyAction.Durations;

	
	public static IEnumerable<string> DurationValues =>
		Durations.Select(d => d.Name).Prepend("").Concat(Durations.Select(d => d.Name + " dotted"));

	private readonly DoricoSession _session;
	private readonly ITouchPortalClient _client;
	private readonly Dictionary<string, string> _lastSent = new(StringComparer.Ordinal);
	private readonly Lock _lock = new();
	private bool _paired;

	public DoricoStatePublisher(DoricoSession session, ITouchPortalClient client)
	{
		_session = session;
		_client = client;
	}

	private static string Bool(bool value) => value ? "true" : "false";

	// Empty for a duration the pad has no key for.
	private static string DurationName(DoricoSession session)
	{
		var name = Durations.FirstOrDefault(d => string.Equals(d.DoricoValue, session.Duration, StringComparison.OrdinalIgnoreCase)).Name;
		return name is null ? "" : session.RhythmDots > 0 ? name + " dotted" : name;
	}

	public static IEnumerable<string> StateIds => StateSpecs.Select(spec => spec.Id);

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_session.ConnectionChanged += OnConnectionChanged;
		_session.DisplayStateChanged += Publish;
		_client.Paired += OnPaired;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_session.ConnectionChanged -= OnConnectionChanged;
		_session.DisplayStateChanged -= Publish;
		_client.Paired -= OnPaired;
		return Task.CompletedTask;
	}

	private void Publish()
	{
		// Read and queue under one lock, so concurrent status events can't send a stale value last.
		lock (_lock)
		{
			if (!_paired)
			{
				return;
			}

			foreach (var (id, read) in StateSpecs)
			{
				var value = read(_session);
				if (!_lastSent.TryGetValue(id, out var last) || last != value)
				{
					_lastSent[id] = value;
					_client.UpdateState(id, value);
				}
			}
		}
	}

	// A fresh TP session knows nothing yet, so resend everything.
	private void OnPaired()
	{
		lock (_lock)
		{
			_paired = true;
			_lastSent.Clear();
		}

		Publish();
	}

	private void OnConnectionChanged(bool connected) => Publish();
}
