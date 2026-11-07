using DoriDeck.TouchPortal;
using ScoreInterface.Commands;
using Serilog;

namespace DoriDeck.Actions;

/// Runs an inline Lua snippet, via a temporary file because Dorico only runs scripts from disk.
internal sealed class CustomScriptAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string LuaScriptData = "com.sarti2004.dorideck.custom_script.lua";

	public string Id => "com.sarti2004.dorideck.action.custom_script";

	public async Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var luaScript = data.GetText(LuaScriptData);
		if (string.IsNullOrWhiteSpace(luaScript))
		{
			Logger.Warning("Custom script: no Lua entered.");
			return;
		}

		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped the custom script.");
			return;
		}

		var tmpFile = Path.GetTempFileName();
		var tmpPath = Path.ChangeExtension(tmpFile, ".lua");
		File.Delete(tmpFile);

		try
		{
			await File.WriteAllTextAsync(tmpPath, luaScript, cancellationToken);

			await dorico.SendRequestAsync(
				new Command("Script.RunScript", new CommandParameter("ScriptPath", EscapeCommas(tmpPath.Replace('\\', '/')))),
				cancellationToken);
		}
		finally
		{
			File.Delete(tmpPath);
		}
	}
}
