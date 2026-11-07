namespace DoriDeck.Services;

public interface IKeyboardService
{
	/// False means keystrokes would be dropped. On macOS this also asks the user for Accessibility permission.
	bool RequestAccess();

	void Press(DoriDeckKey key);

	void PressChord(
		DoriDeckKey modifier,
		DoriDeckKey key);

	void EnterText(string text);

	bool TryRelease(DoriDeckKey key);

	void ReleaseModifiersSafely();
}
