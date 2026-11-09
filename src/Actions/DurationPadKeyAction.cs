using DoriDeck.TouchPortal;
using Serilog;

namespace DoriDeck.Actions;


internal sealed class DurationPadKeyAction : DoriDeckActionBase, ITouchPortalHoldAction
{
	public const string KeyData = "com.sarti2004.dorideck.duration_pad_key.key";

	private static readonly TimeSpan DefaultLongPress = TimeSpan.FromMilliseconds(500);

	public static readonly IReadOnlyList<(string Name, string DoricoValue)> Durations =
	[
		("Breve", "kBreve"),
		("Whole", "kSemibreve"),
		("Half", "kMinim"),
		("Quarter", "kCrotchet"),
		("Eighth", "kQuaver"),
		("16th", "kSemiQuaver"),
		("32nd", "kDemiSemiQuaver"),
		("64th", "kHemiDemiSemiQuaver"),
	];

	private sealed record Key(string Name, string Press, string? LongPress, bool Dotted = false);

	private static readonly Key[] KeyTable =
	[
		.. Durations.Select(d => new Key(d.Name, $"NoteInput.NoteValue?LogDuration={d.DoricoValue}&Set=true", null, Dotted: true)),
		new("Play", "Play.StartOrStop", "Play.StartOrStop?PlayFromLocation=kSelection"),
		new("Left", "EventEdit.NavigateLeft", "EventEdit.NavigateLeftBar"),
		new("Right", "EventEdit.NavigateRight", "EventEdit.NavigateRightBar"),
	];

	public static IEnumerable<string> Keys => KeyTable.Select(key => key.Name);

	private readonly TimeSpan _longPress;
	private readonly Lock _lock = new();
	private readonly Dictionary<string, CancellationTokenSource> _held = new(StringComparer.Ordinal);

	public DurationPadKeyAction(DoricoSession session, ILogger logger)
		: this(session, logger, DefaultLongPress)
	{
	}

	internal DurationPadKeyAction(DoricoSession session, ILogger logger, TimeSpan longPress)
		: base(session, logger)
	{
		_longPress = longPress;
	}

	public string Id => "com.sarti2004.dorideck.action.duration_pad_key";

	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken) =>
		Find(data) is { } key ? RunCommandAsync(key.Press, applyToAllFlows: false, cancellationToken) : Task.CompletedTask;

	public async Task DownAsync(ActionData data, CancellationToken cancellationToken)
	{
		if (Find(data) is not { } key)
		{
			return;
		}

		var press = new CancellationTokenSource();
		lock (_lock)
		{
			if (_held.Remove(key.Name, out var stale))
			{
				stale.Cancel();
			}

			_held[key.Name] = press;
		}

		try
		{
			await Task.Delay(_longPress, press.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		if (!Release(key, press))
		{
			return;
		}

		await RunCommandAsync(key.LongPress ?? key.Press, applyToAllFlows: false, cancellationToken);
		if (key.Dotted)
		{
			await RunCommandAsync("NoteInput.SetDotted?Value=1&Set=true", applyToAllFlows: false, cancellationToken);
		}
	}

	public Task UpAsync(ActionData data, CancellationToken cancellationToken)
	{
		if (Find(data) is not { } key)
		{
			return Task.CompletedTask;
		}

		CancellationTokenSource? press;
		lock (_lock)
		{
			_held.Remove(key.Name, out press);
		}

		if (press is null)
		{
			return Task.CompletedTask;
		}

		press.Cancel();
		return RunCommandAsync(key.Press, applyToAllFlows: false, cancellationToken);
	}

	private bool Release(Key key, CancellationTokenSource press)
	{
		lock (_lock)
		{
			if (!_held.TryGetValue(key.Name, out var current) || current != press)
			{
				return false;
			}

			_held.Remove(key.Name);
			return true;
		}
	}

	private Key? Find(ActionData data)
	{
		var name = data.GetText(KeyData);
		var key = KeyTable.FirstOrDefault(k => k.Name == name);
		if (key is null)
		{
			Logger.Warning("Unknown Duration Pad key {Key}.", name);
		}

		return key;
	}
}
