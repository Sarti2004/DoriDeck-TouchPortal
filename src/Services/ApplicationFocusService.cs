using System.Diagnostics;
using DoriDeck.Services.MacOS;
using DoriDeck.Services.Windows;

namespace DoriDeck.Services;

public sealed class ApplicationFocusService : IApplicationFocusService
{
	private const int ActivateAttempts = 5;
	private static readonly TimeSpan ActivateRetryDelay = TimeSpan.FromMilliseconds(50);

	public async Task<bool> ActivateDoricoAsync(CancellationToken cancellationToken)
	{
		if (OperatingSystem.IsMacOS())
		{
			return NSRunningApplicationInterop.ActivateDorico();
		}

		if (OperatingSystem.IsWindows())
		{
			// The foreground window can take a moment to change.
			for (var attempt = 0; attempt < ActivateAttempts; attempt++)
			{
				if (WindowsApplicationInterop.ActivateDorico())
				{
					return true;
				}

				await Task.Delay(ActivateRetryDelay, cancellationToken);
			}
		}

		return false;
	}

	public bool IsForeground(string processNamePrefix)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(processNamePrefix);

		if (OperatingSystem.IsMacOS())
		{
			// MacOS activates Dorico once. Nothing to check here.
			return true;
		}

		var window =
			WindowAndClipboardInterop.GetForegroundWindowHandle();

		if (window == IntPtr.Zero)
		{
			return false;
		}

		var processId =
			WindowAndClipboardInterop.GetForegroundWindowProcessId(window);

		try
		{
			using var process = Process.GetProcessById(
				checked((int)processId));

			return process.ProcessName.StartsWith(
				processNamePrefix,
				StringComparison.OrdinalIgnoreCase);
		}
		catch (ArgumentException)
		{
			// The process may have exited between obtaining its ID
			// and opening the Process object.
			return false;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	public void EnsureForeground(
		string processNamePrefix,
		string? errorMessage = null)
	{
		if (!IsForeground(processNamePrefix))
		{
			throw new InvalidOperationException(
				errorMessage ??
				$"{processNamePrefix} must be the foreground application.");
		}
	}
}