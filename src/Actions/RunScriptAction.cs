using DoriDeck.TouchPortal;
using Serilog;

namespace DoriDeck.Actions;

/// Runs a Lua script from the Dorico scripts folder.
internal sealed class RunScriptAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string ScriptNameData = "com.sarti2004.dorideck.run_script.script";
	public const string ApplyToAllFlowsData = "com.sarti2004.dorideck.run_script.all_flows";

	public string Id => "com.sarti2004.dorideck.action.run_script";

	public async Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var scriptName = data.GetText(ScriptNameData);
		if (string.IsNullOrWhiteSpace(scriptName))
		{
			Logger.Warning("Run script: no script selected.");
			return;
		}

		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped script {Script}.", scriptName);
			return;
		}

		await ForEachFlowAsync(
			dorico,
			data.GetSwitch(ApplyToAllFlowsData),
			() => ScriptExecution.RunAsync(dorico, Session.ScriptPath, scriptName, cancellationToken),
			cancellationToken,
			continueOnFlowError: true);
	}
}
