using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoriDeck.TouchPortal;

internal static class MessageTypes
{
	public const string Info = "info";
	public const string Action = "action";

	// Sent instead of "action" for actions in a button's On Hold tab.
	public const string Down = "down";
	public const string Up = "up";
	public const string Settings = "settings";
	public const string ClosePlugin = "closePlugin";
}

internal sealed record ActionDataItem(string Id, string Value);

/// Data fields of an incoming action, keyed by data id.
internal sealed class ActionData(IReadOnlyList<ActionDataItem> items)
{
	public static readonly ActionData Empty = new([]);

	public string? GetText(string id) => items.FirstOrDefault(item => item.Id == id)?.Value;

	// The TP docs don't specify the switch format, so accept either form.
	public bool GetSwitch(string id) =>
		string.Equals(GetText(id), "On", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(GetText(id), "true", StringComparison.OrdinalIgnoreCase);
}

// Type is action, down or up.
internal sealed record ActionMessage(string ActionId, ActionData Data, string Type = MessageTypes.Action);

internal sealed record PairMessage(string Id)
{
	public string Type { get; } = "pair";
}

internal sealed record StateUpdateMessage(string Id, string Value)
{
	public string Type { get; } = "stateUpdate";
}

internal sealed record ChoiceUpdateMessage(string Id, IReadOnlyList<string> Value)
{
	public string Type { get; } = "choiceUpdate";
}

internal static class MessageSerializer
{
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

	/// Reads [{"name":"value"}, ...], the shape of both info.settings and settings.values.
	public static IReadOnlyDictionary<string, string> ParseSettings(JsonElement root, string property)
	{
		var settings = new Dictionary<string, string>(StringComparer.Ordinal);
		if (root.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array)
		{
			foreach (var setting in items.EnumerateArray().SelectMany(item => item.EnumerateObject()))
			{
				settings[setting.Name] = setting.Value.ToString();
			}
		}

		return settings;
	}

	public static ActionMessage ParseAction(JsonElement root)
	{
		var items = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
			? data.EnumerateArray()
				.Select(item => new ActionDataItem(
					item.GetProperty("id").GetString() ?? string.Empty,
					item.TryGetProperty("value", out var value) ? value.ToString() : string.Empty))
				.ToList()
			: [];

		return new ActionMessage(
			root.GetProperty("actionId").GetString() ?? string.Empty,
			new ActionData(items),
			root.TryGetProperty("type", out var type) ? type.GetString() ?? MessageTypes.Action : MessageTypes.Action);
	}
}
