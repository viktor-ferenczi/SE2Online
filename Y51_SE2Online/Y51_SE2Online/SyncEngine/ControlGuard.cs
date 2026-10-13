using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers;
using Keen.Game2.Client.WorldObjects.Character;
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
		Task.Run(() => BuildIndex(typeof(InteractedEntityProviderServerComponent).Assembly));
	}

	internal static void BuildIndex(Assembly asm)
	{
		var signals = new HashSet<Type>();
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
			// Types that failed to load come back as null entries.
			foreach (Type type in types)
			{
				if (type == null)
				{
					continue;
				}
				foreach (MethodInfo method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					bool validatesController = false;
					foreach (CustomAttributeData attr in method.CustomAttributes)
					{
						if (attr.AttributeType == typeof(Server) && attr.ConstructorArguments.Count == 1 && attr.ConstructorArguments[0].Value as string == "ValidateControlled")
						{
							validatesController = true;
						}
					}
					if (!validatesController)
					{
						continue;
					}
					foreach (CustomAttributeData attr in method.CustomAttributes)
					{
						if (typeof(SignalAttribute).IsAssignableFrom(attr.AttributeType))
						{
							signals.Add(attr.AttributeType);
						}
					}
				}
			}
			_index = signals;
			IndexState = $"{signals.Count} controller-only signals";
			Log?.Info("control guard: " + IndexState + " (handlers with [Server(\"ValidateControlled\")]); only a character's own player sends them");
		}
		catch (Exception ex)
		{
			IndexState = "index failed (" + ex.GetType().Name + "); TrySetEntity only";
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
		return l.Name.EndsWith(TrySetEntity, StringComparison.Ordinal);
	}

	internal static bool DropUpstream(CommandLayout l, object? target)
	{
		if (!Enabled || target is not Entity entity || !IsControllerOnly(l))
		{
			return false;
		}
		Func<Entity, bool>? isForeign = IsForeignCharacterHere;
		if (isForeign == null || !isForeign(entity))
		{
			return false;
		}
		Interlocked.Increment(ref JoinerDropped);
		return true;
	}

	internal static void SetJoinerCharacters(Entity[] characters) => _joinerCharacters = characters;

	internal static bool IsJoinerCharacter(Entity? e)
	{
		if (e == null)
		{
			return false;
		}
		Entity[] characters = _joinerCharacters;
		for (int i = 0; i < characters.Length; i++)
		{
			if (characters[i] == e)
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
		byte[] kinds = _stubKinds;
		if (stub < 0)
		{
			return -1;
		}
		if (stub >= kinds.Length)
		{
			byte[] grown = new byte[Math.Max(stub + 1, 1024)];
			Array.Copy(kinds, grown, kinds.Length);
			kinds = _stubKinds = grown;
		}
		// Per stub: 0 = not classified yet, 1 = controller-only signal whose first segment is the target entity, 2 = anything else.
		byte kind = kinds[stub];
		CommandLayout layout = null;
		if (kind == 0)
		{
			layout = CommandLayouts.ByStub(stub);
			kind = (byte)(IsControllerOnly(layout) && layout.Segments.Length != 0 && layout.Segments[0].Kind == SegmentKind.Ref ? 1 : 2);
			// Until the signal index is built a "2" may be wrong, so it is not cached.
			if (_index != null || kind == 1)
			{
				kinds[stub] = kind;
			}
		}
		if (kind != 1 || _joinerCharacters.Length == 0)
		{
			return -1;
		}
		if (available < 4)
		{
			return -1;
		}
		int objectIndex = *(int*)payload;
		if (objectIndex < 0 || cb.ReadObject(objectIndex) is not Entity e || !IsJoinerCharacter(e))
		{
			return -1;
		}
		if (layout == null)
		{
			layout = CommandLayouts.ByStub(stub);
		}
		int length = layout?.Frame(new ReadOnlySpan<byte>(payload, (int)Math.Min(int.MaxValue, available)), _scratch ??= new List<(int, Segment)>()) ?? -1;
		if (length < 0 || length > available)
		{
			return -1;
		}
		if (Interlocked.Increment(ref HostClientDropped) <= 3)
		{
			Log?.Info("control guard: the host's game sent " + ClientUpstream.Short(layout.Name) + " for a joiner's character; skipped (only its own player sets that)");
		}
		return length;
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
			Entity skipped = __result.Entity;
			IControllable next = null;
			var alive = new List<Entity>();
			Session session = HostWorld.Current?.HostClientSession;
			if (session != null && session.TryFillAliveCharacters(alive, preserveOrder: true))
			{
				// Walk the alive characters after the skipped one, wrapping, for the first free non-joiner character.
				int start = Math.Max(0, alive.IndexOf(skipped));
				for (int i = 1; i <= alive.Count; i++)
				{
					if (next != null)
					{
						break;
					}
					Entity candidate = alive[(start + i) % alive.Count];
					if (!IsJoinerCharacter(candidate))
					{
						IControllable controllable = candidate.AsInterface<IControllable>();
						if (controllable != null && (controllable.Controller == null || controllable.Controller == controller))
						{
							next = controllable;
						}
					}
				}
			}
			__result = next;
			if (Interlocked.Increment(ref HostF6Refused) <= 3)
			{
				Log?.Info($"host: F6 / spectator keys would take a joiner's character ({skipped}); " + (next != null ? "going to the host's own character instead" : "ignored"));
			}
		}
		catch (Exception ex)
		{
			Log?.WarnOnce("host-next-character", $"host: F6 filter failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	internal static void SetJoinerDriven(Entity[] grids) => _joinerDriven = grids;

	internal static bool IsJoinerDriven(Entity? e)
	{
		if (e == null)
		{
			return false;
		}
		Entity[] grids = _joinerDriven;
		for (int i = 0; i < grids.Length; i++)
		{
			if (grids[i] == e)
			{
				return true;
			}
		}
		return false;
	}

	private static int[] FlightTypeIds()
	{
		return _flightTypeIds ??= new int[]
		{
			RuntimeDataInfo.Of(typeof(ControlData)).TypeId,
			RuntimeDataInfo.Of(typeof(AngularControlData)).TypeId,
			RuntimeDataInfo.Of(typeof(TargetControlData)).TypeId,
			RuntimeDataInfo.Of(typeof(ControlReferenceOrientation)).TypeId
		};
	}

	internal unsafe static int HostFlightSkip(bool removal, byte* payload, nint available, ReplicationCommandBuffer cb)
	{
		if (!HostFlightGate || _joinerDriven.Length == 0 || available < (removal ? 12 : 16))
		{
			return -1;
		}
		int objectIndex = ((int*)payload)[1];
		int typeId = ((int*)payload)[2];
		if (Array.IndexOf(FlightTypeIds(), typeId) < 0 || objectIndex < 0 || cb.ReadObject(objectIndex) is not Entity e || !IsJoinerDriven(e))
		{
			return -1;
		}
		int result = 12;
		if (!removal)
		{
			int dataLength = ((int*)payload)[3];
			if (dataLength < 0 || 16 + (nint)dataLength > available)
			{
				return -1;
			}
			result = 16 + dataLength;
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

	private static readonly ConditionalWeakTable<Entity, object> _watchedBlocks = new();

	public static bool PanelDetailsPrefix(StreamedControlPanelSessionComponent __instance, Entity entity)
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
		for (Entity? p = e?.TryGet<HierarchyComponent>()?.Parent; p != null; p = p.TryGet<HierarchyComponent>()?.Parent)
		{
			if (p.TryGet<CharacterControllableComponent>() != null)
			{
				return foreign(p);
			}
		}
		return false;
	}

	public static string Describe() => $"0.8.2 control guard ({IndexState}): dropped here {JoinerDropped} (joiner, other players' characters), host: joiners' {HostInjectDropped}, host game's {HostClientDropped}, F6 refused {HostF6Refused}; host passenger seat: flight data skipped {HostFlightSkipped}, ship actions skipped {HostShipActionsSkipped}; control panel close kept others' details {PanelDetailsKept}";
}
