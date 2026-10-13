using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class AudioGate
{
	private const byte KindUnknown = 0;

	private const byte KindNone = 1;

	private const byte KindBuild = 2;

	private const byte KindCharacter = 3;

	[ThreadStatic]
	private static int t_muted;

	private static byte[] _kinds = Array.Empty<byte>();

	public static volatile bool Enabled;

	public static long Muted2D;

	private static int _logged;

	private static int _failed;

	internal static Action? OnPlayed2D;

	public static bool IsMuted => t_muted > 0;

	public static void Enter()
	{
		t_muted++;
	}

	public static void Exit()
	{
		t_muted--;
	}

	public static bool Play2DPrefix()
	{
		if (t_muted <= 0)
		{
			OnPlayed2D?.Invoke();
			return true;
		}
		Interlocked.Increment(ref Muted2D);
		if (Interlocked.Exchange(ref _logged, 1) == 0)
		{
			GameLog.Instance.Info("audio: another player's action played as this player's own (2D) sound; muted here (0.7.6, further ones counted in the status line)");
		}
		return false;
	}

	// Crouch sounds come from a CharacterCrouchTag OnAdded/OnRemoved job (CheckCrouchAfterPhysics), not a
	// replicated signal, so Mute() never sees them. A character nobody here controls is someone else's.
	public static void CrouchPrefix(Component __instance, out bool __state)
	{
		__state = false;
		if (!Enabled)
		{
			return;
		}
		try
		{
			IControllable? controllable = __instance.Entity.TryGet<CharacterControllableComponent>();
			__state = controllable != null && controllable.Controller == null;
		}
		catch (Exception)
		{
			return;
		}
		if (__state)
		{
			t_muted++;
			Interlocked.Increment(ref AudioRouting.Instance.CharacterMuted);
		}
	}

	public static void CrouchFinalizer(bool __state)
	{
		if (__state)
		{
			t_muted--;
		}
	}

	private static byte Kind(int stub)
	{
		byte[] array = _kinds;
		if ((uint)stub < (uint)array.Length && array[stub] != 0)
		{
			return array[stub];
		}
		byte b = 1;
		CommandLayout commandLayout = CommandLayouts.ByStub(stub);
		if (commandLayout != null)
		{
			string shortName = ClientUpstream.Short(commandLayout.Name);
			b = (byte)(AudioRouting.IsBuildSound(shortName) ? 2 : ((!AudioRouting.IsCharacterSound(shortName)) ? 1 : 3));
		}
		if ((uint)stub >= (uint)array.Length)
		{
			byte[] array2 = new byte[Math.Max(stub + 1, Math.Max(1024, array.Length * 2))];
			array.CopyTo(array2, 0);
			array = (_kinds = array2);
		}
		array[stub] = b;
		return b;
	}

	public unsafe static bool Mute(int stub, ReplicationCommandBuffer cb, byte* payload, nint available, bool injected, bool s2c)
	{
		if (!Enabled)
		{
			return false;
		}
		try
		{
			byte b = Kind(stub);
			if (b == 1)
			{
				return false;
			}
			long nowMs = MpClock.NowMs();
			if (injected)
			{
				ClientWorld current = ClientWorld.Current;
				if (current == null)
				{
					return false;
				}
				if (b == 2)
				{
					return !AudioRouting.Instance.TakeOwnBuild(nowMs);
				}
				if (available < 4)
				{
					return false;
				}
				bool flag = current.IsOwnCharacterProxy(cb.ReadObject(Unsafe.ReadUnaligned<int>(payload)));
				if (!flag)
				{
					Interlocked.Increment(ref AudioRouting.Instance.CharacterMuted);
				}
				return !flag;
			}
			HostWorld current2 = HostWorld.Current;
			if (!s2c || current2 == null)
			{
				return false;
			}
			if (b == 2)
			{
				return AudioRouting.Instance.TakeJoinerBuild(nowMs) != 0;
			}
			if (available < 4)
			{
				return false;
			}
			bool flag2 = current2.IsJoinerCharacter(cb.ReadObject(Unsafe.ReadUnaligned<int>(payload)) as Entity);
			if (flag2)
			{
				Interlocked.Increment(ref AudioRouting.Instance.CharacterMuted);
			}
			return flag2;
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _failed, 1) == 0)
			{
				GameLog.Instance.Warn($"audio: deciding whose sound a signal is failed ({ex.GetType().Name}: {ex.Message}); sounds play as in single player");
			}
			Enabled = false;
			return false;
		}
	}

	public static string Describe()
	{
		return $"{AudioRouting.Instance.Describe()}; 2D plays muted {Interlocked.Read(in Muted2D)}";
	}
}
