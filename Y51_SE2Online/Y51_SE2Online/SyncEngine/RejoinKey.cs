using System;
using System.Runtime.InteropServices;

namespace Y51_SE2Online.SyncEngine;

internal static class RejoinKey
{
	private const int VkRejoin = 114;

	private static bool _down;

	internal static Func<bool>? Override;

	public static bool Pressed()
	{
		bool flag;
		try
		{
			flag = Override?.Invoke() ?? (OperatingSystem.IsWindows() && IsDown(114) && OwnWindowInFront());
		}
		catch (Exception)
		{
			flag = false;
		}
		bool result = flag && !_down;
		_down = flag;
		return result;
	}

	internal static bool Edge(int vk, ref bool down)
	{
		bool flag;
		try
		{
			flag = OperatingSystem.IsWindows() && IsDown(vk) && OwnWindowInFront();
		}
		catch (Exception)
		{
			flag = false;
		}
		bool result = flag && !down;
		down = flag;
		return result;
	}

	internal static bool IsDown(int vk)
	{
		return (GetAsyncKeyState(vk) & 0x8000) != 0;
	}

	internal static bool OwnWindowInFront()
	{
		nint foregroundWindow = GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero)
		{
			return false;
		}
		GetWindowThreadProcessId(foregroundWindow, out var processId);
		return processId == (uint)Environment.ProcessId;
	}

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
