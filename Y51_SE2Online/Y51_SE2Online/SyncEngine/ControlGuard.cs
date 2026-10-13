using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers;
using Keen.Game2.Client.WorldObjects.Spectators;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Annotations.Signals;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Multiplayer.Annotations;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal static class ControlGuard
{
	public static volatile bool Enabled;

	internal static IMpLog? Log;

	public static long JoinerDropped;

	public static long HostInjectDropped;

	public static long HostClientDropped;

	public static long HostF6Refused;

	private const string TrySetEntity = "InteractedEntityProviderServerComponent+TrySetEntitySignal";

	private static volatile HashSet<Type>? _index;

	internal static Func<Entity, bool>? IsForeignCharacterHere;

	private static volatile Entity[] _joinerCharacters = Array.Empty<Entity>();

	private static byte[] _stubKinds = Array.Empty<byte>();

	[ThreadStatic]
	private static List<(int Offset, Segment Seg)>? _scratch;

	public static volatile bool HostFlightGate;

	public static long HostFlightSkipped;

	public static long HostShipActionsSkipped;

	private static volatile Entity[] _joinerDriven = Array.Empty<Entity>();

	private static int[]? _flightTypeIds;

	public static string IndexState { get; private set; } = "not built";

	public static void Start(IMpLog log)
	{
		Log = log;
		Enabled = true;
		Task.Run(() =>
		{
			BuildIndex(typeof(InteractedEntityProviderServerComponent).Assembly);
		});
	}

	internal static void BuildIndex(Assembly asm)
	{
		HashSet<Type> hashSet = new HashSet<Type>();
		try
		{
			Type[] types;
			try
			{
				types = asm.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types;
			}
			Type[] array = types;
			foreach (Type type in array)
			{
				if (type == null)
				{
					continue;
				}
				MethodInfo[] methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (MethodInfo methodInfo in methods)
				{
					bool flag = false;
					foreach (CustomAttributeData customAttribute in methodInfo.CustomAttributes)
					{
						if (customAttribute.AttributeType == typeof(Server) && customAttribute.ConstructorArguments.Count == 1 && customAttribute.ConstructorArguments[0].Value as string == "ValidateControlled")
						{
							flag = true;
						}
					}
					if (!flag)
					{
						continue;
					}
					foreach (CustomAttributeData customAttribute2 in methodInfo.CustomAttributes)
					{
						if (typeof(SignalAttribute).IsAssignableFrom(customAttribute2.AttributeType))
						{
							hashSet.Add(customAttribute2.AttributeType);
						}
					}
				}
			}
			_index = hashSet;
			IndexState = $"{hashSet.Count} controller-only signals";
			Log?.Info("control guard: " + IndexState + " (handlers with [Server(\"ValidateControlled\")]); only a character's own player sends them");
		}
		catch (Exception ex2)
		{
			IndexState = "index failed (" + ex2.GetType().Name + "); TrySetEntity only";
			Log?.Warn("control guard: " + IndexState);
		}
	}

	internal static bool IsControllerOnly(CommandLayout? l)
	{
		if (l == null || l.Kind != CommandKind.Signal)
		{
			return false;
		}
		HashSet<Type> index = _index;
		if (index != null && l.SignalType != null)
		{
			return index.Contains(l.SignalType);
		}
		return l.Name.EndsWith("InteractedEntityProviderServerComponent+TrySetEntitySignal", StringComparison.Ordinal);
	}

	internal static bool DropUpstream(CommandLayout l, object? target)
	{
		if (!Enabled || !(target is Entity arg) || !IsControllerOnly(l))
		{
			return false;
		}
		Func<Entity, bool> isForeignCharacterHere = IsForeignCharacterHere;
		if (isForeignCharacterHere == null || !isForeignCharacterHere(arg))
		{
			return false;
		}
		Interlocked.Increment(ref JoinerDropped);
		return true;
	}

	internal static void SetJoinerCharacters(Entity[] characters)
	{
		_joinerCharacters = characters;
	}

	internal static bool IsJoinerCharacter(Entity? e)
	{
		if (e == null)
		{
			return false;
		}
		Entity[] joinerCharacters = _joinerCharacters;
		for (int i = 0; i < joinerCharacters.Length; i++)
		{
			if (joinerCharacters[i] == e)
			{
				return true;
			}
		}
		return false;
	}

	internal unsafe static int HostClientSkip(int stub, byte* payload, nint available, ReplicationCommandBuffer cb)
	{
		if (!Enabled)
		{
			return -1;
		}
		byte[] array = _stubKinds;
		if (stub < 0)
		{
			return -1;
		}
		if (stub >= array.Length)
		{
			byte[] array2 = new byte[Math.Max(stub + 1, 1024)];
			Array.Copy(array, array2, array.Length);
			array = (_stubKinds = array2);
		}
		byte b = array[stub];
		CommandLayout commandLayout = null;
		if (b == 0)
		{
			commandLayout = CommandLayouts.ByStub(stub);
			b = (byte)((IsControllerOnly(commandLayout) && commandLayout.Segments.Length != 0 && commandLayout.Segments[0].Kind == SegmentKind.Ref) ? 1 : 2);
			if (_index != null || b == 1)
			{
				array[stub] = b;
			}
		}
		if (b != 1 || _joinerCharacters.Length == 0)
		{
			return -1;
		}
		if (available < 4)
		{
			return -1;
		}
		int num = *(int*)payload;
		if (num < 0 || !(cb.ReadObject(num) is Entity e) || !IsJoinerCharacter(e))
		{
			return -1;
		}
		if (commandLayout == null)
		{
			commandLayout = CommandLayouts.ByStub(stub);
		}
		int num2 = commandLayout?.Frame(new ReadOnlySpan<byte>(payload, (int)Math.Min(int.MaxValue, available)), _scratch ?? (_scratch = new List<(int, Segment)>())) ?? (-1);
		if (num2 < 0 || num2 > available)
		{
			return -1;
		}
		if (Interlocked.Increment(ref HostClientDropped) <= 3)
		{
			Log?.Info("control guard: the host's game sent " + ClientUpstream.Short(commandLayout.Name) + " for a joiner's character; skipped (only its own player sets that)");
		}
		return num2;
	}

	public static void HostNextCharacterPostfix(SpectatingModesSessionComponent __instance, PlayerControllerComponent controller, ref IControllable? __result)
	{
		if (!Enabled || __result == null)
		{
			return;
		}
		try
		{
			if (!IsJoinerCharacter(__result.Entity))
			{
				return;
			}
			Entity entity = __result.Entity;
			IControllable controllable = null;
			List<Entity> list = new List<Entity>();
			Session session = HostWorld.Current?.HostClientSession;
			if (session != null && session.TryFillAliveCharacters(list, preserveOrder: true))
			{
				int num = Math.Max(0, list.IndexOf(entity));
				for (int i = 1; i <= list.Count; i++)
				{
					if (controllable != null)
					{
						break;
					}
					Entity entity2 = list[(num + i) % list.Count];
					if (!IsJoinerCharacter(entity2))
					{
						IControllable controllable2 = entity2.AsInterface<IControllable>();
						if (controllable2 != null && (controllable2.Controller == null || controllable2.Controller == controller))
						{
							controllable = controllable2;
						}
					}
				}
			}
			__result = controllable;
			if (Interlocked.Increment(ref HostF6Refused) <= 3)
			{
				Log?.Info($"host: F6 / spectator keys would take a joiner's character ({entity}); " + ((controllable != null) ? "going to the host's own character instead" : "ignored"));
			}
		}
		catch (Exception ex)
		{
			Log?.WarnOnce("host-next-character", $"host: F6 filter failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	internal static void SetJoinerDriven(Entity[] grids)
	{
		_joinerDriven = grids;
	}

	internal static bool IsJoinerDriven(Entity? e)
	{
		if (e == null)
		{
			return false;
		}
		Entity[] joinerDriven = _joinerDriven;
		for (int i = 0; i < joinerDriven.Length; i++)
		{
			if (joinerDriven[i] == e)
			{
				return true;
			}
		}
		return false;
	}

	private static int[] FlightTypeIds()
	{
		object obj = _flightTypeIds;
		if (obj == null)
		{
			obj = new int[4]
			{
				RuntimeDataInfo.Of(typeof(ControlData)).TypeId,
				RuntimeDataInfo.Of(typeof(AngularControlData)).TypeId,
				RuntimeDataInfo.Of(typeof(TargetControlData)).TypeId,
				RuntimeDataInfo.Of(typeof(ControlReferenceOrientation)).TypeId
			};
			_flightTypeIds = (int[]?)obj;
		}
		return (int[])obj;
	}

	internal unsafe static int HostFlightSkip(bool removal, byte* payload, nint available, ReplicationCommandBuffer cb)
	{
		if (!HostFlightGate || _joinerDriven.Length == 0 || available < (removal ? 12 : 16))
		{
			return -1;
		}
		int num = ((int*)payload)[1];
		int value = ((int*)payload)[2];
		if (Array.IndexOf(FlightTypeIds(), value) < 0 || num < 0 || !(cb.ReadObject(num) is Entity e) || !IsJoinerDriven(e))
		{
			return -1;
		}
		int result = 12;
		if (!removal)
		{
			int num2 = ((int*)payload)[3];
			if (num2 < 0 || (nint)16 + (nint)num2 > available)
			{
				return -1;
			}
			result = 16 + num2;
		}
		if (Interlocked.Increment(ref HostFlightSkipped) == 1)
		{
			Log?.Info("host: the host's player sits in a passenger seat of a ship a joiner flies; its flight input is not applied (looking around stays)");
		}
		return result;
	}

	public static bool HostCockpitPrefix(CockpitInputHandlerComponent __instance)
	{
		try
		{
			if (!HostFlightGate || _joinerDriven.Length == 0 || !IsJoinerDriven(__instance._observedBlock?.Grid?.Entity))
			{
				return true;
			}
			if (Interlocked.Increment(ref HostShipActionsSkipped) <= 3)
			{
				Log?.Info("host: power / dampeners / lights / brakes / boost from a passenger seat ignored (a joiner flies this ship)");
			}
			return false;
		}
		catch (Exception)
		{
			return true;
		}
	}

	[ThreadStatic]
	private static int t_panelClose;

	public static long PanelDetailsKept;

	// Keen's DetachControlPanelList calls DetachAllBlockDetailsFromEntity for every block, which disposes
	// every player's BlockDetailModels on those blocks ("This detaches the models from all clients!").
	// On a player's close (Grids still attached; the grid-removed path arrives with Grids empty) skip that:
	// the closing client detaches its own detail models right after (ControlPanelViewModel.OnDispose).
	public static void PanelDetachPrefix(ControlPanelListModel model, out bool __state)
	{
		__state = model != null && model.Grids.Count > 0;
		if (__state)
		{
			t_panelClose++;
		}
	}

	public static void PanelDetachFinalizer(bool __state)
	{
		if (__state)
		{
			t_panelClose--;
		}
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, object> _watchedBlocks = new();

	public static bool PanelDetailsPrefix(Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.StreamedControlPanelSessionComponent __instance, Entity entity)
	{
		if (t_panelClose <= 0)
		{
			return true;
		}
		Interlocked.Increment(ref PanelDetailsKept);
		// The closed panel's own block-removed subscription goes with it, so a detail model kept here would outlive
		// its block and the next 1 s refresh throws on the deleted block (it crashed the server). Detach on removal.
		try
		{
			if (!_watchedBlocks.TryGetValue(entity, out _))
			{
				_watchedBlocks.Add(entity, __instance);
				entity.GetSession().OnBeforeEntityRemovedFromScene.Add(entity, removed =>
				{
					_watchedBlocks.Remove(removed);
					try { __instance.DetachAllBlockDetailsFromEntity(removed); }
					catch (Exception) { }
				});
			}
		}
		catch (Exception)
		{
			return true;
		}
		return false;
	}

	internal static bool IsHeldByForeignCharacter(Entity? e)
	{
		Func<Entity, bool>? foreign = IsForeignCharacterHere;
		if (!Enabled || foreign == null)
		{
			return false;
		}
		for (Entity? p = e?.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent; p != null; p = p.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent)
		{
			if (p.TryGet<Keen.Game2.Client.WorldObjects.Character.CharacterControllableComponent>() != null)
			{
				return foreign(p);
			}
		}
		return false;
	}

	public static string Describe()
	{
		return $"0.8.2 control guard ({IndexState}): dropped here {JoinerDropped} (joiner, other players' characters), host: joiners' {HostInjectDropped}, host game's {HostClientDropped}, F6 refused {HostF6Refused}; host passenger seat: flight data skipped {HostFlightSkipped}, ship actions skipped {HostShipActionsSkipped}; control panel close kept others' details {PanelDetailsKept}";
	}
}
