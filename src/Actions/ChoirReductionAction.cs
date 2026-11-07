using DoriDeck.Services;
using DoriDeck.TouchPortal;
using ScoreInterface;
using ScoreInterface.Commands;
using ScoreInterface.Requests;
using Serilog;

namespace DoriDeck.Actions;

/// Creates a piano reduction of a choir score, with a cue for each voice.
internal sealed class ChoirReductionAction(
	DoricoSession session,
	IKeyboardService keyboard,
	IApplicationFocusService focus,
	ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string VoicesData = "com.sarti2004.dorideck.choir_reduction.voices";
	public const string DefaultVoices = "Soprano,Alto,Tenor,Bass";

	private static readonly char[] VoiceSeparators = [',', '\r', '\n'];
	private static readonly string[] DownStemPrefixes = ["alto", "bass"];
	private static readonly string[] UpperStaffPrefixes = ["soprano", "alto"];

	public string Id => "com.sarti2004.dorideck.action.choir_reduction";

	public async Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var voicesText = data.GetText(VoicesData);
		var voices = (string.IsNullOrWhiteSpace(voicesText) ? DefaultVoices : voicesText)
			.Split(VoiceSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped the choir reduction.");
			return;
		}

		if (!EnsureKeyboardAccess(keyboard))
		{
			return;
		}

		await focus.ActivateDoricoAsync(cancellationToken);

		await dorico.SetLayoutOptionsAsync([new OptionValue("cueLayoutOptions.showCues", "false")], cancellationToken: cancellationToken);
		await Task.Delay(300, cancellationToken);
		await dorico.SetNotationOptionsAsync([new OptionValue("omitBarRestsBeneathCues", "false")], cancellationToken: cancellationToken);
		await Task.Delay(300, cancellationToken);

		await dorico.SetEngravingOptionsAsync(
			[
				new OptionValue("cueEngravingOptions.showLyrics", "false"),
				new OptionValue("cueEngravingOptions.defaultCueClefValue", "1"),
				new OptionValue("cueEngravingOptions.cueVisualScale", "1"),
			],
			cancellationToken);
		await Task.Delay(300, cancellationToken);

		for (var i = 0; i < voices.Length; i++)
		{
			await CreateCueAsync(dorico, voices[i], cancellationToken);

			if (i < voices.Length - 1)
			{
				await MoveToNextStaffAsync(i == 0 || IsUpperStaff(voices[i + 1]), cancellationToken);
			}
		}

		await dorico.SetLayoutOptionsAsync([new OptionValue("cueLayoutOptions.showCues", "true")], cancellationToken: cancellationToken);
		await Task.Delay(300, cancellationToken);
		await dorico.SetNotationOptionsAsync([new OptionValue("omitBarRestsBeneathCues", "true")], cancellationToken: cancellationToken);
		await Task.Delay(300, cancellationToken);
	}

	private static async Task CreateCueAsync(IScoreInterfaceRemote dorico, string voice, CancellationToken cancellationToken)
	{
		await dorico.SendRequestAsync(new Command($"NoteInput.CreateCue?Definition={voice}&UseLocalOverride=0"), cancellationToken);
		await Task.Delay(250, cancellationToken);

		var stemDirection = IsStemsDown(voice) ? "kForceStemsDown" : "kForceStemsUp";
		await dorico.SendRequestAsync(new Command($"UI.InvokePropertyChangeValue?Type=kCueVoiceDirection&Value={stemDirection}"), cancellationToken);
		await Task.Delay(100, cancellationToken);

		if (voice.StartsWith("tenor", StringComparison.OrdinalIgnoreCase))
		{
			await dorico.SendRequestAsync(new Command("UI.InvokePropertyChangeValue?Type=kCueOctaveShift&Value=-1"), cancellationToken);
			await Task.Delay(100, cancellationToken);
		}
	}

	private async Task MoveToNextStaffAsync(bool moveBackUp, CancellationToken cancellationToken)
	{
		keyboard.PressChord(DoriDeckKey.Shift, DoriDeckKey.DownArrow);
		await Task.Delay(100, cancellationToken);
		keyboard.Press(DoriDeckKey.DownArrow);
		await Task.Delay(100, cancellationToken);

		if (moveBackUp)
		{
			keyboard.Press(DoriDeckKey.UpArrow);
			await Task.Delay(100, cancellationToken);
		}
	}

	private static bool IsStemsDown(string name) =>
		DownStemPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

	private static bool IsUpperStaff(string name) =>
		UpperStaffPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
