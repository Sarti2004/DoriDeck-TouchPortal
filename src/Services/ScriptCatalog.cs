using Serilog;

namespace DoriDeck.Services;

/// Finds the Lua scripts under a folder for the Run Script action's list.
internal static class ScriptCatalog
{
	/// Every .lua script under the folder, recursively, as slash-separated relative names without extension.
	public static IReadOnlyList<string> DiscoverScriptNames(string scriptPath, ILogger logger)
	{
		if (!Directory.Exists(scriptPath))
		{
			logger.Warning("Script path does not exist: {ScriptPath}", scriptPath);
			return [];
		}

		try
		{
			return Directory.GetFiles(scriptPath, "*.lua", SearchOption.AllDirectories)
				.Select(file => ToScriptName(scriptPath, file))
				.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
		{
			// An unreadable folder shouldn't stop the plugin; the list stays empty until the next scan.
			logger.Error(ex, "Failed to scan the script folder {ScriptPath}.", scriptPath);
			return [];
		}
	}

	private static string ToScriptName(string scriptPath, string file)
	{
		var relativePath = Path.GetRelativePath(scriptPath, file);

		return Path.ChangeExtension(relativePath, null)
			.Replace(Path.DirectorySeparatorChar, '/')
			.Replace(Path.AltDirectorySeparatorChar, '/');
	}
}
