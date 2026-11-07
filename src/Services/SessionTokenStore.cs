using System.Text.Json;
using Serilog;

namespace DoriDeck.Services;

/// Keeps the Dorico session token so reconnects skip the approval prompt. TP has no secret store.
public interface ISessionTokenStore
{
	string? Read();

	void Save(string token);

	void Clear();
}

internal sealed class FileSessionTokenStore(ILogger logger) : ISessionTokenStore
{
	private sealed record Payload(string? SessionToken);

	private static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"DoriDeck-TP",
		"session.json");

	private readonly ILogger _logger = logger.ForContext<FileSessionTokenStore>();

	public string? Read()
	{
		try
		{
			return File.Exists(FilePath)
				? JsonSerializer.Deserialize<Payload>(File.ReadAllText(FilePath))?.SessionToken
				: null;
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			_logger.Warning(ex, "Failed to read the Dorico session token from {Path}.", FilePath);
			return null;
		}
	}

	public void Save(string token) => Write(new Payload(token));

	public void Clear() => Write(new Payload(null));

	private void Write(Payload payload)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(payload));
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.Warning(ex, "Failed to save the Dorico session token to {Path}.", FilePath);
		}
	}
}
