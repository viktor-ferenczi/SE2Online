using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.VRage.Audio;
using Keen.VRage.Audio.EngineComponents;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Utils;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using VRage.Multiplayer.Replication.CB;

namespace Y51_SE2Online.SyncEngine;

internal static class HarnessFix076
{
	private static bool Has(EngineMember m, Type guard, bool prefix, bool postfix)
	{
		Patches patchInfo = Harmony.GetPatchInfo(EngineMembers.Method(m));
		if (patchInfo == null)
		{
			return false;
		}
		if (!prefix || patchInfo.Prefixes.Any((Patch p) => p.PatchMethod.DeclaringType == guard))
		{
			if (postfix)
			{
				return patchInfo.Postfixes.Any((Patch p) => p.PatchMethod.DeclaringType == guard);
			}
			return true;
		}
		return false;
	}

	public static (bool Ok, string Detail) ToolShadowGuardCheck()
	{
		bool flag = Has(HookTargets.ToolShadowSpawn, typeof(ToolShadowGuard), prefix: false, postfix: true);
		bool flag2 = Has(HookTargets.ToolShadowDespawn, typeof(ToolShadowGuard), prefix: true, postfix: false);
		bool flag3 = Has(HookTargets.ToolsUnregister, typeof(ToolShadowGuard), prefix: true, postfix: true);
		Type typeFromHandle = typeof(ToolsRenderAdapterComponent.TransformMimicTarget);
		bool flag4 = typeFromHandle.IsValueType && typeFromHandle.GetField("Target")?.FieldType.Name == "DEntity";
		ToolsRenderAdapterComponent toolsRenderAdapterComponent = (ToolsRenderAdapterComponent)RuntimeHelpers.GetUninitializedObject(typeof(ToolsRenderAdapterComponent));
		toolsRenderAdapterComponent._firstPersonToolShadowCaster = new WeakEntityReference();
		ToolShadowGuard.SpawnPostfix(toolsRenderAdapterComponent, EntityAllocator.AllocateStub());
		bool flag5 = ToolShadowGuard.DespawnPrefix(toolsRenderAdapterComponent, EntityAllocator.AllocateStub());
		return (Ok: (flag & flag2 & flag3 & flag4 & flag5) && ToolShadowGuard.KeptForNewerTool == 0L && ToolShadowGuard.OwnerCleared == 0, Detail: $"spawn postfix {(flag ? "on" : "MISSING")}, despawn prefix {(flag2 ? "on" : "MISSING")}, UnregisterTool prefix+postfix {(flag3 ? "on" : "MISSING")}; TransformMimicTarget {(flag4 ? "struct with a DEntity Target (the dangling reference)" : "CHANGED")}; no caster -> Keen's method runs: {flag5}; {ToolShadowGuard.Describe()}");
	}

	public static (bool Ok, string Detail) UpstreamRemovalCheck()
	{
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		Entity entity = EntityAllocator.AllocateStub();
		Entity grid = EntityAllocator.AllocateStub();
		Entity left = EntityAllocator.AllocateStub();
		ReplicationCommandBuffer replicationCommandBuffer = new ReplicationCommandBuffer();
		VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader replicationCommandBufferWriterReader = new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(replicationCommandBuffer);
		int assemblyOffset = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
		int typeId = RuntimeDataInfo.Of(typeof(WorldTransform)).TypeId;
		replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(assemblyOffset + 1, entity, grid, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)new byte[4] { 1, 2, 3, 4 }));
		replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_RemoveDataSignal(assemblyOffset + 2, entity, grid, typeId);
		replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_RemoveDataSignal(assemblyOffset + 2, entity, left, typeId);
		ClientUpstream clientUpstream = new ClientUpstream(new HarnessHooks.HarnessLog());
		ObjectSpace space = new ObjectSpace
		{
			Name = "client",
			IdOf = (Entity _) => (uint?)null,
			Resolve = (uint _, bool _) => (Entity?)null
		};
		clientUpstream.Capture(replicationCommandBuffer, (Entity? e) => (e != grid) ? ((uint?)null) : new uint?(11u), space, null, (Entity? e) =>
		{
			uint? result = ((e != grid) ? ((e != left) ? ((uint?)null) : new uint?(12u)) : new uint?(11u));
			return result;
		});
		bool num = clientUpstream.CurrentInput.Count == 0 && clientUpstream.PendingRemovals.Count == 2 && clientUpstream.PendingRemovals.Keys.Any(((uint, ushort) k) => k.Item1 == 11) && clientUpstream.PendingRemovals.Keys.Any(((uint, ushort) k) => k.Item1 == 12) && clientUpstream.DataRemoved == 2 && clientUpstream.Unparsed == 0;
		ClientUpstream clientUpstream2 = new ClientUpstream(new HarnessHooks.HarnessLog());
		clientUpstream2.Capture(replicationCommandBuffer, (Entity? e) => (e != grid) ? ((uint?)null) : new uint?(11u), space, null);
		bool flag = clientUpstream2.PendingRemovals.Count == 1 && clientUpstream2.NotOurs == 1;
		return (Ok: num & flag, Detail: $"value then removal on the driven grid: {((clientUpstream.CurrentInput.Count == 0) ? "value dropped" : "VALUE KEPT")}, {clientUpstream.PendingRemovals.Count} removal(s) queued (grid just left included); without the grace: {clientUpstream2.PendingRemovals.Count} queued, {clientUpstream2.NotOurs} not ours; {clientUpstream.Describe()}");
	}

	public unsafe static (bool Ok, string Detail) AudioGateCheck()
	{
		bool flag = Has(HookTargets.AudioPlay2D, typeof(AudioGate), prefix: true, postfix: false);
		IAudioPlayer audioPlayer = (IAudioPlayer)RuntimeHelpers.GetUninitializedObject(typeof(AudioEngineComponent));
		long muted2D = AudioGate.Muted2D;
		AudioGate.Enter();
		string text;
		try
		{
			audioPlayer.Play(default);
			text = ((AudioGate.Muted2D == muted2D + 1) ? "skipped" : "NOT skipped");
		}
		catch (Exception ex)
		{
			text = "RAN (" + ex.GetType().Name + ")";
		}
		finally
		{
			AudioGate.Exit();
		}
		string value;
		try
		{
			audioPlayer.Play(default);
			value = "ran";
		}
		catch (Exception ex2)
		{
			value = "ran into Keen's code (" + ex2.GetType().Name + ": no audio engine in the harness)";
		}
		bool flag2 = AudioGate.Muted2D == muted2D + 1 && !AudioGate.IsMuted;
		CommandLayout commandLayout = CommandLayouts.ByName("Keen.Game2.Simulation.GameSystems.BlockPlacement.IBlockPlacement+OnBlocksAddedAudioSignal");
		bool enabled = AudioGate.Enabled;
		AudioGate.Enabled = true;
		bool flag3 = false;
		bool flag4 = true;
		if (commandLayout != null && HostWorld.Current != null)
		{
			ReplicationCommandBuffer cb = new ReplicationCommandBuffer();
			byte* payload = stackalloc byte[4];
			AudioRouting.Instance.NoteJoinerBuild(2, MpClock.NowMs());
			flag3 = AudioGate.Mute(commandLayout.Stub, cb, payload, 0, injected: false, s2c: true);
			flag4 = !AudioGate.Mute(commandLayout.Stub, cb, payload, 0, injected: false, s2c: true);
		}
		AudioGate.Enabled = enabled;
		return (Ok: (((flag && text == "skipped") & flag2) && commandLayout != null) & flag3 & flag4, Detail: $"prefix on AudioEngineComponent.Play {(flag ? "on" : "MISSING")}; inside a muted replay: {text}; outside: {value}; build sound stub {commandLayout?.Stub.ToString() ?? "NOT FOUND"}: joiner's build -> host {(flag3 ? "silent" : "PLAYS")}, then host's own -> {(flag4 ? "plays" : "SILENT")}");
	}
}
