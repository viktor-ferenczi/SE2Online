using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Physics.BreakHandler;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using VRage.Multiplayer.Replication.CB;

namespace Y51_SE2Online.SyncEngine;

internal static class HarnessFix082
{
	public static (bool Ok, string Detail) MarkedDataCheck()
	{
		Entity session = EntityAllocator.AllocateStub();
		Entity target = EntityAllocator.AllocateStub();
		int baseIndex = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
		byte[] bytes = new byte[9] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
		string text = Cmp(removal: false);
		string text2 = Cmp(removal: true);
		return (Ok: text == "identical + marker" && text2 == text, Detail: "PostData " + text + ", RemoveData " + text2);
		string Cmp(bool removal)
		{
			//IL_0059: Unknown result type (might be due to invalid IL or missing references)
			ReplicationCommandBuffer replicationCommandBuffer = new ReplicationCommandBuffer();
			ReplicationCommandBuffer replicationCommandBuffer2 = new ReplicationCommandBuffer();
			VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader replicationCommandBufferWriterReader = new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(replicationCommandBuffer);
			if (removal)
			{
				replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_RemoveDataSignal(baseIndex + 2, session, target, 77);
			}
			else
			{
				replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(baseIndex + 1, session, target, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(77, (ReadOnlySpan<byte>)bytes));
			}
			HostWorld.WriteMarkedData(replicationCommandBuffer2, baseIndex + ((!removal) ? 1 : 2), session, target, 77, removal ? null : bytes);
			byte[] array = CommandBuffers.Bytes(replicationCommandBuffer, 0);
			byte[] array2 = CommandBuffers.Bytes(replicationCommandBuffer2, 0);
			if (array.Length != array2.Length || !array.AsSpan(4).SequenceEqual(array2.AsSpan(4)) || BitConverter.ToInt32(array2, 0) != 1580056798 || replicationCommandBuffer2.ReadObject(BitConverter.ToInt32(array2, 12)) != target)
			{
				return $"DIFFERENT ({array.Length} vs {array2.Length} B)";
			}
			return "identical + marker";
		}
	}

	public static (bool Ok, string Detail) DataTypesCheck()
	{
		DataTypeRegistry.EnsureDone(new HarnessHooks.HarnessLog());
		int value = TypeId<AsymmetricDestructionData>.Value;
		int value2 = TypeId<BreakableBodyMinVelocityOverride>.Value;
		int value3 = TypeId<DEntity>.Value;
		return (Ok: value > 0 && value2 > 0 && value3 == 0 && DataTypeRegistry.Registered + DataTypeRegistry.AlreadyKnown > 200, Detail: $"{DataTypeRegistry.State}; AsymmetricDestructionData {DataTypeRegistry.BeforeAsymmetric} -> {value}, BreakableBodyMinVelocityOverride {DataTypeRegistry.BeforeVelocityOverride} -> {value2}, DEntity {value3}");
	}

	private static bool Patched(EngineMember m, Type guard)
	{
		Patches patchInfo = Harmony.GetPatchInfo(EngineMembers.Method(m));
		return patchInfo?.Prefixes.Concat(patchInfo.Postfixes).Concat(patchInfo.Finalizers).Any((Patch p) => p.PatchMethod.DeclaringType == guard) ?? false;
	}

	public unsafe static (bool Ok, string Detail) ControlGuardCheck()
	{
		ControlGuard.BuildIndex(typeof(InteractedEntityProviderServerComponent).Assembly);
		CommandLayout commandLayout = CommandLayouts.ByName("Keen.Game2.Simulation.WorldObjects.Tools.InteractedEntityProviderServerComponent+TrySetEntitySignal");
		bool flag = commandLayout != null && ControlGuard.IsControllerOnly(commandLayout);
		Entity entity = EntityAllocator.AllocateStub();
		Entity entity2 = EntityAllocator.AllocateStub();
		ControlGuard.SetJoinerCharacters(new Entity[1] { entity });
		bool enabled = ControlGuard.Enabled;
		ControlGuard.Enabled = true;
		ReplicationCommandBuffer replicationCommandBuffer = new ReplicationCommandBuffer();
		byte* ptr = stackalloc byte[32];
		new Span<byte>(ptr, 32).Clear();
		*(int*)ptr = replicationCommandBuffer.StoreObject((object)entity);
		((int*)ptr)[1] = -1;
		int num = ((commandLayout != null) ? ControlGuard.HostClientSkip(commandLayout.Stub, ptr, 32, replicationCommandBuffer) : (-2));
		*(int*)ptr = replicationCommandBuffer.StoreObject((object)entity2);
		int num2 = ((commandLayout != null) ? ControlGuard.HostClientSkip(commandLayout.Stub, ptr, 32, replicationCommandBuffer) : (-2));
		Entity entity3 = EntityAllocator.AllocateStub();
		Entity entity4 = EntityAllocator.AllocateStub();
		ControlGuard.SetJoinerDriven(new Entity[1] { entity3 });
		bool hostFlightGate = ControlGuard.HostFlightGate;
		ControlGuard.HostFlightGate = true;
		int typeId = RuntimeDataInfo.Of(typeof(ControlData)).TypeId;
		byte* ptr2 = stackalloc byte[24];
		*(int*)ptr2 = -1;
		((int*)ptr2)[1] = replicationCommandBuffer.StoreObject((object)entity3);
		((int*)ptr2)[2] = typeId;
		((int*)ptr2)[3] = 8;
		int num3 = ControlGuard.HostFlightSkip(removal: false, ptr2, 24, replicationCommandBuffer);
		((int*)ptr2)[1] = replicationCommandBuffer.StoreObject((object)entity4);
		int num4 = ControlGuard.HostFlightSkip(removal: false, ptr2, 24, replicationCommandBuffer);
		ControlGuard.SetJoinerDriven(Array.Empty<Entity>());
		ControlGuard.SetJoinerCharacters(Array.Empty<Entity>());
		ControlGuard.Enabled = enabled;
		ControlGuard.HostFlightGate = hostFlightGate;
		bool flag2 = Patched(HookTargets.NextCharacter, typeof(ControlGuard));
		bool flag3 = new EngineMember[5]
		{
			HookTargets.CockpitTogglePower,
			HookTargets.CockpitToggleDampeners,
			HookTargets.CockpitToggleLights,
			HookTargets.CockpitToggleBrakes,
			HookTargets.CockpitBoost
		}.All((EngineMember m) => Patched(m, typeof(ControlGuard)));
		bool flag4 = Patched(HookTargets.GpsDrawHud, typeof(NametagOverlay));
		bool flag5 = Patched(HookTargets.RenderDestroyEntity, typeof(BuildGuards));
		return (Ok: (flag && num == 32 && num2 == -1 && num3 == 24 && num4 == -1) & flag2 & flag3 & flag4 & flag5, Detail: $"{ControlGuard.IndexState}, TrySetEntity {(flag ? "controller-only" : "NOT INDEXED")}; host game's TrySetEntity for a joiner's character: {((num == 32) ? "skipped (32 B)" : $"NOT skipped ({num})")}, for its own: {((num2 == -1) ? "runs" : "SKIPPED")}; host ControlData on a joiner's ship: {((num3 == 24) ? "skipped" : $"NOT skipped ({num3})")}, on another: {((num4 == -1) ? "runs" : "SKIPPED")}; patches: F6 {flag2}, cockpit keys {flag3}, HUD draw {flag4}, render destroy {flag5}");
	}
}
