using DoriDeck.TouchPortal;
using Serilog;

namespace DoriDeck.Actions;

/// Connects to Dorico. A failure is logged by the session.
internal sealed class ConnectDoricoAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public string Id => "com.sarti2004.dorideck.action.connect";

	// No time limit here: the first connect waits for the user to approve DoriDeck in Dorico.
	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken) =>
		Session.EnsureConnectedAsync().WaitAsync(cancellationToken);
}
