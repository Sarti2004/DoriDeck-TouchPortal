#if !WINDOWS
using System.Runtime.InteropServices;

namespace DoriDeck.Services.MacOS;

/// <summary>
/// Reads the title of another process's focused window through the Accessibility API. Requires the
/// plugin host to be granted Accessibility permission (System Settings > Privacy &amp; Security >
/// Accessibility) - the same permission CoreGraphics keyboard injection needs, so there is a single
/// permission story for the whole plugin on macOS.
/// </summary>
internal static class AccessibilityInterop
{
	private const string ApplicationServicesLibrary =
		"/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

	private const int AXErrorSuccess = 0;

	[DllImport(ApplicationServicesLibrary)]
	private static extern IntPtr AXUIElementCreateApplication(int pid);

	[DllImport(ApplicationServicesLibrary)]
	private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

	[DllImport(ApplicationServicesLibrary)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

	/// Whether keystrokes will reach other apps. With prompt, macOS asks the user to grant it if not.
	public static bool IsProcessTrusted(bool prompt)
	{
		var key = CoreFoundationInterop.CreateString("AXTrustedCheckOptionPrompt");
		var options = CoreFoundationInterop.CreateBooleanDictionary(key, prompt);
		try
		{
			return AXIsProcessTrustedWithOptions(options);
		}
		finally
		{
			CoreFoundationInterop.Release(options);
			CoreFoundationInterop.Release(key);
		}
	}

	public static string? GetFocusedWindowTitle(int processId)
	{
		if (processId <= 0)
		{
			return null;
		}

		var appElement = AXUIElementCreateApplication(processId);
		if (appElement == IntPtr.Zero)
		{
			return null;
		}

		try
		{
			var focusedWindowAttribute = CoreFoundationInterop.CreateString("AXFocusedWindow");
			try
			{
				if (AXUIElementCopyAttributeValue(appElement, focusedWindowAttribute, out var windowElement) != AXErrorSuccess
					|| windowElement == IntPtr.Zero)
				{
					return null;
				}

				try
				{
					var titleAttribute = CoreFoundationInterop.CreateString("AXTitle");
					try
					{
						if (AXUIElementCopyAttributeValue(windowElement, titleAttribute, out var titleValue) != AXErrorSuccess
							|| titleValue == IntPtr.Zero)
						{
							return null;
						}

						try
						{
							return CoreFoundationInterop.StringToManaged(titleValue);
						}
						finally
						{
							CoreFoundationInterop.Release(titleValue);
						}
					}
					finally
					{
						CoreFoundationInterop.Release(titleAttribute);
					}
				}
				finally
				{
					CoreFoundationInterop.Release(windowElement);
				}
			}
			finally
			{
				CoreFoundationInterop.Release(focusedWindowAttribute);
			}
		}
		finally
		{
			CoreFoundationInterop.Release(appElement);
		}
	}
}
#endif
