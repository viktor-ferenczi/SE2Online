using System;
using Keen.VRage.Core.Input;
using Keen.VRage.Input;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Hotkeys read from the game's own keyboard device, so they work wherever the game's input does (Windows and Proton) and only while the game has focus.</summary>
internal static class GameKeys
{
	private const int VkF3 = 114;

	private static bool _menuJoinDown;

	/// <summary>F3 at the main menu joins the last host again.</summary>
	public static bool MenuJoinPressed()
	{
		bool down = IsDown(VkF3);
		bool edge = down && !_menuJoinDown;
		_menuJoinDown = down;
		return edge;
	}

	/// <summary>Key binding codes are Windows virtual-key codes, which is also how the game numbers its keyboard inputs.</summary>
	internal static bool IsDown(int vk)
	{
		try
		{
			if (!InputDeviceManager.HasValue) return false;
			IInputDevice keyboard = Singleton<InputDeviceManager>.Instance.Keyboard;
			return keyboard != null && keyboard.GetDigitalState(new DigitalInput(vk, GenericDeviceClass.Keyboard));
		}
		catch (Exception)
		{
			return false;
		}
	}
}
