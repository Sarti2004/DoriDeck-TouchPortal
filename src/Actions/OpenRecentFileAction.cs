using System.Diagnostics;
using DoriDeck.TouchPortal;
using ScoreInterface.Commands;
using Serilog;

namespace DoriDeck.Actions;

/// Opens the first project in Dorico's recent file list.
internal sealed class OpenRecentFileAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public string Id => "com.sarti2004.dorideck.action.open_recent_file";

	public Task ExecuteAsync(ActionData data, CancellationToken cancellationToken) =>
		OpenRecentFileAsync(Session, 0, Logger, cancellationToken);

	internal static async Task OpenRecentFileAsync(
		DoricoSession session,
		int index,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var recentFiles = session.RecentFiles;
		if (recentFiles.Count <= index)
		{
			logger.Warning("Dorico's recent file list has no entry {Index}.", index);
			return;
		}

		var dorico = await GetConnectedRemoteAsync(session, cancellationToken);
		if (dorico == null)
		{
			// Dorico isn't running: let the OS open the project.
			OpenWithShell(recentFiles[index], logger);
			return;
		}

		await dorico.SendRequestAsync(
			new Command("File.Open", new CommandParameter("File", EscapeCommas(recentFiles[index]))),
			cancellationToken);
	}

	private static void OpenWithShell(string filePath, ILogger logger)
	{
		var fullPath = Path.GetFullPath(filePath);
		if (!File.Exists(fullPath))
		{
			logger.Warning("Recent file not found: {Path}", fullPath);
			return;
		}

		Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true })?.Dispose();
	}
}
