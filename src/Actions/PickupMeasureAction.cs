using DoriDeck.TouchPortal;
using ScoreInterface.Commands;
using Serilog;

namespace DoriDeck.Actions;

/// Creates a pickup measure. A failed step stops the rest, including the delete.
internal sealed class PickupMeasureAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(20);

	public string Id => "com.sarti2004.dorideck.action.pickup_measure";

	public async Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped the pickup measure.");
			return;
		}

		await dorico.SendRequestAsync(new Command("Edit.SelectToStartOfSystem"), cancellationToken);
		await Task.Delay(StepDelay, cancellationToken);
		await dorico.SendRequestAsync(new Command("NoteInput.InsertScope", new CommandParameter("InsertModeScope", "kGlobalCurrentBar")), cancellationToken);
		await Task.Delay(StepDelay, cancellationToken);
		await dorico.SendRequestAsync(new Command("Filter.Rests"), cancellationToken);
		await Task.Delay(StepDelay, cancellationToken);
		await dorico.SendRequestAsync(new Command("Edit.Delete"), cancellationToken);
		await Task.Delay(StepDelay, cancellationToken);
		await dorico.SendRequestAsync(new Command("NoteInput.Mode"), cancellationToken);
	}
}
