using DoriDeck.TouchPortal;
using ScoreInterface.Commands;
using ScoreInterface.Requests;
using Serilog;

namespace DoriDeck.Actions;

/// Creates a rehearsal mark with custom text. Changes the score's rehearsal mark engraving options, as the original did.
internal sealed class SectionHeaderAction(DoricoSession session, ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string TextData = "com.sarti2004.dorideck.section_header.text";
	public const string NewLineData = "com.sarti2004.dorideck.section_header.new_line";

	public string Id => "com.sarti2004.dorideck.action.section_header";

	public async Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var text = data.GetText(TextData);
		if (string.IsNullOrWhiteSpace(text))
		{
			Logger.Warning("Rehearsal mark: no text entered.");
			return;
		}

		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped the rehearsal mark.");
			return;
		}

		// Raw on purpose: the command text is sent as JSON, which turns this into a real line break.
		var separator = data.GetSwitch(NewLineData) ? @"\r\n" : " ";

		var enclosureType = (await dorico.GetEngravingOptionsAsync(cancellationToken))?
			.FirstOrDefault(option => option.Path == "rehearsalMarkOptions.enclosureType")
			?.CurrentValue;

		if (enclosureType != "kNone")
		{
			await dorico.SetEngravingOptionsAsync(
				[
					new OptionValue("rehearsalMarkOptions.enclosureType", "kNone"),
					new OptionValue("rehearsalMarkOptions.noteRelativeHorizontalAlignment", "kLeft"),
					new OptionValue("rehearsalMarkOptions.minimumDistanceFromStave", "5"),
				],
				cancellationToken);
			await Task.Delay(20, cancellationToken);
		}

		await dorico.SendRequestAsync(new Command("NoteInput.CreateRehearsalMark"), cancellationToken);
		await Task.Delay(20, cancellationToken);

		await dorico.SendRequestAsync(new Command("UI.InvokePropertyChangeValue?Type=kRehearsalMarkCustomPrefix&Value="), cancellationToken);
		await Task.Delay(20, cancellationToken);
		await dorico.SendRequestAsync(new Command("UI.InvokePropertyChangeValue?Type=kRehearsalMarkCustomPrefix&Value="), cancellationToken);
		await Task.Delay(10, cancellationToken);

		await dorico.SendRequestAsync(
			new Command($"UI.InvokePropertyChangeValue?Type=kRehearsalMarkCustomSuffix&Value={separator}{EscapeCommas(text)}"),
			cancellationToken);
	}
}
