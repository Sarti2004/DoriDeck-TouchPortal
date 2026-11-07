using DoriDeck.TouchPortal;
using Serilog;

namespace DoriDeck.Actions;

/// Runs a single Dorico command, e.g. "NoteInput.CreateDynamic?Definition=f".
internal sealed class RunCommandAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string CommandNameData = "com.sarti2004.dorideck.run_command.command";
	public const string ApplyToAllFlowsData = "com.sarti2004.dorideck.run_command.all_flows";

	public string Id => "com.sarti2004.dorideck.action.run_command";

	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var commandName = data.GetText(CommandNameData)?.Trim();
		if (string.IsNullOrEmpty(commandName))
		{
			Logger.Warning("Run command: no command entered.");
			return Task.CompletedTask;
		}

		return RunCommandAsync(commandName, data.GetSwitch(ApplyToAllFlowsData), cancellationToken);
	}
}
