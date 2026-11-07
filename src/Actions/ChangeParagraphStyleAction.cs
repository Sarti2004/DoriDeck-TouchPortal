using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DoriDeck.Services;
using DoriDeck.Services.Windows;
using DoriDeck.TouchPortal;
using ScoreInterface.Commands;
using Serilog;

namespace DoriDeck.Actions;

/// Opens Dorico's Change Paragraph Style dialog, picks a style and confirms it with keystrokes.
internal sealed class ChangeParagraphStyleAction(
	DoricoSession session,
	IKeyboardService keyboard,
	IApplicationFocusService focus,
	ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string StyleData = "com.sarti2004.dorideck.change_paragraph_style.style";

	private const int DialogOpenDelayMilliseconds = 100;
	private const int KeyDelayMilliseconds = 20;
	private const int UiAutomationAttempts = 10;

	public string Id => "com.sarti2004.dorideck.action.change_paragraph_style";

	public async Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var style = data.GetText(StyleData);
		if (string.IsNullOrWhiteSpace(style))
		{
			Logger.Warning("Change paragraph style: no style selected.");
			return;
		}

		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped changing the paragraph style.");
			return;
		}

		if (!EnsureKeyboardAccess(keyboard))
		{
			return;
		}

		// Keystrokes go to the focused window, so without Dorico in front they could land in another app.
		if (!await focus.ActivateDoricoAsync(cancellationToken))
		{
			Logger.Warning("Could not bring Dorico to the front; skipped changing the paragraph style.");
			return;
		}

		var isDorico5 = Session.ActiveDoricoVersion == "5";

		await dorico.SendRequestAsync(new Command("Edit.ChangeParagraphStyle"), cancellationToken);
		await Task.Delay(DialogOpenDelayMilliseconds, cancellationToken);

		await PressAsync(DoriDeckKey.Return, cancellationToken);
		if (!(OperatingSystem.IsWindows() && isDorico5 && await SelectWithUiAutomationAsync(style, cancellationToken)))
		{
			keyboard.EnterText(style);
		}
		await Task.Delay(KeyDelayMilliseconds, cancellationToken);
		await PressAsync(DoriDeckKey.Return, cancellationToken);

		// Move focus from the style list to the OK button and press it.
		await PressAsync(DoriDeckKey.Tab, cancellationToken);
		if (!isDorico5)
		{
			await PressAsync(DoriDeckKey.Tab, cancellationToken);
		}
		await PressAsync(DoriDeckKey.Return, cancellationToken);
	}

	// The dropdown popup appears asynchronously after Return, so retry until its items are exposed.
	[SupportedOSPlatform("windows")]
	private static async Task<bool> SelectWithUiAutomationAsync(string style, CancellationToken cancellationToken)
	{
		for (var attempt = 0; attempt < UiAutomationAttempts; attempt++)
		{
			try
			{
				if (WindowsUiAutomation.SelectDropdownItem(style))
				{
					return true;
				}
			}
			catch (COMException)
			{
				// The popup can disappear or rebuild between lookups; try again.
			}

			await Task.Delay(KeyDelayMilliseconds, cancellationToken);
		}

		return false;
	}

	private async Task PressAsync(DoriDeckKey key, CancellationToken cancellationToken)
	{
		keyboard.Press(key);
		await Task.Delay(KeyDelayMilliseconds, cancellationToken);
	}
}
