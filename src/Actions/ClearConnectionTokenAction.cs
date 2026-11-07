using DoriDeck.TouchPortal;
using Serilog;

namespace DoriDeck.Actions;

/// Forgets the Dorico session token, so the next connection asks for approval again.
internal sealed class ClearConnectionTokenAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public string Id => "com.sarti2004.dorideck.action.clear_connection_token";

	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		Session.ClearSessionToken();
		Logger.Information("Cleared the Dorico connection token.");
		return Task.CompletedTask;
	}
}
