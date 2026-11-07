using DoriDeck.TouchPortal;
using Serilog;

namespace DoriDeck.Actions;

/// Opens the second project in Dorico's recent file list.
internal sealed class OpenPreviousFileAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public string Id => "com.sarti2004.dorideck.action.open_previous_file";

	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken) =>
		OpenRecentFileAction.OpenRecentFileAsync(Session, 1, Logger, cancellationToken);
}
