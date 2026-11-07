using System.Globalization;
using Serilog;

namespace DoriDeck;

/// Validated plugin settings. TP identifies settings by name, so renaming one loses the saved value.
public sealed record DoriDeckSettings
{
	public const string DoricoVersionName = "Dorico version";
	public const string ScriptPathName = "Scripts folder";
	public const string FlowSwitchDelayName = "Flow switch delay (ms)";

	public static readonly IReadOnlyList<string> Names =
		[DoricoVersionName, ScriptPathName, FlowSwitchDelayName];

	public string DoricoVersion { get; init; } = DoricoSession.DefaultDoricoVersion;

	// Empty means Dorico's default scripts folder.
	public string ScriptPath { get; init; } = string.Empty;

	public int FlowSwitchDelay { get; init; } = DoricoSession.DefaultFlowSwitchDelay;

	public static DoriDeckSettings Parse(IReadOnlyDictionary<string, string> values, ILogger logger)
	{
		var version = values.GetValueOrDefault(DoricoVersionName)?.Trim();

		return new DoriDeckSettings
		{
			DoricoVersion = string.IsNullOrEmpty(version) ? DoricoSession.DefaultDoricoVersion : version,
			ScriptPath = values.GetValueOrDefault(ScriptPathName)?.Trim() ?? string.Empty,
			FlowSwitchDelay = ParseDelay(values, FlowSwitchDelayName, DoricoSession.DefaultFlowSwitchDelay, logger),
		};
	}

	private static int ParseDelay(IReadOnlyDictionary<string, string> values, string name, int fallback, ILogger logger)
	{
		if (!values.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
		{
			return fallback;
		}

		if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay) && delay >= 0)
		{
			return delay;
		}

		logger.Warning("Setting {Name} has invalid value {Value}; using {Fallback}.", name, raw, fallback);
		return fallback;
	}
}
