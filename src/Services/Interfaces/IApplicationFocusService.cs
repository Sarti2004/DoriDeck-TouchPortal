namespace DoriDeck.Services;

public interface IApplicationFocusService
{
	/// Brings Dorico to the front. False means keystrokes could reach another app.
	Task<bool> ActivateDoricoAsync(CancellationToken cancellationToken);

	bool IsForeground(string processNamePrefix);

	void EnsureForeground(
		string processNamePrefix,
		string? errorMessage = null);
}