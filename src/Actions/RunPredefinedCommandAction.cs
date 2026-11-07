using System.Text.RegularExpressions;
using DoriDeck.TouchPortal;
using ScoreInterface.Responses;
using Serilog;

namespace DoriDeck.Actions;

/// Runs a common Dorico command picked by name, replacing the MD3 autocomplete for the usual cases.
internal sealed partial class RunPredefinedCommandAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string CommandData = "com.sarti2004.dorideck.run_predefined_command.command";
	public const string ApplyToAllFlowsData = "com.sarti2004.dorideck.run_predefined_command.all_flows";

	// Dorico 6 commands that need no parameters, shown until a connected Dorico reports its own list.
	public const string BundledCommandsFile = "dorico-commands.md";

	// Names are what TP shows and sends back, so they must stay unique and match valueChoices in entry.tp.
	public static readonly IReadOnlyList<(string Name, string Command)> PredefinedCommands =
	[
		.. new[] { "pp", "p", "mp", "mf", "f", "ff" }
			.Select(d => ($"Dynamic {d}", $"NoteInput.CreateDynamic?Definition={d}")),
		.. new[]
		{
			("ped", "Pedal"), ("pizz", "Pizzicato"), ("marcato", "Marcato"), ("portato", "Portato"),
			("mute", "Mute"), ("upbow", "Up-bow"), ("downbow", "Down-bow"), ("con sord", "Con sordino"),
			("nh", "Natural harmonic"), ("Vh", "Harmonic"), ("arco", "Arco"),
		}
			.Select(t => (t.Item2, $"NoteInput.CreatePlayingTechnique?Definition={t.Item1}&UseLocalOverride=0")),
		.. new[] { ("gliss", "Glissando"), ("trill", "Trill"), ("mordent", "Mordent") }
			.Select(o => (o.Item2, $"NoteInput.CreateOrnament?Definition={o.Item1}&UseLocalOverride=0")),
		.. new[]
		{
			("kAccent", "Accent"), ("kStaccato", "Staccato"), ("kMarcato", "Marcato (articulation)"),
			("kTenuto", "Tenuto"), ("kStress", "Stress"), ("kStaccatissimo", "Staccatissimo"), ("kUnstress", "Unstress"),
		}
			.Select(a => (a.Item2, $"NoteInput.SetArticulation?Value={a.Item1}")),
		.. new[] { ("fermata", "Fermata"), ("caesura", "Caesura") }
			.Select(p => (p.Item2, $"NoteInput.CreatePause?Definition={p.Item1}")),
		.. new[]
		{
			("kBreve", "Breve"), ("kSemibreve", "Whole note"), ("kMinim", "Half note"), ("kCrotchet", "Quarter note"),
			("kQuaver", "Eighth note"), ("kSemiquaver", "16th note"), ("kDemisemiquaver", "32nd note"),
			("kHemidemisemiquaver", "64th note"),
		}
			.Select(l => (l.Item2, $"NoteInput.NoteValue?LogDuration={l.Item1}&Set=true")),
	];

	private static readonly Dictionary<string, string> CommandsByName =
		PredefinedCommands.ToDictionary(c => c.Name, c => c.Command, StringComparer.Ordinal);

	// A Dorico label is "Display name (Command.Id)", or the bare id when Dorico gives no name.
	[GeneratedRegex(@"^(?<id>[A-Za-z]\w*(?:\.\w+)+)$|\((?<id>[A-Za-z]\w*(?:\.\w+)+)\)$")]
	private static partial Regex CommandIdPattern();

	public string Id => "com.sarti2004.dorideck.action.run_predefined_command";

	/// The predefined names, then every Dorico command that needs no parameters, sorted by label.
	public static IReadOnlyList<string> BuildChoices(IEnumerable<CommandInfo> doricoCommands) =>
	[
		.. PredefinedCommands.Select(c => c.Name),
		.. doricoCommands
			.Where(c => !c.RequiredParameters.Any())
			.Select(Label)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.OrdinalIgnoreCase),
	];

	// The id runs, not the name, so labels that Dorico later renames or translates keep working.
	public static string Label(CommandInfo command) =>
		string.IsNullOrWhiteSpace(command.DisplayName) ? command.Name : $"{command.DisplayName} ({command.Name})";

	/// Reads the bundled markdown table: | Action | Description | Arguments |.
	public static IReadOnlyList<CommandInfo> LoadBundledCommands(string path) =>
		File.ReadLines(path)
			.Select(line => line.Split('|', StringSplitOptions.TrimEntries))
			.Where(cells => cells.Length >= 3 && CommandIdPattern().IsMatch(cells[1]))
			.Select(cells => new CommandInfo(cells[1], cells[2].Length > 0 ? cells[2] : null))
			.ToList();

	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var name = data.GetText(CommandData);
		var command = name is null ? null : CommandsByName.GetValueOrDefault(name) ?? DoricoCommandId(name);
		if (command is null)
		{
			Logger.Warning("Run predefined command: unknown command {Name}.", name);
			return Task.CompletedTask;
		}

		return RunCommandAsync(command, data.GetSwitch(ApplyToAllFlowsData), cancellationToken);
	}

	private static string? DoricoCommandId(string label)
	{
		var match = CommandIdPattern().Match(label);
		return match.Success ? match.Groups["id"].Value : null;
	}
}
