using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DoriDeck.Services;
using DoriDeck.Syllabifiers;
using DoriDeck.TouchPortal;
using ScoreInterface;
using ScoreInterface.Commands;
using Serilog;

namespace DoriDeck.Actions;

/// Syllabifies the lyrics on the clipboard and enters them in Dorico.
internal sealed class InsertLyricsAction(
	DoricoSession session,
	IKeyboardService keyboard,
	IApplicationFocusService focus,
	IClipboardService clipboard,
	ILogger logger) : DoriDeckActionBase(session, logger), ITouchPortalAction
{
	public const string ModeData = "com.sarti2004.dorideck.insert_lyrics.mode";
	public const string LanguageData = "com.sarti2004.dorideck.insert_lyrics.language";
	public const string VerseData = "com.sarti2004.dorideck.insert_lyrics.verse";

	public const string SyllabifyAndInsertMode = "Syllabify and insert";
	public const string SyllabifyOnlyMode = "Syllabify only";
	public const string InsertOnlyMode = "Insert only";
	public const string AutoVerse = "Auto";
	public const string ChorusVerse = "Chorus";

	// Names are what TP shows and sends back, so they must match valueChoices in entry.tp.
	public static readonly IReadOnlyList<string> Modes = [SyllabifyAndInsertMode, SyllabifyOnlyMode, InsertOnlyMode];
	public static readonly IReadOnlyList<(string Name, string Code)> Languages =
		[("English/Russian", "EN"), ("German", "DE"), ("Finnish", "FI"), ("Latin", "Latin")];
	public static readonly IReadOnlyList<string> Verses = [AutoVerse, "Verse 1", "Verse 2", "Verse 3", "Verse 4", ChorusVerse];

	private const int ClipboardRetryCount = 5;
	private const int OperationDelayMilliseconds = 5;
	private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(100);
	private static readonly Regex VerseToken = new(@"^\(Verse\s+(\d+)\)", RegexOptions.IgnoreCase);

	public string Id => "com.sarti2004.dorideck.action.insert_lyrics";

	public async Task ExecuteAsync(ActionData data, CancellationToken cancellationToken)
	{
		var mode = data.GetText(ModeData) ?? SyllabifyAndInsertMode;
		var languageName = data.GetText(LanguageData);
		var language = Languages.FirstOrDefault(item => item.Name == languageName).Code ?? "EN";
		var verse = VerseNumber(data.GetText(VerseData));

		var rawText = (await Task.Run(() => clipboard.ReadText(ClipboardRetryCount, ClipboardRetryDelay), cancellationToken))?.Trim();
		if (string.IsNullOrWhiteSpace(rawText))
		{
			Logger.Warning("Insert lyrics: the clipboard does not contain any text.");
			return;
		}

		// A marker at the start of the lyrics overrides the action's setting.
		var verseMatch = VerseToken.Match(rawText);
		if (verseMatch.Success)
		{
			verse = verseMatch.Groups[1].Value;
			rawText = rawText[verseMatch.Length..].TrimStart();
		}
		else if (rawText.StartsWith("(Chorus)", StringComparison.OrdinalIgnoreCase))
		{
			verse = ChorusVerse;
			rawText = rawText["(Chorus)".Length..];
		}

		var processedText = mode == InsertOnlyMode ? rawText : new LyricProcessor(Logger).Process(rawText, language);

		if (mode == SyllabifyOnlyMode)
		{
			await Task.Run(() => clipboard.WriteText(processedText, ClipboardRetryCount, ClipboardRetryDelay), cancellationToken);
			return;
		}

		await InsertAsync(processedText, verse, cancellationToken);
	}

	private async Task InsertAsync(string text, string verse, CancellationToken cancellationToken)
	{
		var dorico = await GetConnectedRemoteAsync(Session, cancellationToken);
		if (dorico == null)
		{
			Logger.Warning("Not connected to Dorico; skipped inserting lyrics.");
			return;
		}

		var lyricLine = int.TryParse(verse, out var parsedLine) && parsedLine > 0 ? parsedLine : 0;

		// Only moving to a verse above the first uses keystrokes.
		if (lyricLine > 1 && !EnsureKeyboardAccess(keyboard))
		{
			return;
		}

		await focus.ActivateDoricoAsync(cancellationToken);

		if (!Session.HasScore || !Session.HasSelection)
		{
			Logger.Warning("Insert lyrics: select the first note before running this action.");
			return;
		}

		var parts = SplitIntoLyricParts(text);
		if (parts.Length == 0)
		{
			Logger.Warning("Insert lyrics: no lyrics found on the clipboard.");
			return;
		}

		await dorico.SendRequestAsync(new Command("NoteInput.StartLyricInput"), cancellationToken);
		await MoveToVerseAsync(dorico, lyricLine, verse, cancellationToken);
		await EnterPartsAsync(dorico, parts, cancellationToken);
	}

	private async Task MoveToVerseAsync(IScoreInterfaceRemote dorico, int lyricLine, string verse, CancellationToken cancellationToken)
	{
		// Lyric input starts on line 1. Advancing a line needs the current text, which only the popover can give.
		for (var line = 1; line < lyricLine; line++)
		{
			var entry = await ReadSelectedTextAsync(cancellationToken);
			await Task.Delay(20, cancellationToken);
			await dorico.SendRequestAsync(
				new Command(
					"NoteInput.AcceptCurrentLyricInput",
					new CommandParameter("LyricText", EscapeLyricText(entry)),
					new CommandParameter("LyricsEntryAdvanceType", "kIncrementVerseNumber")),
				cancellationToken);
			await Task.Delay(20, cancellationToken);
		}

		if (verse.Equals(ChorusVerse, StringComparison.OrdinalIgnoreCase))
		{
			await Task.Delay(20, cancellationToken);
			await dorico.SendRequestAsync(
				new Command("NoteInput.AcceptCurrentLyricInput", new CommandParameter("LyricsEntryAdvanceType", "kDecrementVerseNumber")),
				cancellationToken);
			await Task.Delay(20, cancellationToken);
		}
	}

	private static async Task EnterPartsAsync(IScoreInterfaceRemote dorico, string[] parts, CancellationToken cancellationToken)
	{
		for (var visited = 0; visited < parts.Length; visited++)
		{
			await Task.Delay(OperationDelayMilliseconds, cancellationToken);

			var lyricText = parts[visited];
			string advanceType;

			if (lyricText == "-" || string.IsNullOrWhiteSpace(lyricText))
			{
				advanceType = GetAdvanceType(lyricText);
				await dorico.SendRequestAsync(
					new Command("NoteInput.AcceptCurrentLyricInput", new CommandParameter("LyricText", string.Empty), new CommandParameter("LyricsEntryAdvanceType", advanceType)),
					cancellationToken);
			}
			else
			{
				advanceType = visited + 1 == parts.Length
					? GetAdvanceType(lyricText, isLastPart: true)
					: GetAdvanceType(parts[visited + 1]);

				await dorico.SendRequestAsync(
					new Command(
						"NoteInput.AcceptCurrentLyricInput",
						new CommandParameter("LyricText", lyricText),
						new CommandParameter("LyricsEntryAdvanceType", advanceType)),
					cancellationToken);

				// The separator after this word is already covered by its advance type.
				visited++;
			}

			if (advanceType != "kEndInput")
			{
				await Task.Delay(OperationDelayMilliseconds, cancellationToken);
				await dorico.SendRequestAsync(new Command("NoteInput.AdvanceLyricInput"), cancellationToken);
			}
		}
	}

	private static string VerseNumber(string? verseName) => verseName switch
	{
		ChorusVerse => ChorusVerse,
		null or AutoVerse => string.Empty,
		_ => verseName.Replace("Verse ", string.Empty, StringComparison.Ordinal),
	};

	internal static string[] SplitIntoLyricParts(string text)
	{
		text = text.Normalize(NormalizationForm.FormC).ReplaceLineEndings(" ");
		text = EscapeLyricText(text
			.Replace(' ', ' ')
			.Replace(' ', ' ')
			.Replace("+", " "));

		return Regex.Matches(text.Trim(), @"\([^)]*\)|[^\s-]+|-|\s")
			.Select(match => match.Value)
			.ToArray();
	}

	// Commands are sent as raw JSON and split on & and comma, so these would otherwise break the message.
	private static string EscapeLyricText(string text) => text
		.Replace(@"\", @"\\\\")
		.Replace("\"", "\\\"")
		.Replace("&", @"\\&")
		.Replace(",", @"\\,");

	private async Task<string> ReadSelectedTextAsync(CancellationToken cancellationToken)
	{
		for (var attempt = 1; attempt <= 2; attempt++)
		{
			keyboard.PressChord(DoriDeckKey.PrimaryModifier, DoriDeckKey.A);

			var sequenceBeforeCopy = clipboard.GetSequence();

			keyboard.PressChord(DoriDeckKey.PrimaryModifier, DoriDeckKey.C);

			if (await WaitForClipboardChangeAsync(sequenceBeforeCopy, TimeSpan.FromMilliseconds(200), cancellationToken))
			{
				var text = await Task.Run(() => clipboard.ReadText(2, TimeSpan.FromMilliseconds(200)), cancellationToken);
				if (text is not null)
				{
					return text;
				}
			}

			if (attempt < 2)
			{
				Logger.Warning("Copy attempt {Attempt} failed while reading the lyric popover; retrying.", attempt);
				await Task.Delay(20, cancellationToken);
			}
		}

		throw new TimeoutException("Can't copy text from clipboard.");
	}

	private async Task<bool> WaitForClipboardChangeAsync(uint previousSequence, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var started = Stopwatch.StartNew();

		while (started.Elapsed < timeout)
		{
			if (clipboard.GetSequence() != previousSequence)
			{
				return true;
			}

			await Task.Delay(20, cancellationToken);
		}

		return false;
	}

	private static string GetAdvanceType(string text, bool isLastPart = false)
	{
		if (isLastPart)
		{
			return "kEndInput";
		}

		return text == "-" ? "kHyphenateCurrentWord" : "kEndOrExtendCurrentWord";
	}
}
