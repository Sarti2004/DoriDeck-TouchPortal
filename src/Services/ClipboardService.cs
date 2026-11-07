namespace DoriDeck.Services;

/// Clipboard access for actions, so tests never touch the real clipboard.
internal interface IClipboardService
{
	string? ReadText(int retryCount, TimeSpan retryDelay);

	void WriteText(string text, int retryCount, TimeSpan retryDelay);

	/// Changes whenever the clipboard's content changes.
	uint GetSequence();
}

internal sealed class ClipboardService : IClipboardService
{
	public string? ReadText(int retryCount, TimeSpan retryDelay) => ClipboardSnapshot.ReadUnicodeText(retryCount, retryDelay);

	public void WriteText(string text, int retryCount, TimeSpan retryDelay) => ClipboardSnapshot.WriteUnicodeText(text, retryCount, retryDelay);

	public uint GetSequence() => WindowAndClipboardInterop.GetClipboardSequence();
}
