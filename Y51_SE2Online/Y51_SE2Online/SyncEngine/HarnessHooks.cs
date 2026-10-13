using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.Camera;
using Keen.Game2.Simulation.GameSystems.Control;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Progression;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.Game2.Simulation.StreamedUI.Terminal.Production;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Doors;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Sound;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.DCS.Scenes;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Extensions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.UI;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Annotations;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.SecureIds;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Voxels.Components;
using Keen.VRage.Voxels.Storage.Operations;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Probe;
using Y51_SE2Online.Sync;
using VRage.Multiplayer.Replication.CB;

namespace Y51_SE2Online.SyncEngine;

internal static class HarnessHooks
{
	internal sealed class HarnessLog : IMpLog
	{
		public void Info(string message)
		{
		}

		public void Warn(string message)
		{
		}

		public void Error(string message)
		{
		}
	}

	private delegate void BlackboardWriter(object writer, int signalId, Entity entity, ReadOnlySpan<(StringHash, float)> floats, ReadOnlySpan<(StringHash, int)> ints, ReadOnlySpan<(StringHash, StringHash)> strings, ReadOnlySpan<(StringHash, bool)> bools);

	internal struct HarnessKeyed
	{
		public StringId Key;

		public int Count;

		public Entity? Who;
	}

	private sealed class FakeClientNarrator : INetworkStoryNarrator
	{
		public object? Moved;

		public object? Result;

		public Exception? Error;

		public int Finishes;

		public void Start<TResult>(NetworkStoryContext context, IAsyncStateMachine stateMachine)
		{
			stateMachine.MoveNext();
		}

		public unsafe void MoveToServer(NetworkStoryContext context, IAsyncStateMachine stateMachine, ServerRequest server)
		{
			//IL_0019: Expected native int or pointer, but got VT
			server.Result = new ReplicationEventContext
			{
				IsLocal = false,
				SourceId = new ClientId(1)
			};
			Moved = stateMachine;
		}

		public void MoveToClient(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client)
		{
			stateMachine.MoveNext();
		}

		public void Finish<TResult>(NetworkStoryContext context, TResult result, Exception? exception)
		{
			Finishes++;
			Result = result;
			Error = exception;
		}
	}

	private sealed class FakeSink : IStorySink
	{
		public readonly ConcurrentQueue<object> ToClient = new ConcurrentQueue<object>();

		public void SendStoryMove(PeerId peer, StoryMove move)
		{
			ToClient.Enqueue(StoryMove.Decode(move.Encode()));
		}

		public void SendStoryFinish(PeerId peer, StoryFinish finish)
		{
			ToClient.Enqueue(StoryFinish.Decode(finish.Encode()));
		}
	}

	private sealed class TestStory<T> : NetworkStory<T>
	{
		public object? Result;

		public Exception? Error;

		public int Sets;

		public void SetResult(T result, Exception? exception)
		{
			Sets++;
			Result = result;
			Error = exception;
		}

		protected override Task GetVoidTask()
		{
			throw new NotSupportedException();
		}

		protected override Task<T> GetTask()
		{
			throw new NotSupportedException();
		}
	}

	private sealed class ObsHarnessLog : IMpLog
	{
		public readonly List<string> Lines = new List<string>();

		public void Info(string message)
		{
			Lines.Add("INFO " + message);
		}

		public void Warn(string message)
		{
			Lines.Add("WARN " + message);
		}

		public void Error(string message)
		{
			Lines.Add("ERROR " + message);
		}
	}

	private sealed class Npc : INpcIdentityProvider
	{
		public bool IsNpc(IdentityId id)
		{
			return PlayerIdentities.IsNpc(id);
		}

		public bool IsNpc(Entity entity)
		{
			return false;
		}

		public bool IsHostileNpc(IdentityId id)
		{
			return (long)id == 2;
		}
	}

	private sealed class DummyStory : IAsyncStateMachine
	{
		public void MoveNext()
		{
		}

		public void SetStateMachine(IAsyncStateMachine stateMachine)
		{
		}
	}

	private static Entity? _bbEntity;

	private static (StringHash, float)[]? _bbFloats;

	private static (StringHash, int)[]? _bbInts;

	private static (StringHash, StringHash)[]? _bbStrings;

	private static (StringHash, bool)[]? _bbBools;

	private static int _bbCalls;

	private const string BlackboardSignal = "Keen.VRage.Animation.GameObjects.AnimationBlackboardComponent+BlackboardSendSignal";

	private const string DoorSignal = "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Doors.DoorServerComponent+RequestDoorStateToggleSignal";

	private static nint[]? _guardReal;

	private static int _doorCalls;

	private static readonly string[] SignalKinds = new string[8] { "ReplicateNewObject", "ReleaseReplicatedObject", "ConfirmReplicatedObjectReleased", "ReplicateMemberChanged", "ReplicateNewCollection", "ReleaseReplicatedCollection", "ConfirmReplicatedCollectionReleased", "ReplicateCollectionChanged" };

	private static readonly FieldInfo ComponentEntity = EngineMembers.Field(HookTargets.Reflected.Single((EngineMember m) => m.Id == "component-entity"));

	public static (bool Ok, string Detail) WithFirstChance(Func<(bool, string)> f)
	{
		Exception first = null;
		EventHandler<FirstChanceExceptionEventArgs> value = (object? _, FirstChanceExceptionEventArgs e) =>
		{
			if (first == null)
			{
				first = e.Exception;
			}
		};
		AppDomain.CurrentDomain.FirstChanceException += value;
		try
		{
			return f();
		}
		catch (Exception ex)
		{
			return (Ok: false, Detail: $"{ex.GetType().Name}: {ex.Message}; first exception: {first}");
		}
		finally
		{
			AppDomain.CurrentDomain.FirstChanceException -= value;
		}
	}

	public static (bool Ok, string Detail) BundleRoundTrip()
	{
		return WithFirstChance(BundleRoundTripCore);
	}

	public static (bool Ok, string Detail) SnapshotRoundTrip()
	{
		return WithFirstChance(SnapshotRoundTripCore);
	}

	private static (bool Ok, string Detail) BundleRoundTripCore()
	{
		Entity entity = EntityAllocator.AllocateStub();
		Entity entity2 = EntityAllocator.AllocateStub();
		EntityBundle entityBundle = new EntityBundle();
		entityBundle.Add(new EntityObjectBuilder
		{
			DebugName = "root"
		}, entity, isRoot: true);
		entityBundle.Add(new EntityObjectBuilder
		{
			DebugName = "referenced"
		}, entity2);
		NetIdRegistry<Entity> ids = new NetIdRegistry<Entity>();
		ids.Bind(entity, 42u);
		NetIdProxyContext netIdProxyContext = new NetIdProxyContext((Entity e) => (int)ids.GetOrAssign(e), null);
		byte[] array = EngineCodec.SerializeBundle(entityBundle, netIdProxyContext);
		ProxyMap proxies = new ProxyMap();
		NetIdProxyContext netIdProxyContext2 = new NetIdProxyContext(null, (int id4) => proxies.GetOrCreate((uint)id4));
		EntityBundle entityBundle2 = EngineCodec.DeserializeBundle(array, netIdProxyContext2);
		bool num = entityBundle2.Roots == 1 && entityBundle2.Entities.Count == 2 && entityBundle2.Builders.Count == 2 && entityBundle2.Entities[0] != null && proxies.TryGetId(entityBundle2.Entities[0], out var id) && id == 42 && entityBundle2.Entities[1] != null && proxies.TryGetId(entityBundle2.Entities[1], out var id2) && ids.TryGet(entity2, out var id3) && id2 == id3 && entityBundle2.Builders[0].DebugName == "root" && EntityAllocator.IsStub(entityBundle2.Entities[0]);
		List<EntityLocator> list = EngineCodec.Locate(entityBundle, new Dictionary<Entity, EntityObjectBuilder>
		{
			[entity] = entityBundle.Builders[0],
			[entity2] = entityBundle.Builders[1]
		}, (Entity e) => ids.GetOrAssign(e), out var unlocated);
		return (Ok: num & (unlocated == 0 && list.Count == 2 && EngineCodec.Resolve(entityBundle2, list[1]) == entityBundle2.Builders[1]), Detail: $"{array.Length} B, {netIdProxyContext.Written} refs written, {netIdProxyContext2.Read} read; roots {entityBundle2.Roots}, entities {entityBundle2.Entities.Count}, proxy ids {string.Join(",", entityBundle2.Entities.Select((Entity e) => (e == null || !proxies.TryGetId(e, out var id4)) ? "?" : id4.ToString()))}, locators {list.Count} (unlocated {unlocated})");
	}

	private static (bool Ok, string Detail) SnapshotRoundTripCore()
	{
		SessionComponentsSnapshot snapshot = new SessionComponentsSnapshot
		{
			Components = Array.Empty<SessionComponentsSnapshot.ComponentInfo>()
		};
		NetIdProxyContext proxies = new NetIdProxyContext((Entity _) => 7, null);
		byte[] array = EngineCodec.SerializeSnapshot(snapshot, proxies);
		SessionComponentsSnapshot sessionComponentsSnapshot = EngineCodec.DeserializeSnapshot(array, new NetIdProxyContext(null, (int _) => EntityAllocator.AllocateStub()));
		return (Ok: sessionComponentsSnapshot != null && sessionComponentsSnapshot.Components != null && sessionComponentsSnapshot.Components.Length == 0, Detail: $"{array.Length} B, components {sessionComponentsSnapshot?.Components?.Length}");
	}

	public static (int Offset, int InvokeIndex, int DataIndex, ReplicationCommandBuffer Cb) WriteClientCommands(object session, object source, int typeId, byte[] payload, Action action)
	{
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		ReplicationCommandBuffer replicationCommandBuffer = new ReplicationCommandBuffer();
		VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader replicationCommandBufferWriterReader = new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(replicationCommandBuffer);
		int assemblyOffset = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
		Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext> callback = delegate
		{
			action();
		};
		replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(assemblyOffset, (Entity)session, callback);
		replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(assemblyOffset + 1, (Entity)session, (Entity)source, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)payload));
		int item = -1;
		int item2 = -1;
		try
		{
			ReplicationCommandBufferIndexer.CommandBufferCommand[] table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
			item = Array.FindIndex(table, (ReplicationCommandBufferIndexer.CommandBufferCommand c) => c.SignalType == typeof(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.PostInvokeInternalSignal));
			item2 = Array.FindIndex(table, (ReplicationCommandBufferIndexer.CommandBufferCommand c) => c.SignalType == typeof(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.PostDataSignal));
		}
		catch (Exception)
		{
		}
		return (Offset: assemblyOffset, InvokeIndex: item, DataIndex: item2, Cb: replicationCommandBuffer);
	}

	public static object NewStub()
	{
		return EntityAllocator.AllocateStub();
	}

	public static (bool Ok, string Detail) BlobIndexing()
	{
		return WithFirstChance(BlobIndexingCore);
	}

	public static (bool Ok, string Detail) OffloadingRoundTrip()
	{
		return WithFirstChance(OffloadingRoundTripCore);
	}

	public static (bool Ok, string Detail) CloseQueueHygiene()
	{
		return WithFirstChance(CloseQueueHygieneCore);
	}

	private static ProceduralVoxelStorageComponentObjectBuilder OffloadedVoxelOb(Blob id, params Blob[] regions)
	{
		ProceduralVoxelStorageComponentObjectBuilder proceduralVoxelStorageComponentObjectBuilder = new ProceduralVoxelStorageComponentObjectBuilder();
		OffloadedVoxelStorage offloadedVoxelStorage = new OffloadedVoxelStorage
		{
			Identifier = id,
			StorageBlobs = new HashSet<Blob>(regions)
		};
		typeof(VoxelStorageComponentBaseObjectBuilder).GetField("Offloaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(proceduralVoxelStorageComponentObjectBuilder, offloadedVoxelStorage);
		return proceduralVoxelStorageComponentObjectBuilder;
	}

	private static (bool Ok, string Detail) BlobIndexingCore()
	{
		Blob blob = new Blob();
		Blob blob2 = new Blob();
		Blob blob3 = new Blob();
		HashSet<Blob> hashSet = new HashSet<Blob> { blob, blob2, blob3 };
		ProceduralVoxelStorageComponentObjectBuilder objectBuilder = OffloadedVoxelOb(blob, blob2, blob3);
		SessionComponentsSnapshot sessionComponentsSnapshot = new SessionComponentsSnapshot();
		sessionComponentsSnapshot.Components = new SessionComponentsSnapshot.ComponentInfo[1]
		{
			new SessionComponentsSnapshot.ComponentInfo
			{
				Type = typeof(ProceduralVoxelStorageComponent),
				ObjectBuilder = objectBuilder
			}
		};
		HashSet<Blob> hashSet2 = new HashSet<Blob>();
		byte[] array = EngineCodec.SerializeSnapshot(sessionComponentsSnapshot, new NetIdProxyContext((Entity _) => 0, null), hashSet2);
		HashSet<Blob> hashSet3 = new HashSet<Blob>();
		ProceduralVoxelStorageComponentObjectBuilder proceduralVoxelStorageComponentObjectBuilder = EngineCodec.DeserializeSnapshot(array, new NetIdProxyContext(null, (int _) => EntityAllocator.AllocateStub()), hashSet3)?.Components?.FirstOrDefault().ObjectBuilder as ProceduralVoxelStorageComponentObjectBuilder;
		bool flag = hashSet2.SetEquals(hashSet) && hashSet3.SetEquals(hashSet) && proceduralVoxelStorageComponentObjectBuilder != null;
		HashSet<Blob> hashSet4 = EngineCodec.SnapshotBlobs(sessionComponentsSnapshot, (Type _) => true, out int serverOnly, out List<string> _);
		HashSet<Blob> hashSet5 = EngineCodec.SnapshotBlobs(sessionComponentsSnapshot, (Type _) => false, out int serverOnly2, out List<string> owners2);
		flag &= hashSet4.SetEquals(hashSet) && serverOnly == 0 && hashSet5.Count == 0 && serverOnly2 == 3;
		Blob blob4 = new Blob();
		Blob blob5 = new Blob();
		EntityObjectBuilder entityObjectBuilder = new EntityObjectBuilder();
		entityObjectBuilder.DebugName = "asteroid";
		entityObjectBuilder.ObjectBuilders = CollectionsUnsafe.CreateKeyedListFromArrays(new Guid[1] { Guid.NewGuid() }, new object[1] { OffloadedVoxelOb(blob4, blob5) });
		EntityObjectBuilder eob = entityObjectBuilder;
		EntityBundle entityBundle = new EntityBundle();
		entityBundle.Add(eob, EntityAllocator.AllocateStub(), isRoot: true);
		HashSet<Blob> hashSet6 = new HashSet<Blob>();
		string value;
		try
		{
			byte[] array2 = EngineCodec.SerializeBundle(entityBundle, new NetIdProxyContext((Entity _) => 5, null), hashSet6);
			HashSet<Blob> hashSet7 = new HashSet<Blob>();
			EngineCodec.DeserializeBundle(array2, new NetIdProxyContext(null, (int _) => EntityAllocator.AllocateStub()), hashSet7);
			bool flag2 = hashSet6.SetEquals(new Blob[2] { blob4, blob5 }) && hashSet7.SetEquals(hashSet6);
			flag &= flag2;
			value = $"bundle {array2.Length} B: {hashSet6.Count} written / {hashSet7.Count} read{(flag2 ? "" : " MISMATCH")}";
		}
		catch (Exception ex)
		{
			flag = false;
			value = "bundle threw " + ex.GetType().Name + ": " + ex.Message;
		}
		List<Guid> list = EngineCodec.Ids(hashSet);
		flag &= list.Count == 3 && list.Contains(Guid.Parse(blob.ToString())) && EngineCodec.Id(Blob.FromGuid(list[0])) == list[0];
		return (Ok: flag, Detail: $"snapshot {array.Length} B: {hashSet2.Count} blob ids written, {hashSet3.Count} read; filter: client {hashSet4.Count}, server-only {serverOnly2} ({string.Join(", ", owners2)}); {value}");
	}

	private static (bool Ok, string Detail) OffloadingRoundTripCore()
	{
		InMemoryDataOffloading inMemoryDataOffloading = new InMemoryDataOffloading();
		byte[] array = (from i in Enumerable.Range(0, 70000)
			select (byte)i).ToArray();
		byte[] saved = (from i in Enumerable.Range(0, 3000)
			select (byte)(i * 3)).ToArray();
		Blob blob = inMemoryDataOffloading.OffloadData(array);
		Blob blob2 = new Blob();
		inMemoryDataOffloading.AddKnownBlob(blob2, () => new Task<Stream>(new MemoryStream(saved, writable: false)));
		Blob blob3 = new Blob();
		Dictionary<Guid, byte[]> dictionary = EngineCodec.LoadBlobs(inMemoryDataOffloading, new Blob[3] { blob, blob2, blob3 }, TimeSpan.FromSeconds(10L), out int failed, out string firstError);
		byte[] array2 = dictionary[EngineCodec.Id(blob)];
		int num;
		if (array2 != null && array2.SequenceEqual(array))
		{
			byte[] array3 = dictionary[EngineCodec.Id(blob2)];
			if (array3 != null && array3.SequenceEqual(saved) && dictionary[EngineCodec.Id(blob3)] == null)
			{
				num = ((failed == 1) ? 1 : 0);
				goto IL_012f;
			}
		}
		num = 0;
		goto IL_012f;
		IL_012f:
		bool flag = (byte)num != 0;
		InMemoryDataOffloading inMemoryDataOffloading2 = new InMemoryDataOffloading();
		foreach (var (id, array5) in dictionary)
		{
			if (array5 != null)
			{
				EngineCodec.Register(inMemoryDataOffloading2, id, array5);
			}
		}
		bool flag2 = ((IReadOnlyDataOffloading)inMemoryDataOffloading2).LoadData(blob).WaitBlocking(TimeSpan.FromSeconds(10L), out var result);
		bool flag3 = ((IReadOnlyDataOffloading)inMemoryDataOffloading2).LoadData(Blob.FromGuid(EngineCodec.Id(blob2))).WaitBlocking(TimeSpan.FromSeconds(10L), out var result2);
		bool flag4 = ((IReadOnlyDataOffloading)inMemoryDataOffloading2).LoadData(blob).WaitBlocking(TimeSpan.FromSeconds(10L), out var result3);
		flag &= (flag2 & flag3 & flag4) && result != null && result.SequenceEqual(array) && result2 != null && result2.SequenceEqual(saved) && (result3?.SequenceEqual(array) ?? false);
		inMemoryDataOffloading.Dispose();
		inMemoryDataOffloading2.Dispose();
		return (Ok: flag, Detail: $"host LoadBlobs: {dictionary.Count} ids, {failed} unknown ({firstError?.Split(':').Skip(1).FirstOrDefault()?.Trim()}); client LoadData after Register: {result?.Length} B / {result2?.Length} B / cached {result3?.Length} B");
	}

	private static (bool Ok, string Detail) CloseQueueHygieneCore()
	{
		Session session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
		HashSet<Entity> hashSet = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		HashSet<Entity> hashSet2 = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		typeof(Session).GetField("PendingEntitiesForClose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(session, hashSet);
		typeof(Session).GetField("EntitiesForClose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(session, hashSet2);
		Entity entity = EntityAllocator.AllocateStub();
		Entity entity2 = EntityAllocator.AllocateStub();
		Entity entity3 = EntityAllocator.AllocateStub();
		hashSet.Add(entity);
		hashSet.Add(entity2);
		hashSet2.Add(entity3);
		bool flag = EngineCodec.IsClosing(session, entity2) && EngineCodec.IsClosing(session, entity3) && !EngineCodec.IsClosing(session, EntityAllocator.AllocateStub());
		int num = EngineCodec.PurgeDeadFromCloseQueues(session, out string sample);
		bool flag2 = (EngineCodec.CloseQueuesReachable & flag) && num == 3 && hashSet.Count == 0 && hashSet2.Count == 0 && EngineCodec.IsDead(entity) && EngineCodec.IsDead(null);
		int num2 = EngineCodec.PurgeDeadFromCloseQueues(session, out string _);
		flag2 &= num2 == 0;
		Entity entity4 = EntityAllocator.AllocateStub();
		Entity entity5 = EntityAllocator.AllocateStub();
		Entity item = EntityAllocator.AllocateStub();
		hashSet.Add(entity4);
		hashSet2.Add(entity5);
		hashSet.Add(item);
		int num3 = EngineCodec.RemoveFromCloseQueues(session, new Entity[2] { entity4, entity5 });
		flag2 &= num3 == 2 && hashSet.Count == 1 && hashSet.Contains(item) && hashSet2.Count == 0;
		return (Ok: flag2, Detail: $"dropped {num} dead entries ({sample}), second pass {num2}; failed bundle's entities removed by identity: {num3}, others kept");
	}

	public static (bool Ok, string Detail) PairingRollback(object replication)
	{
		return WithFirstChance(() =>
		{
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Expected Obj, but got Unknown
			InProcessReplication val = (InProcessReplication)replication;
			ReplayContext replay = val.Client.ReplayContext;
			(Entity, Entity) tuple = (EntityAllocator.AllocateStub(), EntityAllocator.AllocateStub());
			replay.AddObjectPairing(tuple.Item1, tuple.Item2);
			List<Entity> list = (from _ in Enumerable.Range(0, 3)
				select EntityAllocator.AllocateStub()).ToList();
			List<Entity> list2 = list.Select((Entity _) => EntityAllocator.AllocateStub()).ToList();
			for (int num = 0; num < 3; num++)
			{
				replay.AddObjectPairing(list[num], list2[num]);
			}
			int num2 = ClientWorld.RollBackPairings(replay, list, out string firstError);
			bool num3 = num2 == 3 && firstError == null && list.All((Entity p) => !replay.HasPairingRemote(p)) && list2.All((Entity c) => !replay.HasPairingLocal(c)) && replay.HasPairingRemote(tuple.Item1) && replay.HasPairingLocal(tuple.Item2);
			int num4 = ClientWorld.RollBackPairings(replay, list, out string _);
			replay.RemoveObjectPairing(tuple.Item1);
			return (num3 && num4 == 0, $"removed {num2}, second rollback {num4}, unrelated pairing kept");
		});
	}

	public static (bool Ok, string Detail) CommandLayoutsCheck()
	{
		return WithFirstChance(() =>
		{
			ReplicationCommandBufferIndexer.CommandBufferCommand[] table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
			CommandLayout[] all = CommandLayouts.All;
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			int num6 = 0;
			List<string> list = new List<string>();
			for (int i = 0; i < all.Length; i++)
			{
				CommandLayout commandLayout = all[i];
				if (commandLayout != null && commandLayout.Kind == CommandKind.Signal)
				{
					num++;
					if (!commandLayout.Forwardable)
					{
						if (commandLayout.PayloadBytes < 0)
						{
							continue;
						}
						num6++;
					}
					else
					{
						num2++;
					}
					int num7 = ((table[i].WriterInfo != null) ? CommandLayouts.WriterAllocationSize(table[i].WriterInfo) : (-1));
					if (num7 < 0)
					{
						num5++;
					}
					else
					{
						num3++;
						if (num7 != 8 + commandLayout.PayloadBytes)
						{
							num4++;
							if (list.Count < 5)
							{
								list.Add($"{commandLayout.Name}: writer {num7} B, layout {8 + commandLayout.PayloadBytes} B");
							}
						}
					}
				}
			}
			string value = string.Join("; ", new string[3] { "Keen.Game2.Simulation.WorldObjects.Characters.CharacterRequestSignals+RequestJumpSignal", "Keen.Game2.Simulation.WorldObjects.Characters.CharacterRequestSignals+RequestSprintSignal", "Keen.Game2.Simulation.WorldObjects.Characters.CharacterMovementComponent+OnCharacterPhysicsStateChangedSignal" }.Select(Show));
			CommandLayout commandLayout2 = CommandLayouts.ByName("Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal");
			return (all.Length == table.Length && num > 100 && num4 == 0 && num3 > 50 && commandLayout2 != null && commandLayout2.Kind == CommandKind.PostData, $"{all.Length} commands, {num} signals, {num2} forwardable + {num6} sized but not forwarded ({num3} checked against the writers' allocationSize, {num5} computed sizes), {num4} mismatches{((list.Count > 0) ? (": " + string.Join(" | ", list)) : "")}; {value}");
		});
		static string Show(string n)
		{
			CommandLayout commandLayout = CommandLayouts.ByName(n);
			if (commandLayout != null)
			{
				return n.Split('+').Last() + " " + (commandLayout.Forwardable ? $"{commandLayout.PayloadBytes} B refs [{commandLayout.Signature}]" : ("not forwardable: " + commandLayout.Why));
			}
			return n.Split('+').Last() + " absent";
		}
	}

	public static (bool Ok, string Detail) TransformLayout()
	{
		return WithFirstChance(() =>
		{
			WorldTransform reference = new WorldTransform(new Vector3D(1.5, -2.25, 3.125), new Keen.VRage.Library.Mathematics.Quaternion(0.1f, 0.2f, 0.3f, 0.9f));
			byte[] array = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in reference, 1)).ToArray();
			int size = RuntimeDataInfo.Of(typeof(WorldTransform)).Size;
			bool num = array.Length == 40 && Unsafe.SizeOf<WorldTransform>() == 40 && BitConverter.ToDouble(array, 0) == 1.5 && BitConverter.ToDouble(array, 8) == -2.25 && BitConverter.ToDouble(array, 16) == 3.125 && BitConverter.ToSingle(array, 24) == 0.1f && BitConverter.ToSingle(array, 36) == 0.9f;
			byte[] array2 = TransformSmoother.Lerp(array, array, 0.5);
			return (num & array2.AsSpan(0, 24).SequenceEqual(array.AsSpan(0, 24)), $"managed {array.Length} B (RuntimeDataInfo on this runtime: {size} B), position at 0/8/16, orientation X..W at 24..36");
		});
	}

	public static (bool Ok, string Detail) UpstreamRoundTrip()
	{
		return WithFirstChance(() =>
		{
			//IL_00da: Unknown result type (might be due to invalid IL or missing references)
			//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
			HarnessLog log = new HarnessLog();
			Entity entity = EntityAllocator.AllocateStub();
			Entity mine = EntityAllocator.AllocateStub();
			Entity other = EntityAllocator.AllocateStub();
			Entity stranger = EntityAllocator.AllocateStub();
			CommandLayout sig = (from l in CommandLayouts.All
				where l != null && l.Forwardable && l.Kind == CommandKind.Signal && !l.Dynamic && l.RefOffsets.Length != 0 && l.Segments.Where((Segment g) => g.Kind == SegmentKind.Ref).All((Segment g) => g.Type == typeof(Entity))
				orderby l.RefOffsets.Length descending
				select l).First();
			ReplicationCommandBuffer cb = new ReplicationCommandBuffer();
			VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader replicationCommandBufferWriterReader = new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(cb);
			int assemblyOffset = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
			int typeId = RuntimeDataInfo.Of(typeof(WorldTransform)).TypeId;
			replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(assemblyOffset + 1, entity, mine, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)new byte[8] { 1, 2, 3, 4, 5, 6, 7, 8 }));
			replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(assemblyOffset + 1, entity, stranger, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)new byte[8]));
			Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext> callback = delegate
			{
			};
			replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(assemblyOffset, entity, callback);
			byte[] payload = new byte[sig.PayloadBytes];
			for (int num = 0; num < payload.Length; num++)
			{
				payload[num] = (byte)(64 + num);
			}
			CommandBuffers.Append(cb, sig.Stub, payload, (int off) => cb.StoreObject((object)((off == sig.RefOffsets[0]) ? mine : other)), sig.RefOffsets);
			ClientUpstream clientUpstream = new ClientUpstream(log);
			ObjectSpace space = new ObjectSpace
			{
				Name = "client",
				IdOf = (Entity e) =>
				{
					uint? result = ((e != mine) ? ((e != other) ? ((uint?)null) : new uint?(9u)) : new uint?(7u));
					return result;
				},
				Resolve = (uint _, bool _) => (Entity?)null
			};
			int stories = 0;
			clientUpstream.Capture(cb, (Entity? e) => (e != mine) ? ((uint?)null) : new uint?(7u), space, (Delegate _) =>
			{
				stories++;
			});
			bool num2 = clientUpstream.CurrentInput.Count == 1 && clientUpstream.CurrentInput.Values.First().SequenceEqual(new byte[8] { 1, 2, 3, 4, 5, 6, 7, 8 }) && clientUpstream.PendingCommands.Count == 1 && clientUpstream.NotOurs == 1 && clientUpstream.Closures == 1 && stories == 0 && clientUpstream.Unparsed == 0;
			WireCommand cmd = clientUpstream.PendingCommands.FirstOrDefault();
			bool flag = cmd != null && cmd.Refs.Count == sig.RefOffsets.Length && cmd.Refs.Select((WireValue wireValue, int i) => wireValue.Kind == WireKind.Entity && wireValue.Id == (uint)((i == 0) ? 7 : 9)).All((bool x) => x) && sig.RefOffsets.All((int off) => BitConverter.ToInt32(cmd.Payload, off) == 0);
			bool flag2 = cmd != null && cmd.Payload.Length == payload.Length && (from i in Enumerable.Range(0, payload.Length)
				where !sig.RefOffsets.Any((int o) => i >= o && i < o + 4)
				select i).All((int i) => cmd.Payload[i] == payload[i]);
			bool num3 = num2 & (flag & flag2);
			WireWriter wireWriter = new WireWriter();
			WireCommand.Write(wireWriter, cmd);
			WireReader r = new WireReader(wireWriter.ToArray());
			WireCommand c = WireCommand.Read(ref r);
			Entity hostMine = EntityAllocator.AllocateStub();
			Entity hostOther = EntityAllocator.AllocateStub();
			ObjectSpace sp = new ObjectSpace
			{
				Name = "host",
				IdOf = (Entity _) => (uint?)null,
				Resolve = (uint id, bool _) => id switch
				{
					9u => hostOther, 
					7u => hostMine, 
					_ => null, 
				}
			};
			ReplicationCommandBuffer cb2 = new ReplicationCommandBuffer();
			string fail = null;
			bool flag3 = SignalCodec.Write(cb2, sig, c, sp, new List<(int, Segment)>(), ref fail);
			byte[] raw = (flag3 ? CommandBuffers.Bytes(cb2, 0) : Array.Empty<byte>());
			bool flag4 = flag3 && raw.Length == 8 + sig.PayloadBytes && BitConverter.ToInt32(raw, 4) == sig.Stub && cb2.ReadObject(BitConverter.ToInt32(raw, 8 + sig.RefOffsets[0])) == hostMine && sig.RefOffsets.Skip(1).All((int off) => cb2.ReadObject(BitConverter.ToInt32(raw, 8 + off)) == hostOther) && (from i in Enumerable.Range(0, payload.Length)
				where !sig.RefOffsets.Any((int o) => i >= o && i < o + 4)
				select i).All((int i) => raw[8 + i] == payload[i]);
			bool num4 = num3 & flag4;
			ReplicationCommandBuffer cb3 = new ReplicationCommandBuffer();
			CommandBuffers.Append(cb3, sig.Stub, payload, (int off) => cb3.StoreObject((object)stranger), sig.RefOffsets);
			ClientUpstream clientUpstream2 = new ClientUpstream(log);
			clientUpstream2.Capture(cb3, (Entity? _) => (uint?)null, space, null);
			bool flag5 = clientUpstream2.PendingCommands.Count == 0 && clientUpstream2.BadRefs == 1 && clientUpstream2.Unparsed == 0;
			return (num4 & flag5, $"signal {sig.Name.Split('.').Last()} ({sig.PayloadBytes} B, refs [{sig.Signature}]); client kept 1 input value + 1 command, dropped 1 foreign PostData + 1 plain closure; refs as typed entity ids {(flag ? "ok" : "WRONG")}, data bytes {(flag2 ? "kept" : "CHANGED")}; wire + host rebuild {(flag4 ? "ok" : ("WRONG " + fail))}; unshared entity refused {(flag5 ? "ok" : "WRONG")}; {clientUpstream.Describe()}");
		});
	}

	public static void AddExternalService(object sessionBuilder, object service)
	{
		((SessionBuilder)sessionBuilder).ExternalServices.Add((IKeyedService)service);
	}

	public static (bool Ok, string Detail) LookMathMatchesKeen()
	{
		return WithFirstChance(() =>
		{
			ControlData reference = new ControlData
			{
				Movement = new Keen.VRage.Library.Mathematics.Vector3(0.25f, -0.5f, 1f),
				Rotation = new Keen.VRage.Library.Mathematics.Vector3(0.01f, -0.02f, 0.03f)
			};
			byte[] array = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in reference, 1)).ToArray();
			bool flag = array.Length == 24 && Unsafe.SizeOf<ControlData>() == 24 && BitConverter.ToSingle(array, 12) == 0.01f && BitConverter.ToSingle(array, 20) == 0.03f;
			ControlData controlData = MemoryMarshal.Read<ControlData>(LookAccumulator.WithRotation(array, 0.5f, 0.25f, -0.125f));
			flag &= controlData.Rotation.X == 0.5f && controlData.Rotation.Y == 0.25f && controlData.Rotation.Z == -0.125f && controlData.Movement.Y == -0.5f;
			bool flag2 = true;
			Random random = new Random(71);
			Keen.VRage.Library.Mathematics.Quaternion quaternion = Keen.VRage.Library.Mathematics.Quaternion.Normalize(new Keen.VRage.Library.Mathematics.Quaternion(0.1f, 0.7f, -0.2f, 0.65f));
			Keen.VRage.Library.Mathematics.Quaternion q = quaternion;
			Keen.VRage.Library.Mathematics.Quaternion q2 = quaternion;
			System.Numerics.Quaternion b = N(quaternion);
			System.Numerics.Quaternion b2 = N(quaternion);
			float num = 65f;
			for (int i = 0; i < 50; i++)
			{
				Keen.VRage.Library.Mathematics.Vector3 vector = new Keen.VRage.Library.Mathematics.Vector3((float)(random.NextDouble() - 0.5) * 0.05f, (float)(random.NextDouble() - 0.5) * 0.05f, (float)(random.NextDouble() - 0.5) * 0.05f);
				q *= Keen.VRage.Library.Mathematics.Quaternion.CreateFromAxisAngle(Keen.VRage.Library.Mathematics.Vector3.Up, 0f - vector.Y);
				float num2 = 1f / 60f * num;
				Keen.VRage.Library.Mathematics.Quaternion identity = Keen.VRage.Library.Mathematics.Quaternion.Identity;
				if (vector.X != 0f)
				{
					identity *= Keen.VRage.Library.Mathematics.Quaternion.CreateFromAxisAngle(Keen.VRage.Library.Mathematics.Vector3.Left, vector.X * num2);
				}
				if (vector.Y != 0f)
				{
					identity *= Keen.VRage.Library.Mathematics.Quaternion.CreateFromAxisAngle(Keen.VRage.Library.Mathematics.Vector3.Down, vector.Y * num2);
				}
				if (vector.Z != 0f)
				{
					identity *= Keen.VRage.Library.Mathematics.Quaternion.CreateFromAxisAngle(Keen.VRage.Library.Mathematics.Vector3.Forward, vector.Z * num2);
				}
				q2 *= identity;
				System.Numerics.Vector3 rotation = new System.Numerics.Vector3(vector.X, vector.Y, vector.Z);
				b *= LookMath.Step(LookMode.Ground, rotation, num);
				b2 *= LookMath.Step(LookMode.Jetpack, rotation, num);
			}
			float num3 = LookMath.AngleDeg(N(q), b);
			float num4 = LookMath.AngleDeg(N(q2), b2);
			bool flag3 = num3 < 0.01f && num4 < 0.01f && LookMath.AngleDeg(N(quaternion), b) > 1f;
			Keen.VRage.Library.Mathematics.Vector3 offset = new Keen.VRage.Library.Mathematics.Vector3(0f, 0.95f, 0.05f);
			WorldTransform worldTransform = new WorldTransform(new Vector3D(100.0, -20.5, 7.25), quaternion);
			Keen.VRage.Library.Mathematics.Quaternion rotation2 = Keen.VRage.Library.Mathematics.Quaternion.Normalize(Keen.VRage.Library.Mathematics.Quaternion.CreateFromAxisAngle(Keen.VRage.Library.Mathematics.Vector3.Left, 0.6f) * Keen.VRage.Library.Mathematics.Quaternion.CreateFromAxisAngle(Keen.VRage.Library.Mathematics.Vector3.Down, -0.4f));
			WorldTransform worldTransform2 = worldTransform * CharacterMovementComponent.OffsetRotation(in rotation2, in offset);
			System.Numerics.Vector3 vector2 = LookMath.PivotShift(N(worldTransform.Orientation), N(worldTransform2.Orientation), new System.Numerics.Vector3(offset.X, offset.Y, offset.Z));
			double num5 = worldTransform.Position.X + (double)vector2.X - worldTransform2.Position.X;
			double num6 = worldTransform.Position.Y + (double)vector2.Y - worldTransform2.Position.Y;
			double num7 = worldTransform.Position.Z + (double)vector2.Z - worldTransform2.Position.Z;
			double num8 = Math.Sqrt(num5 * num5 + num6 * num6 + num7 * num7);
			double num9 = worldTransform2.Position.X - worldTransform.Position.X;
			double num10 = worldTransform2.Position.Y - worldTransform.Position.Y;
			double num11 = worldTransform2.Position.Z - worldTransform.Position.Z;
			bool flag4 = num8 < 0.0001 && Math.Sqrt(num9 * num9 + num10 * num10 + num11 * num11) > 0.1;
			LookPredictor lookPredictor = new LookPredictor(new HarnessLog());
			lookPredictor.Begin(7u, 0L);
			lookPredictor.SetMode(LookMode.Jetpack, 0L, "harness");
			lookPredictor.OnHost(1000L, worldTransform.Position.X, worldTransform.Position.Y, worldTransform.Position.Z, N(worldTransform.Orientation));
			lookPredictor.Step(1001L, new System.Numerics.Vector3(0.02f, -0.01f, 0f), num, new System.Numerics.Vector3(offset.X, offset.Y, offset.Z));
			(double, double, double, System.Numerics.Quaternion) tuple = LookMath.ReadTransform(lookPredictor.Display());
			WorldTransform worldTransform3 = worldTransform * CharacterMovementComponent.OffsetRotation(in ILSpyHelper_AsRefReadOnly(K(System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Conjugate(N(worldTransform.Orientation)) * tuple.Item4))), in offset);
			double num12 = Math.Abs(tuple.Item1 - worldTransform3.Position.X) + Math.Abs(tuple.Item2 - worldTransform3.Position.Y) + Math.Abs(tuple.Item3 - worldTransform3.Position.Z);
			bool flag5 = num12 < 0.0001 && LookMath.AngleDeg(tuple.Item4, N(worldTransform3.Orientation)) < 0.01f;
			return (flag & flag2 & flag3 & flag4 & flag5, $"ControlData {array.Length} B, Rotation at {12} ({(flag ? "ok" : "MISMATCH")}), movement states {(flag2 ? "ok" : "MISMATCH")}; 50 chained steps vs Keen: ground {num3:0.0000} deg, jetpack {num4:0.0000} deg; jetpack pivot vs Keen's OffsetRotation: {num8:0.000000} m; predictor display vs Keen: {num12:0.000000} m");
			static ref readonly T ILSpyHelper_AsRefReadOnly<T>(in T temp)
			{
				//ILSpy generated this function to help ensure overload resolution can pick the overload using 'in'
				return ref temp;
			}
		});
	}

	private static System.Numerics.Quaternion N(Keen.VRage.Library.Mathematics.Quaternion q)
	{
		return new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W);
	}

	private static Keen.VRage.Library.Mathematics.Quaternion K(System.Numerics.Quaternion q)
	{
		return new Keen.VRage.Library.Mathematics.Quaternion(q.X, q.Y, q.Z, q.W);
	}

	public static (bool Ok, string Detail) SignalLayoutReport()
	{
		return WithFirstChance(() =>
		{
			List<CommandLayout> all = (from l in CommandLayouts.All
				where l != null && l.Kind == CommandKind.Signal
				select (l)).ToList();
			int value = all.Count((CommandLayout l) => l.Forwardable);
			int value2 = all.Count((CommandLayout l) => l.Forwardable && l.Dynamic);
			int value3 = all.Count((CommandLayout l) => l.Forwardable && l.Segments.Any((Segment g) => IsRef(g) && g.Param != 0 && typeof(Component).IsAssignableFrom(g.Type) && l.Translated.ContainsKey(g.Param)));
			int value4 = all.Count((CommandLayout l) => l.Forwardable && l.Segments.Length != 0 && IsRef(l.Segments[0]) && typeof(Component).IsAssignableFrom(l.Segments[0].Type));
			int value5 = all.Count((CommandLayout l) => l.Forwardable && l.Segments.Any((Segment g) => IsRef(g) && typeof(Session).IsAssignableFrom(g.Type)));
			IEnumerable<string> contents = from l in all
				orderby l.Name
				select $"{l.Name}\t{(l.Forwardable ? "forwardable" : ("NO: " + l.Why))}\t{(l.Dynamic ? "dynamic" : (l.PayloadBytes + " B"))}\t[{l.Signature}]\ttranslated {{{string.Join(",", from kv in l.Translated
					orderby kv.Key
					select $"{kv.Key}:{kv.Value}")}}}\t" + string.Join(",", from g in l.Segments.Where(IsRef)
					select $"{g.Param}:{g.Type.Name}");
			string text = Path.Combine(GameLog.LogDirectory(), "y51online_signal_layouts.txt");
			File.WriteAllLines(text, contents);
			CommandLayout commandLayout = Find("BlackboardSendSignal");
			CommandLayout commandLayout2 = Find("RequestDoorStateToggleSignal");
			CommandLayout commandLayout3 = Find("SyncSupportingMaterialSignal");
			return (CommandLayouts.CallbacksAnalysed > 100 && commandLayout != null && commandLayout.Forwardable && commandLayout.Dynamic && commandLayout2 != null && commandLayout2.Forwardable && commandLayout3 != null && commandLayout3.Forwardable && commandLayout3.Translated.Count > 0 && commandLayout.Translated.ContainsKey(0), $"reader callbacks analysed {CommandLayouts.CallbacksAnalysed} (signals without one {CommandLayouts.CallbacksMissing}); {all.Count} signals, {value} forwardable ({value2} with spans); forwardable with a translated component argument {value3} (a joiner drops those S2C), component target {value4}, Session argument {value5}; " + string.Join("; ", new string[5] { "BlackboardSendSignal", "SyncSupportingMaterialSignal", "UpdateStatSignal", "RequestDoorStateToggleSignal", "RequestJumpSignal" }.Select(Show)) + "; full list " + text);
			CommandLayout? Find(string suffix)
			{
				return all.FirstOrDefault((CommandLayout l) => l.Name.EndsWith("+" + suffix, StringComparison.Ordinal));
			}
			string Show(string suffix)
			{
				CommandLayout commandLayout4 = Find(suffix);
				if (commandLayout4 == null)
				{
					return suffix + " absent";
				}
				return suffix + " " + (commandLayout4.Forwardable ? ((commandLayout4.Dynamic ? "dynamic" : (commandLayout4.PayloadBytes + " B")) + $" [{commandLayout4.Signature}] translated {{{string.Join(",", from kv in commandLayout4.Translated
					orderby kv.Key
					select $"{kv.Key}:{kv.Value}")}}}") : ("NOT forwardable: " + commandLayout4.Why));
			}
		});
		static bool IsRef(Segment g)
		{
			if (g.Kind == SegmentKind.Ref)
			{
				return g.Type != null;
			}
			return false;
		}
	}

	private static void BlackboardCallback(object context, Entity entity, ReadOnlySpan<(StringHash, float)> floats, ReadOnlySpan<(StringHash, int)> ints, ReadOnlySpan<(StringHash, StringHash)> strings, ReadOnlySpan<(StringHash, bool)> bools)
	{
		_bbCalls++;
		_bbEntity = entity;
		_bbFloats = floats.ToArray();
		_bbInts = ints.ToArray();
		_bbStrings = strings.ToArray();
		_bbBools = bools.ToArray();
	}

	private static BlackboardWriter MakeBlackboardWriter(out Type writerType)
	{
		writerType = Type.GetType("VRage.Animation.Replication.CB.ReplicationCommandBufferWriterReader, VRage.Animation", throwOnError: true);
		MethodInfo method = writerType.GetMethod("Write_Keen_VRage_Animation_GameObjects_AnimationBlackboardComponent_BlackboardSendSignal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		DynamicMethod dynamicMethod = new DynamicMethod("y51online_bb_write", typeof(void), new Type[7]
		{
			typeof(object),
			typeof(int),
			typeof(Entity),
			typeof(ReadOnlySpan<(StringHash, float)>),
			typeof(ReadOnlySpan<(StringHash, int)>),
			typeof(ReadOnlySpan<(StringHash, StringHash)>),
			typeof(ReadOnlySpan<(StringHash, bool)>)
		}, typeof(HarnessHooks).Module, skipVisibility: true);
		ILGenerator iLGenerator = dynamicMethod.GetILGenerator();
		iLGenerator.Emit(OpCodes.Ldarg_0);
		iLGenerator.Emit(writerType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, writerType);
		for (short num = 1; num <= 6; num++)
		{
			iLGenerator.Emit(OpCodes.Ldarg, num);
		}
		iLGenerator.Emit(OpCodes.Call, method);
		iLGenerator.Emit(OpCodes.Ret);
		return (BlackboardWriter)dynamicMethod.CreateDelegate(typeof(BlackboardWriter));
	}

	private static ObjectSpace Space(string name, Dictionary<Entity, uint> ids)
	{
		Dictionary<uint, Entity> back = ids.ToDictionary((KeyValuePair<Entity, uint> kv) => kv.Value, (KeyValuePair<Entity, uint> kv) => kv.Key);
		return new ObjectSpace
		{
			Name = name,
			IdOf = (Entity e) => (!ids.TryGetValue(e, out var value)) ? ((uint?)null) : new uint?(value),
			Resolve = (uint id, bool _) => (!back.TryGetValue(id, out var value)) ? null : value
		};
	}

	public unsafe static (bool Ok, string Detail) BlackboardRoundTrip()
	{
		return WithFirstChance(() =>
		{
			//IL_0336: Unknown result type (might be due to invalid IL or missing references)
			CommandLayout commandLayout = CommandLayouts.ByName("Keen.VRage.Animation.GameObjects.AnimationBlackboardComponent+BlackboardSendSignal") ?? throw new InvalidOperationException("no BlackboardSendSignal layout");
			BlackboardWriter blackboardWriter = MakeBlackboardWriter(out Type writerType);
			Entity entity = EntityAllocator.AllocateStub();
			Entity entity2 = EntityAllocator.AllocateStub();
			ReplicationCommandBuffer cb1 = new ReplicationCommandBuffer();
			object writer = Activator.CreateInstance(writerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[1] { cb1 }, null);
			(StringHash, float)[] array = new (StringHash, float)[2]
			{
				(StringHash.GetOrCompute("y51online_speed"), 1.5f),
				(StringHash.GetOrCompute("y51online_turn"), -2.25f)
			};
			(StringHash, int)[] array2 = new (StringHash, int)[1] { (StringHash.GetOrCompute("y51online_state"), 7) };
			(StringHash, StringHash)[] array3 = Array.Empty<(StringHash, StringHash)>();
			(StringHash, bool)[] array4 = new (StringHash, bool)[1] { (StringHash.GetOrCompute("y51online_jetpack"), true) };
			blackboardWriter(writer, commandLayout.Stub, entity, array, array2, array3, array4);
			byte[] array5 = CommandBuffers.Bytes(cb1, 0);
			byte[] array6 = array5.AsSpan(8).ToArray();
			List<(int, Segment)> list = new List<(int, Segment)>();
			int num = commandLayout.Frame(array6, list);
			bool flag = BitConverter.ToInt32(array5, 4) == commandLayout.Stub && num == array6.Length && list.Count == 1;
			string fail = null;
			WireCommand wireCommand = SignalCodec.Encode(commandLayout, 3, array6, (int i) => cb1.ReadObject(i), Space("host", new Dictionary<Entity, uint> { [entity] = 5u }), list, out var size, ref fail);
			WireWriter wireWriter = new WireWriter();
			if (wireCommand != null)
			{
				WireCommand.Write(wireWriter, wireCommand);
			}
			WireReader r = new WireReader(wireWriter.ToArray());
			WireCommand wireCommand2 = ((wireCommand == null) ? null : WireCommand.Read(ref r));
			ReplicationCommandBuffer replicationCommandBuffer = new ReplicationCommandBuffer();
			bool flag2 = wireCommand2 != null && SignalCodec.Write(replicationCommandBuffer, commandLayout, wireCommand2, Space("joiner", new Dictionary<Entity, uint> { [entity2] = 5u }), list, ref fail);
			byte[] array7 = (flag2 ? CommandBuffers.Bytes(replicationCommandBuffer, 0) : Array.Empty<byte>());
			bool flag3 = flag2 && array7.Length == array5.Length && BitConverter.ToInt32(array7, 0) == 1580056798 && array7.AsSpan(12).SequenceEqual(array5.AsSpan(12)) && replicationCommandBuffer.ReadObject(BitConverter.ToInt32(array7, 8)) == entity2;
			ReplicationCommandBufferIndexer.CommandBufferCommand[] table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
			ReplicationCommandBuffer.Stub[] array8 = new ReplicationCommandBuffer.Stub[table.Length];
			for (int num2 = 0; num2 < table.Length; num2++)
			{
				array8[num2] = new ReplicationCommandBuffer.Stub
				{
					Wrapper = table[num2].Reader,
					Callback = 0
				};
			}
			delegate*<object, Entity, ReadOnlySpan<(StringHash, float)>, ReadOnlySpan<(StringHash, int)>, ReadOnlySpan<(StringHash, StringHash)>, ReadOnlySpan<(StringHash, bool)>, void> callback = &BlackboardCallback;
			array8[commandLayout.Stub] = new ReplicationCommandBuffer.Stub
			{
				Wrapper = table[commandLayout.Stub].Reader,
				Callback = (nint)callback
			};
			_bbCalls = 0;
			if (flag2)
			{
				replicationCommandBuffer.Replay(new object(), new Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer.ReplayTable(array8));
			}
			bool flag4 = _bbCalls == 1 && _bbEntity == entity2 && _bbFloats.SequenceEqual(array) && _bbInts.SequenceEqual(array2) && _bbStrings.Length == 0 && _bbBools.SequenceEqual(array4);
			return ((flag && wireCommand != null && size == array6.Length) & flag3 & flag4, $"Keen writer {array5.Length} B (payload {array6.Length} B, framed {num}, refs {list.Count}); encoded {((wireCommand == null) ? ("FAILED " + fail) : $"{wireCommand.Payload.Length} B + {wireCommand.Refs.Count} ref")}, wire {wireWriter.Length} B; rewritten {(flag3 ? "byte-identical apart from the marker + receiver's entity" : ("WRONG " + fail))}; Keen reader -> callback {(flag4 ? "got the receiver's entity and all 4 spans" : $"WRONG (calls {_bbCalls})")}");
		});
	}

	private unsafe static void GuardingReader(object context, ref CommandBufferState st, nint callback, ReplicationCommandBuffer cb)
	{
		int num = *((int*)(st.DataPtr + st.DataOffset) - 1);
		delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> obj = (delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void>)_guardReal[num];
		if (*((int*)(st.DataPtr + st.DataOffset) - 2) == 1580056798)
		{
			ReplayGuard.Invoke(obj, context, ref st, callback, cb, num);
		}
		else
		{
			obj(context, ref st, callback, cb);
		}
	}

	private static void ThrowingDoorCallback(object context, Entity entity, bool? open)
	{
		_doorCalls++;
		bool? flag = open;
		throw new InvalidOperationException("handler failure on purpose (open=" + flag + ")");
	}

	public unsafe static (bool Ok, string Detail) ReplayGuardCheck()
	{
		return WithFirstChance(() =>
		{
			//IL_0275: Unknown result type (might be due to invalid IL or missing references)
			CommandLayout commandLayout = CommandLayouts.ByName("Keen.Game2.Simulation.WorldObjects.CubeBlocks.Doors.DoorServerComponent+RequestDoorStateToggleSignal") ?? throw new InvalidOperationException("no door layout");
			CommandLayout commandLayout2 = CommandLayouts.ByName("Keen.VRage.Animation.GameObjects.AnimationBlackboardComponent+BlackboardSendSignal") ?? throw new InvalidOperationException("no blackboard layout");
			Entity d = EntityAllocator.AllocateStub();
			Entity c = EntityAllocator.AllocateStub();
			ReplicationCommandBuffer cb = new ReplicationCommandBuffer();
			byte[] array = new byte[commandLayout.PayloadBytes];
			array[4] = 1;
			array[5] = 1;
			CommandBuffers.Append(cb, commandLayout.Stub, array, (int _) => cb.StoreObject((object)d), commandLayout.RefOffsets, 1580056798);
			BlackboardWriter blackboardWriter = MakeBlackboardWriter(out Type writerType);
			ReplicationCommandBuffer replicationCommandBuffer = new ReplicationCommandBuffer();
			object writer = Activator.CreateInstance(writerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[1] { replicationCommandBuffer }, null);
			(StringHash, float)[] array2 = new (StringHash, float)[1] { (StringHash.GetOrCompute("y51online_after_throw"), 3f) };
			blackboardWriter(writer, commandLayout2.Stub, c, array2, default, default, default);
			byte[] array3 = CommandBuffers.Bytes(replicationCommandBuffer, 0);
			CommandBuffers.Append(cb, commandLayout2.Stub, array3.AsSpan(8), (int _) => cb.StoreObject((object)c), new int[1], 1580056798);
			ReplicationCommandBufferIndexer.CommandBufferCommand[] table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
			_guardReal = new nint[table.Length];
			ReplicationCommandBuffer.Stub[] array4 = new ReplicationCommandBuffer.Stub[table.Length];
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> obj = &GuardingReader;
			for (int num = 0; num < table.Length; num++)
			{
				_guardReal[num] = (nint)table[num].Reader.Reader;
				array4[num] = new ReplicationCommandBuffer.Stub
				{
					Wrapper = new ReaderSignature((delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)obj),
					Callback = 0
				};
			}
			delegate*<object, Entity, bool?, void> callback = &ThrowingDoorCallback;
			delegate*<object, Entity, ReadOnlySpan<(StringHash, float)>, ReadOnlySpan<(StringHash, int)>, ReadOnlySpan<(StringHash, StringHash)>, ReadOnlySpan<(StringHash, bool)>, void> callback2 = &BlackboardCallback;
			array4[commandLayout.Stub] = new ReplicationCommandBuffer.Stub
			{
				Wrapper = new ReaderSignature((delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)obj),
				Callback = (nint)callback
			};
			array4[commandLayout2.Stub] = new ReplicationCommandBuffer.Stub
			{
				Wrapper = new ReaderSignature((delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)obj),
				Callback = (nint)callback2
			};
			long caught = ReplayGuard.Caught;
			_doorCalls = (_bbCalls = 0);
			Exception ex = null;
			try
			{
				cb.Replay(new object(), new Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer.ReplayTable(array4));
			}
			catch (Exception ex2)
			{
				ex = ex2;
			}
			bool flag = ex == null && _doorCalls == 1 && ReplayGuard.Caught == caught + 1 && _bbCalls == 1 && _bbEntity == c && _bbFloats.SequenceEqual(array2) && ReplayGuard.CaughtBy.ContainsKey("DoorServerComponent+RequestDoorStateToggleSignal");
			return (flag, $"door handler threw {_doorCalls}x, caught {ReplayGuard.Caught - caught}, escaped {((ex == null) ? "nothing" : ex.GetType().Name)}; next command (blackboard) still replayed {_bbCalls}x with its data {(flag ? "intact" : "?")}; {ReplayGuard.Describe()}");
		});
	}

	public static (bool Ok, string Detail) ObjectCodecRoundTrip()
	{
		return WithFirstChance(() =>
		{
			Entity entity = EntityAllocator.AllocateStub();
			Entity entity2 = EntityAllocator.AllocateStub();
			Entity o = EntityAllocator.AllocateStub();
			Entity entity3 = EntityAllocator.AllocateStub();
			Entity entity4 = EntityAllocator.AllocateStub();
			ObjectSpace src = Space("a", new Dictionary<Entity, uint>
			{
				[entity] = 11u,
				[entity2] = 12u
			});
			ObjectSpace dst = Space("b", new Dictionary<Entity, uint>
			{
				[entity3] = 11u,
				[entity4] = 12u
			});
			List<string> results = new List<string>();
			bool allOk = true;
			Expect("entity->receiver's entity", Trip(entity, typeof(Entity), out var fail) == entity3);
			Expect("string", (string)Trip("héllo", typeof(string), out fail) == "héllo");
			StringId stringId = StringId.Get("y51online_object_codec");
			Expect("StringId by text", Trip(stringId, typeof(StringId), out fail) is StringId stringId2 && stringId2.String == "y51online_object_codec");
			Expect("Vector3D raw", Trip(new Vector3D(1.0, -2.0, 3.5), typeof(Vector3D), out fail) is Vector3D { X: 1.0, Y: -2.0 } vector3D && vector3D.Z == 3.5);
			Expect("int[]", Trip(new int[3] { 3, 1, 4 }, typeof(int[]), out fail) is int[] first && first.SequenceEqual(new int[3] { 3, 1, 4 }));
			Expect("boxed IShape struct (ShapeSphere r=2.5)", Trip(new ShapeSphere(2.5f), typeof(IShape), out var fail2) is ShapeSphere { Radius: 2.5f } && fail2 == null);
			Entity entity5 = EntityAllocator.AllocateStub();
			BlockPlacerEntityBundle blockPlacerEntityBundle = new BlockPlacerEntityBundle(new EntityBundle
			{
				Entities = { entity5 }
			}, new BoundingBoxI(new Vector3I(0, 0, 0), new Vector3I(2, 3, 4)));
			src.ClearLocalIds();
			string fail3 = null;
			ObjectCodec.Encode(blockPlacerEntityBundle, typeof(BlockPlacerEntityBundle), src, ref fail3);
			bool flag = fail3 == null && src.LocalIds.Count == 0 && src.BundleLocalIds.Count == 1;
			BlockPlacerEntityBundle blockPlacerEntityBundle2 = Trip(blockPlacerEntityBundle, typeof(BlockPlacerEntityBundle), out fail3) as BlockPlacerEntityBundle;
			Expect("BlockPlacerEntityBundle (copy result) with a detached entity", flag && blockPlacerEntityBundle2 != null && blockPlacerEntityBundle2.AABB.Max.Z == 4 && blockPlacerEntityBundle2.Bundle != null && blockPlacerEntityBundle2.Bundle.Entities.Count == 1 && blockPlacerEntityBundle2.Bundle.Entities[0] != null && blockPlacerEntityBundle2.Bundle.Entities[0] != entity5);
			src.ClearLocalIds();
			fail3 = null;
			ObjectCodec.Encode(o, typeof(Entity), src, ref fail3);
			Expect("an unknown top-level entity still counts as unknown", src.LocalIds.Count == 1 && src.BundleLocalIds.Count == 0);
			Expect("List<Entity>", Trip(new List<Entity> { entity2, entity }, typeof(List<Entity>), out fail) is List<Entity> { Count: 2 } list && list[0] == entity4 && list[1] == entity3);
			Expect("ImmutableArray<int>", Trip(ImmutableArray.Create(5, 6), typeof(ImmutableArray<int>), out fail) is ImmutableArray<int> immutableArray && immutableArray.SequenceEqual(new int[2] { 5, 6 }));
			HarnessKeyed harnessKeyed = new HarnessKeyed
			{
				Key = stringId,
				Count = 9,
				Who = entity2
			};
			Expect("struct with StringId + entity", Trip(harnessKeyed, typeof(HarnessKeyed), out fail) is HarnessKeyed harnessKeyed2 && harnessKeyed2.Key.String == "y51online_object_codec" && harnessKeyed2.Count == 9 && harnessKeyed2.Who == entity4);
			Expect("Type", Trip(typeof(Entity), typeof(Type), out fail) as Type == typeof(Entity));
			EntityId entityId = new EntityId();
			Expect("secure EntityId travels as its Guid (phase 3)", Trip(entityId, typeof(EntityId), out var fail4) is EntityId entityId2 && entityId2 == entityId && fail4 == null);
			Expect("default EntityId travels", Trip(default(EntityId), typeof(EntityId), out var fail5) is EntityId entityId3 && entityId3 == default(EntityId) && fail5 == null);
			Trip((Action)(() =>
			{
			}), typeof(Action), out var fail6);
			Expect("delegate refused", fail6?.Contains("delegate") ?? false);
			src.ClearLocalIds();
			string fail7 = null;
			WireValue wireValue = ObjectCodec.Encode(o, typeof(Entity), src, ref fail7);
			Expect("unshared entity -> message-local id", fail7 == null && src.LocalIds.Count == 1 && (int)wireValue.Id < -1);
			string text = ObjectCodec.TypeProblem(typeof(HarnessKeyed));
			Expect("type report: struct travels", text == null);
			Expect("type report: EntityId travels (phase 3)", ObjectCodec.TypeProblem(typeof(EntityId)) == null);
			return (allOk, string.Join(", ", results));
			void Expect(string what, bool ok)
			{
				allOk &= ok;
				results.Add(what + " " + (ok ? "ok" : "WRONG"));
			}
			object? Trip(object? value, Type t, out string? reference, ObjectSpace? from = null)
			{
				reference = null;
				if (from == null)
				{
					from = src;
				}
				from.ClearLocalIds();
				WireValue v = ObjectCodec.Encode(value, t, from, ref reference);
				if (reference != null)
				{
					return null;
				}
				WireWriter wireWriter = new WireWriter();
				WireValue.Write(wireWriter, v);
				WireReader r = new WireReader(wireWriter.ToArray());
				WireValue v2 = WireValue.Read(ref r);
				dst.ClearLocalIds();
				dst.LocalStubs.Clear();
				return ObjectCodec.Decode(v2, t, dst, translated: false, ref reference);
			}
		});
	}

	private static (NetworkStoryContext Builder, FakeClientNarrator Narrator, TestStory<T> Story) StartClient<T>(string method, Dictionary<string, object?> args, Type? owner = null)
	{
		Type type = (owner ?? typeof(HarnessStories)).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Single((Type t) => t.Name.StartsWith("<" + method + ">", StringComparison.Ordinal));
		FieldInfo fieldInfo = ObjectCodec.Fields(type).Single((FieldInfo f) => StoryState.IsBuilder(f.FieldType));
		NetworkStoryContext networkStoryContext = (NetworkStoryContext)Activator.CreateInstance(fieldInfo.FieldType);
		FakeClientNarrator fakeClientNarrator = new FakeClientNarrator();
		TestStory<T> testStory = new TestStory<T>();
		networkStoryContext.Narrator = fakeClientNarrator;
		networkStoryContext.Story = testStory;
		object obj = Activator.CreateInstance(type);
		FieldInfo[] array = ObjectCodec.Fields(type);
		foreach (FieldInfo fieldInfo2 in array)
		{
			object value;
			if (fieldInfo2.Name == "<>1__state")
			{
				fieldInfo2.SetValue(obj, -1);
			}
			else if (fieldInfo2 == fieldInfo)
			{
				fieldInfo2.SetValue(obj, networkStoryContext);
			}
			else if (args.TryGetValue(fieldInfo2.Name, out value))
			{
				fieldInfo2.SetValue(obj, value);
			}
		}
		((IAsyncStateMachine)obj).MoveNext();
		return (Builder: networkStoryContext, Narrator: fakeClientNarrator, Story: testStory);
	}

	public static (bool Ok, string Detail) StoryRoundTrip()
	{
		return WithFirstChance(() =>
		{
			HarnessLog log = new HarnessLog();
			Entity clientE = EntityAllocator.AllocateStub();
			Entity hostE = EntityAllocator.AllocateStub();
			HarnessStories.Trace.Clear();
			HarnessStories.Tag = (Entity? e) =>
			{
				string result4 = ((e != clientE) ? ((e != hostE) ? ((e != null) ? "other" : "null") : "host-entity") : "client-entity");
				return result4;
			};
			ObjectSpace space = Space("client", new Dictionary<Entity, uint> { [clientE] = 40u });
			ObjectSpace objectSpace = Space("server", new Dictionary<Entity, uint> { [hostE] = 40u });
			ClientStories clientStories = new ClientStories(log);
			HostStories hostStories = new HostStories(log);
			FakeSink fakeSink = new FakeSink();
			PeerId peer = new PeerId(77uL);
			List<string> results = new List<string>();
			bool allOk = true;
			StringId stringId = StringId.Get("y51online_story_key");
			var (context, fakeClientNarrator, _) = StartClient<int>("RoundTrip", new Dictionary<string, object>
			{
				["target"] = clientE,
				["x"] = 20,
				["label"] = "abc",
				["key"] = stringId
			});
			Expect("client stopped at MoveTo.Server", fakeClientNarrator.Moved != null && HarnessStories.Trace.Count == 0);
			StoryMove storyMove = clientStories.Prepare(context, fakeClientNarrator.Moved, null, space);
			Expect("encoded", storyMove != null);
			StoryMove move = StoryMove.Decode(storyMove.Encode());
			hostStories.Run(peer, move, fakeSink, objectSpace, null, 1, 0);
			Expect("server half ran with the host's entity and the joiner's event context", HarnessStories.Trace.FirstOrDefault() == "server:host-entity:remote:1:y51online_story_key");
			Expect("MoveTo.Client came back as a move", fakeSink.ToClient.Count == 1 && fakeSink.ToClient.TryPeek(out object result) && result is StoryMove);
			clientStories.ProcessInbox(fakeSink.ToClient, space, (Action a) =>
			{
				a();
			});
			Expect("client half ran with the client's entity", HarnessStories.Trace.ElementAtOrDefault(1) == "client:client-entity:40");
			Expect("story completed on the client with 43", fakeClientNarrator.Finishes == 1 && fakeClientNarrator.Result is int num && num == 43 && fakeClientNarrator.Error == null);
			(NetworkStoryContext Builder, FakeClientNarrator Narrator, TestStory<int> Story) tuple2 = StartClient<int>("RoundTrip", new Dictionary<string, object>
			{
				["target"] = clientE,
				["x"] = 1,
				["label"] = "k",
				["key"] = stringId
			});
			NetworkStoryContext item = tuple2.Builder;
			FakeClientNarrator item2 = tuple2.Narrator;
			MethodInfo methodInfo = typeof(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany((Type t) => t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).FirstOrDefault((MethodInfo mi) => mi.Name.Contains("<MoveNextRemotely>", StringComparison.Ordinal));
			bool ok = false;
			if (methodInfo != null)
			{
				object obj = Activator.CreateInstance(methodInfo.DeclaringType, nonPublic: true);
				FieldInfo[] fields = methodInfo.DeclaringType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (FieldInfo fieldInfo in fields)
				{
					if (typeof(NetworkStoryContext).IsAssignableFrom(fieldInfo.FieldType))
					{
						fieldInfo.SetValue(obj, item);
					}
					else if (fieldInfo.FieldType == typeof(IAsyncStateMachine))
					{
						fieldInfo.SetValue(obj, item2.Moved);
					}
				}
				ok = ClientStories.ReadClosure(Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(methodInfo.GetParameters()[0].ParameterType), obj, methodInfo), out NetworkStoryContext context2, out object box, out Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent _) && context2 == item && new ClientStories(log).Prepare(context2, box, null, space) != null;
			}
			Expect("a real MoveNextRemotely closure (" + (methodInfo?.DeclaringType?.Name ?? "NOT FOUND") + ") is read and encoded", ok);
			(NetworkStoryContext Builder, FakeClientNarrator Narrator, TestStory<string> Story) tuple3 = StartClient<string>("ServerOnly", new Dictionary<string, object>
			{
				["target"] = clientE,
				["values"] = new int[3] { 1, 2, 3 }
			});
			NetworkStoryContext item3 = tuple3.Builder;
			FakeClientNarrator item4 = tuple3.Narrator;
			TestStory<string> item5 = tuple3.Story;
			StoryMove storyMove2 = clientStories.Prepare(item3, item4.Moved, null, space);
			hostStories.Run(peer, StoryMove.Decode(storyMove2.Encode()), fakeSink, objectSpace, null, 1, 0);
			Expect("server-only half ran", HarnessStories.Trace.Contains("server-only:host-entity:6"));
			Expect("finish came back", fakeSink.ToClient.Count == 1 && fakeSink.ToClient.TryPeek(out object result2) && result2 is StoryFinish);
			clientStories.ProcessInbox(fakeSink.ToClient, space, (Action a) =>
			{
				a();
			});
			Expect("client story result 'sum 6'", item5.Sets == 1 && (string)item5.Result == "sum 6" && item5.Error == null);
			Entity entity = EntityAllocator.AllocateStub();
			Entity entity2 = EntityAllocator.AllocateStub();
			HarnessStories.Spawned = entity;
			Dictionary<Entity, uint> dictionary = new Dictionary<Entity, uint>(ReferenceEqualityComparer.Instance) { [hostE] = 40u };
			Dictionary<Entity, uint> dictionary2 = new Dictionary<Entity, uint>(ReferenceEqualityComparer.Instance) { [clientE] = 40u };
			ObjectSpace space2 = Live("server", dictionary);
			ObjectSpace space3 = Live("client", dictionary2);
			var (context3, fakeClientNarrator2, testStory) = StartClient<Entity>("ServerSpawns", new Dictionary<string, object> { ["target"] = clientE });
			hostStories.Run(peer, StoryMove.Decode(clientStories.Prepare(context3, fakeClientNarrator2.Moved, null, space3).Encode()), fakeSink, space2, null, 1, 0);
			hostStories.RetryDeferred(fakeSink, space2);
			bool isEmpty = fakeSink.ToClient.IsEmpty;
			dictionary[entity] = 41u;
			hostStories.RetryDeferred(fakeSink, space2);
			bool flag = fakeSink.ToClient.Count == 1;
			clientStories.ProcessInbox(fakeSink.ToClient, space3, (Action a) =>
			{
				a();
			});
			bool flag2 = testStory.Sets == 0;
			dictionary2[entity2] = 41u;
			clientStories.ProcessInbox(fakeSink.ToClient, space3, (Action a) =>
			{
				a();
			});
			Expect("result with a just-spawned entity: host held it until the NetworkId existed, client until the entity arrived", (isEmpty & flag & flag2) && testStory.Sets == 1 && testStory.Result == entity2 && testStory.Error == null);
			EntityId entityId = new EntityId();
			(NetworkStoryContext Builder, FakeClientNarrator Narrator, TestStory<DBNull> Story) tuple5 = StartClient<DBNull>("Unsendable", new Dictionary<string, object> { ["id"] = entityId });
			NetworkStoryContext item6 = tuple5.Builder;
			FakeClientNarrator item7 = tuple5.Narrator;
			StoryMove storyMove3 = clientStories.Prepare(item6, item7.Moved, null, space);
			object obj2 = ((storyMove3 == null) ? null : StoryState.Decode(StoryMove.Decode(storyMove3.Encode()), objectSpace, NetworkStoryMethodBuilder.Create(), null, out string _));
			FieldInfo fieldInfo2 = obj2?.GetType().GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			Expect("EntityId story travels with the same Guid (phase 3)", storyMove3 != null && fieldInfo2 != null && (EntityId)fieldInfo2.GetValue(obj2) == entityId && clientStories.CannotTravel == 0);
			Type type = (from x in AppDomain.CurrentDomain.GetAssemblies()
				where x.GetName().Name == "Game2.Simulation"
				select x).SelectMany((Assembly x) => x.GetType("Keen.Game2.Simulation.GameSystems.Camera.CameraStatePerPlayerData", throwOnError: false)?.GetNestedTypes(BindingFlags.NonPublic) ?? Type.EmptyTypes).FirstOrDefault((Type t) => t.Name.StartsWith("<StoreCameraData>", StringComparison.Ordinal));
			NetworkStoryMethodBuilder networkStoryMethodBuilder = NetworkStoryMethodBuilder.Create();
			TestStory<DBNull> testStory2 = (TestStory<DBNull>)(((NetworkStoryContext)networkStoryMethodBuilder).Story = new TestStory<DBNull>());
			StoryMove storyMove4 = ((type == null) ? null : clientStories.Prepare(networkStoryMethodBuilder, Activator.CreateInstance(type), null, space));
			clientStories.ProcessInbox(new ConcurrentQueue<object>(), space, (Action a) =>
			{
				a();
			});
			Expect("StoreCameraData kept local, completed without error", type != null && storyMove4 == null && testStory2.Sets == 1 && testStory2.Error == null && clientStories.DeniedCount == 1);
			hostStories.Run(peer, new StoryMove
			{
				StoryId = 99u,
				StateMachineType = "No.Such+<Story>d__1, Nowhere",
				Fields = new List<(string, WireValue)>()
			}, fakeSink, objectSpace, null, 1, 0);
			Expect("unknown story skipped by the host", fakeSink.ToClient.TryDequeue(out object result3) && result3 is StoryFinish storyFinish && storyFinish.Status == 2);
			return (allOk, string.Join(", ", results) + "; " + clientStories.Describe() + "; " + hostStories.Describe());
			void Expect(string what, bool flag3)
			{
				allOk &= flag3;
				results.Add(what + " " + (flag3 ? "ok" : "WRONG"));
			}
		});
		static ObjectSpace Live(string name, Dictionary<Entity, uint> ids)
		{
			return new ObjectSpace
			{
				Name = name,
				IdOf = (Entity e) => (!ids.TryGetValue(e, out var value)) ? ((uint?)null) : new uint?(value),
				Resolve = (uint id, bool _) => ids.FirstOrDefault((KeyValuePair<Entity, uint> kv) => kv.Value == id).Key
			};
		}
	}

	public static (bool Ok, string Detail) SecureEntityAndReleasedStories()
	{
		return WithFirstChance(() =>
		{
			ObsHarnessLog obsHarnessLog = new ObsHarnessLog();
			Entity entity = EntityAllocator.AllocateStub();
			Entity entity2 = EntityAllocator.AllocateStub();
			ObjectSpace space = Space("client", new Dictionary<Entity, uint> { [entity] = 40u });
			ObjectSpace space2 = Space("server", new Dictionary<Entity, uint> { [entity2] = 40u });
			ClientStories clientStories = new ClientStories(obsHarnessLog);
			HostStories hostStories = new HostStories(obsHarnessLog);
			FakeSink fakeSink = new FakeSink();
			PeerId peer = new PeerId(78uL);
			List<string> results = new List<string>();
			bool allOk = true;
			FieldInfo fieldInfo = typeof(SecureEntities).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((Type t) => t.Name.Contains("ResolveSecureEntity", StringComparison.Ordinal) && typeof(IAsyncStateMachine).IsAssignableFrom(t))?.GetFields(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault((FieldInfo f) => typeof(Session).IsAssignableFrom(f.FieldType) || typeof(Entity).IsAssignableFrom(f.FieldType) || typeof(Component).IsAssignableFrom(f.FieldType));
			Expect($"ResolveSecureEntity's state machine carries a narrator source for Keen ({fieldInfo?.FieldType.Name} {fieldInfo?.Name})", fieldInfo != null);
			SinglePlayerSecureEntityIdsSessionComponent ids = (SinglePlayerSecureEntityIdsSessionComponent)Activator.CreateInstance(typeof(SinglePlayerSecureEntityIdsSessionComponent), nonPublic: true);
			object context = new object();
			ids.BindContextToClient(context, new ClientId(2));
			ids._trackedEntities.Add(entity2);
			EntityId idForEntity = ids.GetIdForEntity(entity2, context);
			SecureEntities.ServiceOverride = () => ids;
			try
			{
				(NetworkStoryContext Builder, FakeClientNarrator Narrator, TestStory<Entity> Story) tuple = StartClient<Entity>("ResolveSecureEntity", new Dictionary<string, object> { ["entityId"] = idForEntity }, typeof(SecureEntities));
				NetworkStoryContext item = tuple.Builder;
				FakeClientNarrator item2 = tuple.Narrator;
				TestStory<Entity> item3 = tuple.Story;
				StoryMove storyMove = ((item2.Moved == null) ? null : clientStories.Prepare(item, item2.Moved, null, space));
				if (storyMove != null)
				{
					hostStories.Run(peer, StoryMove.Decode(storyMove.Encode()), fakeSink, space2, null, 2, 2);
				}
				clientStories.ProcessInbox(fakeSink.ToClient, space, (Action a) =>
				{
					a();
				});
				Expect("secure EntityId resolved by the host's ISecureEntityIds, back as the joiner's entity", storyMove != null && item3.Sets == 1 && item3.Result == entity && item3.Error == null);
				(NetworkStoryContext Builder, FakeClientNarrator Narrator, TestStory<Entity> Story) tuple2 = StartClient<Entity>("ResolveSecureEntity", new Dictionary<string, object> { ["entityId"] = new EntityId() }, typeof(SecureEntities));
				NetworkStoryContext item4 = tuple2.Builder;
				FakeClientNarrator item5 = tuple2.Narrator;
				TestStory<Entity> item6 = tuple2.Story;
				StoryMove storyMove2 = ((item5.Moved == null) ? null : clientStories.Prepare(item4, item5.Moved, null, space));
				if (storyMove2 != null)
				{
					hostStories.Run(peer, StoryMove.Decode(storyMove2.Encode()), fakeSink, space2, null, 2, 2);
				}
				clientStories.ProcessInbox(fakeSink.ToClient, space, (Action a) =>
				{
					a();
				});
				Expect("an EntityId the host never minted comes back as null", storyMove2 != null && item6.Sets == 1 && item6.Result == null && item6.Error == null);
			}
			finally
			{
				SecureEntities.ServiceOverride = null;
			}
			Type detachType = typeof(StreamedControlPanelSessionComponent).GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault((Type t) => t.Name.StartsWith("<RequestBlockDetailDetachment>", StringComparison.Ordinal));
			BlockDetailModel model = (BlockDetailModel)Activator.CreateInstance(typeof(LightBlockDetailModel), nonPublic: true);
			ObjectSpace objectSpace = Space("client", new Dictionary<Entity, uint> { [entity] = 40u });
			objectSpace.ObservableId = (object o) => (o != model) ? ((uint?)null) : new uint?(4242u);
			NetworkStoryMethodBuilder networkStoryMethodBuilder = NetworkStoryMethodBuilder.Create();
			TestStory<DBNull> testStory = (TestStory<DBNull>)(((NetworkStoryContext)networkStoryMethodBuilder).Story = new TestStory<DBNull>());
			object obj = Box();
			StoryMove storyMove3 = ((obj != null) ? clientStories.Prepare(networkStoryMethodBuilder, obj, null, objectSpace) : null);
			ObjectSpace objectSpace2 = Space("server", new Dictionary<Entity, uint> { [entity2] = 40u });
			objectSpace2.ResolveObservable = (uint _, bool _) => (object?)null;
			if (storyMove3 != null)
			{
				hostStories.Run(peer, StoryMove.Decode(storyMove3.Encode()), fakeSink, objectSpace2, null, 2, 2);
			}
			bool flag = fakeSink.ToClient.TryPeek(out object result) && result is StoryFinish storyFinish && storyFinish.Status == 0;
			clientStories.ProcessInbox(fakeSink.ToClient, objectSpace, (Action a) =>
			{
				a();
			});
			Expect("RequestBlockDetailDetachment of a model the host released: answered as done, no warning", ((storyMove3 != null) & flag) && testStory.Sets == 1 && testStory.Error == null && hostStories.ReleasedNoOps == 1 && hostStories.Failed == 0);
			ObjectSpace objectSpace3 = Space("client", new Dictionary<Entity, uint> { [entity] = 40u });
			objectSpace3.ObservableId = (object _) => (uint?)null;
			objectSpace3.WasReleased = (object o) => o == model;
			NetworkStoryMethodBuilder networkStoryMethodBuilder2 = NetworkStoryMethodBuilder.Create();
			TestStory<DBNull> testStory2 = (TestStory<DBNull>)(((NetworkStoryContext)networkStoryMethodBuilder2).Story = new TestStory<DBNull>());
			long cannotTravel = clientStories.CannotTravel;
			object obj2 = Box();
			StoryMove storyMove4 = ((obj2 != null) ? clientStories.Prepare(networkStoryMethodBuilder2, obj2, null, objectSpace3) : null);
			clientStories.ProcessInbox(fakeSink.ToClient, objectSpace3, (Action a) =>
			{
				a();
			});
			Expect("the same story on a joiner that released the copy: done here, nothing sent", detachType != null && storyMove4 == null && testStory2.Sets == 1 && testStory2.Error == null && clientStories.ReleasedNoOps == 1 && clientStories.CannotTravel == cannotTravel && fakeSink.ToClient.IsEmpty);
			ObjectSpace objectSpace4 = Space("client", new Dictionary<Entity, uint> { [entity] = 40u });
			objectSpace4.ObservableId = (object _) => (uint?)null;
			objectSpace4.WasReleased = (object _) => false;
			NetworkStoryMethodBuilder networkStoryMethodBuilder3 = NetworkStoryMethodBuilder.Create();
			TestStory<DBNull> testStory3 = (TestStory<DBNull>)(((NetworkStoryContext)networkStoryMethodBuilder3).Story = new TestStory<DBNull>());
			object obj3 = Box();
			StoryMove storyMove5 = ((obj3 != null) ? clientStories.Prepare(networkStoryMethodBuilder3, obj3, null, objectSpace4) : null);
			clientStories.ProcessInbox(fakeSink.ToClient, objectSpace4, (Action a) =>
			{
				a();
			});
			Expect("a model never replicated here: cannot travel, skipped", storyMove5 == null && testStory3.Sets == 1 && testStory3.Error is Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException && clientStories.CannotTravel == cannotTravel + 1);
			Expect("definition lookup bound to DefinitionManager.TryGetAnyDefinition (no 'Use TryGetConfiguration' assert)", ObjectCodec.DefinitionLookupBound);
			List<string> list = obsHarnessLog.Lines.Where((string l) => l.StartsWith("WARN", StringComparison.Ordinal) && !l.Contains("cannot travel", StringComparison.Ordinal)).ToList();
			Expect("no warnings", list.Count == 0);
			return (allOk, string.Join(", ", results) + "; " + clientStories.Describe() + "; " + hostStories.Describe() + ((list.Count > 0) ? ("; " + string.Join(" | ", list.Take(3))) : ""));
			object? Box()
			{
				if (detachType == null)
				{
					return null;
				}
				object obj4 = Activator.CreateInstance(detachType);
				FieldInfo[] array = ObjectCodec.Fields(detachType);
				foreach (FieldInfo fieldInfo2 in array)
				{
					if (fieldInfo2.FieldType == typeof(SkipOnReplicationFail<BlockDetailModel>))
					{
						fieldInfo2.SetValue(obj4, new SkipOnReplicationFail<BlockDetailModel>(model));
					}
				}
				return obj4;
			}
			void Expect(string what, bool ok)
			{
				allOk &= ok;
				results.Add(what + " " + (ok ? "ok" : "WRONG"));
			}
		});
	}

	public static (bool Ok, string Detail) ToolbarLocalStory()
	{
		return WithFirstChance(() =>
		{
			Type type = typeof(PlayerToolbarServerSessionComponent).GetNestedTypes(BindingFlags.NonPublic).Single((Type t) => t.Name.StartsWith("<RequestSetTile>", StringComparison.Ordinal));
			object obj = Activator.CreateInstance(type);
			EmptyToolbarTile emptyToolbarTile = new EmptyToolbarTile();
			FieldInfo[] array = ObjectCodec.Fields(type);
			foreach (FieldInfo fieldInfo in array)
			{
				if (fieldInfo.FieldType == typeof(ToolbarSelection))
				{
					fieldInfo.SetValue(obj, new ToolbarSelection(1, 2));
				}
				else if (fieldInfo.FieldType == typeof(ReleaseToServer<ToolbarTile>))
				{
					fieldInfo.SetValue(obj, new ReleaseToServer<ToolbarTile>((ToolbarTile?)emptyToolbarTile));
				}
			}
			ToolbarPerPlayerData toolbarPerPlayerData = new ToolbarPerPlayerData();
			for (int num2 = 0; num2 < 2; num2++)
			{
				ObservableCollection<ToolbarTile> observableCollection = new ObservableCollection<ToolbarTile>();
				for (int num3 = 0; num3 < 5; num3++)
				{
					observableCollection.Add(null);
				}
				toolbarPerPlayerData.ToolbarTiles.Add(observableCollection);
			}
			int changes = 0;
			toolbarPerPlayerData.ToolbarTiles[1].CollectionChanged += (object? _, NotifyCollectionChangedEventArgs _) =>
			{
				changes++;
			};
			string text = StoryState.Name(ObjectCodec.TypeName(type));
			string value = ClientStories.ApplyToolbarStory(text, obj, toolbarPerPlayerData);
			return (text == "PlayerToolbarServerSessionComponent.RequestSetTile" && toolbarPerPlayerData.ToolbarTiles[1][2] == emptyToolbarTile && changes == 1 && toolbarPerPlayerData.ToolbarTiles[0].All((ToolbarTile t) => t == null), $"{text}: {value}; observable row raised {changes} change(s) (what the hotbar UI listens to)");
		});
	}

	public static (bool Ok, string Detail) StoryTravelReport()
	{
		return WithFirstChance(() =>
		{
			List<(string Name, string Verdict)> rows = new List<(string, string)>();
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly assembly in assemblies)
			{
				string name = assembly.GetName().Name;
				if (name != null && (name.StartsWith("Game2", StringComparison.Ordinal) || name.StartsWith("VRage", StringComparison.Ordinal) || name == "SpaceEngineers2"))
				{
					Type[] array;
					try
					{
						array = assembly.GetTypes();
					}
					catch (ReflectionTypeLoadException ex)
					{
						array = ex.Types.Where((Type t) => t != null).ToArray();
					}
					Type[] array2 = array;
					foreach (Type type in array2)
					{
						if (typeof(IAsyncStateMachine).IsAssignableFrom(type) && ObjectCodec.Fields(type).Any((FieldInfo f) => StoryState.IsBuilder(f.FieldType)))
						{
							rows.Add((StoryState.Name(ObjectCodec.TypeName(type)), StoryState.Assess(type)));
						}
					}
				}
			}
			rows = rows.OrderBy(((string Name, string Verdict) r) => r.Name).ToList();
			string text = Path.Combine(GameLog.LogDirectory(), "y51online_story_travel.txt");
			File.WriteAllLines(text, rows.Select(((string Name, string Verdict) r) => r.Name + "\t" + r.Verdict));
			int num2 = rows.Count(((string Name, string Verdict) r) => r.Verdict == "travels");
			int num3 = rows.Count(((string Name, string Verdict) r) => r.Verdict.StartsWith("travels while", StringComparison.Ordinal) || r.Verdict.StartsWith("depends", StringComparison.Ordinal));
			string[] source = new string[14]
			{
				"ToolsComponent.RequestEquipTool", "ToolsComponent.RequestDequipTool", "BlockPlacerNetworkStories.PlaceOnGrid", "BlockPlacerNetworkStories.PlaceStandalone", "BlockPlacerNetworkStories.TryDelete", "InteractorSessionComponent.DummyWasInteracted", "InteractionStatModifierComponent.RequestActivate", "SeatComponent.SetPilotStory", "CubeGridComponent.ToggleLights", "EmoteControllerComponent.RequestPlayEmote",
				"StreamedInventorySessionComponent.AttachInventoryModelToEntity", "PlayerToolbarServerSessionComponent.RequestSetTile", "CameraStatePerPlayerData.StoreCameraData", "ControlPerPlayerData.StoreControlledEntity"
			};
			return (rows.Count > 150 && V("ToolsComponent.RequestEquipTool") == "travels" && V("BlockPlacerNetworkStories.PlaceOnGrid").StartsWith("travels", StringComparison.Ordinal), $"{rows.Count} story state machines: {num2} travel, {num3} depend on values, {rows.Count - num2 - num3} cannot; " + string.Join("; ", source.Select((string k) => k + ": " + V(k))) + "; full list " + text);
			string V(string text2)
			{
				return rows.FirstOrDefault(((string Name, string Verdict) r) => r.Name == text2).Verdict ?? "absent";
			}
		});
	}

	public static (bool Ok, string Detail) ObservableTypeReport()
	{
		return WithFirstChance(() =>
		{
			List<string> list = new List<string>();
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			int num6 = 0;
			List<string> list2 = new List<string>();
			HashSet<string> found = new HashSet<string>();
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly assembly in assemblies)
			{
				string name = assembly.GetName().Name;
				if (name != null && (name.StartsWith("Game2", StringComparison.Ordinal) || name.StartsWith("VRage", StringComparison.Ordinal) || name == "SpaceEngineers2"))
				{
					Type[] source;
					try
					{
						source = assembly.GetTypes();
					}
					catch (ReflectionTypeLoadException ex)
					{
						source = ex.Types.Where((Type t) => t != null).ToArray();
					}
					foreach (Type item in from t in source
						where t.GetCustomAttribute<ReplicateAttribute>(inherit: false) != null
						orderby t.FullName
						select t)
					{
						num++;
						found.Add(item.Name);
						if (!MetadataIndexerBase<AccessibleTypeIndexer>.Instance.TryGetAccessibleTypeInfo(item, out AccessibleTypeInfo typeInfo))
						{
							list.Add(item.FullName + "\t(no accessible type info: Keen cannot replicate it)");
						}
						else
						{
							list.Add(item.FullName + (item.IsAbstract ? " (abstract)" : ""));
							foreach (AccessibleTypeInfo.MemberInfo orderedMember in typeInfo.OrderedMembers)
							{
								if (!ObservableRules.IsNoReplicate(orderedMember))
								{
									num2++;
									bool flag = ObservableRules.IsSyncFromClients(orderedMember);
									if (flag)
									{
										num4++;
									}
									string value;
									if (typeof(IList).IsAssignableFrom(orderedMember.Type) && typeof(INotifyCollectionChanged).IsAssignableFrom(orderedMember.Type))
									{
										num3++;
										value = "observed list (by Oid)";
									}
									else if (!orderedMember.CanWrite)
									{
										num6++;
										value = "read-only: not replicated";
									}
									else if (ObservableRules.ElementType(orderedMember.Type).Name == "AttachObservablePropertyBindingOp")
									{
										value = "server-only binding bookkeeping: null on joiners";
									}
									else if (typeof(Delegate).IsAssignableFrom(orderedMember.Type))
									{
										value = "client-local callback: null on joiners (their own UI subscribes)";
									}
									else if (ObjectCodec.ProcessLocal(orderedMember.Type, 0) == "DEntity")
									{
										value = "opaque handle: the host's bytes (only compared between models)";
									}
									else
									{
										string text = ObjectCodec.TypeProblem(orderedMember.Type);
										value = ((text == null) ? "travels" : text);
										if (text != null && !text.Contains("depends on the value", StringComparison.Ordinal))
										{
											num5++;
											if (list2.Count < 12)
											{
												list2.Add($"{item.Name}.{orderedMember.Accessor.Name} ({text})");
											}
										}
									}
									list.Add($"\t{orderedMember.Accessor.Name}\t{orderedMember.Type.Name}\t{value}{(flag ? "\t[SyncFromClients]" : "")}");
								}
							}
						}
					}
				}
			}
			string text2 = Path.Combine(GameLog.LogDirectory(), "y51online_observable_types.txt");
			File.WriteAllLines(text2, list);
			List<string> list3 = new string[9] { "PlayerPcuModel", "InventoryListModelStreamed", "InventoryItemModel", "ControlPanelListModel", "LightBlockDetailModel", "ProductionScreenModelStreamed", "GridStatusModel", "InventoryFullnessModel", "CharacterConsumablesModel" }.Where((string k) => !found.Contains(k)).ToList();
			return (num > 50 && list3.Count == 0 && num4 > 20, $"{num} [Replicate] types, {num2} replicating members ({num3} observed lists, {num6} read-only, {num4} [SyncFromClients]); {num5} member types the codec cannot carry{((list2.Count > 0) ? (": " + string.Join("; ", list2)) : "")}" + ((list3.Count > 0) ? ("; MISSING " + string.Join(",", list3)) : "") + "; full list " + text2);
		});
	}

	private static List<(string Kind, ITuple Args)> DrainSignals(ObservableObjectReplicationSessionComponent comp)
	{
		List<(string, ITuple)> list = new List<(string, ITuple)>();
		Queue<Action> queuedSignals = comp._queuedSignals;
		Action result;
		while (queuedSignals.TryDequeue(out result))
		{
			object target = result.Target;
			FieldInfo[] fields = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			object value = fields.First((FieldInfo f) => f.Name == "state").GetValue(target);
			Delegate signal = (Delegate)fields.First((FieldInfo f) => f.Name == "signal").GetValue(target);
			string item = SignalKinds.First((string k) => signal.Method.Name.Contains("<Send" + k + ">", StringComparison.Ordinal));
			list.Add((item, (ITuple)((value as ITuple) ?? ((object)new ValueTuple<object>(value)))));
		}
		return list;
	}

	private static T Make<T>() where T : class
	{
		return (T)Activator.CreateInstance(typeof(T), nonPublic: true);
	}

	private static Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent ReplicationOn(ReplayContext rc)
	{
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent = (Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent)RuntimeHelpers.GetUninitializedObject(typeof(Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext.ServerSessionComponent));
		typeof(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent).GetField("_replayContext", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(inProcessReplicationSessionComponent, rc);
		return inProcessReplicationSessionComponent;
	}

	private static void SetReplication(ObservableObjectReplicationSessionComponent comp, ReplayContext rc)
	{
		typeof(ObservableObjectReplicationSessionComponent).GetField("_replication", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(comp, ReplicationOn(rc));
	}

	public static (bool Ok, string Detail) ObservableRoundTrip()
	{
		return WithFirstChance(() =>
		{
			try
			{
				return ((bool, string))ObservableRoundTripCore();
			}
			catch (Exception ex)
			{
				return (false, ex.ToString());
			}
		});
	}

	private static (bool Ok, string Detail) ObservableRoundTripCore()
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Expected Obj, but got Unknown
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		ImmutableArray<PrefabBindingDefinition> empty = ImmutableArray<PrefabBindingDefinition>.Empty;
		InProcessReplication val = new InProcessReplication(empty);
		ReplayContext serverRc = val.Server.ReplayContext;
		ReplayContext hostClientRc = val.Client.ReplayContext;
		ObservableObjectReplicationServerSessionComponent server = Make<ObservableObjectReplicationServerSessionComponent>();
		SetReplication(server, serverRc);
		typeof(ObservableObjectReplicationServerSessionComponent).GetField("_secureEntityIds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(server, Make<SinglePlayerSecureEntityIdsSessionComponent>());
		ObservableObjectReplicationClientSessionComponent hostClient = Make<ObservableObjectReplicationClientSessionComponent>();
		SetReplication(hostClient, hostClientRc);
		ObsHarnessLog obsHarnessLog = new ObsHarnessLog();
		HostObservables host = new HostObservables(obsHarnessLog);
		PeerId peerA = new PeerId(1001uL);
		PeerId peerId = new PeerId(1002uL);
		Dictionary<PeerId, (ObservableObjectReplicationClientSessionComponent Comp, ClientObservables Obs)> joiners = new Dictionary<PeerId, (ObservableObjectReplicationClientSessionComponent, ClientObservables)>();
		PeerId[] array = new PeerId[2] { peerA, peerId };
		foreach (PeerId key in array)
		{
			ObservableObjectReplicationClientSessionComponent observableObjectReplicationClientSessionComponent = Make<ObservableObjectReplicationClientSessionComponent>();
			SetReplication(observableObjectReplicationClientSessionComponent, new InProcessReplication(empty).Client.ReplayContext);
			joiners[key] = (observableObjectReplicationClientSessionComponent, new ClientObservables(obsHarnessLog));
		}
		List<string> trace = new List<string>();
		int confirmsKept = 0;
		PlayerPcuModel playerPcuModel = new PlayerPcuModel
		{
			PCU = 5,
			MaxPCU = 400
		};
		ControlPanelListModel controlPanelListModel = new ControlPanelListModel
		{
			MainEntity = new EntityId()
		};
		ObservableList<ControlPanelEntityModel> blocks = controlPanelListModel.Blocks;
		ControlPanelEntityModel controlPanelEntityModel = new ControlPanelEntityModel
		{
			EntityId = new EntityId()
		};
		controlPanelEntityModel.DetailViewTypes = new HashSet<SubclassOf<BlockDetailModel>> { typeof(LightBlockDetailModel) };
		controlPanelListModel.Blocks.Add(controlPanelEntityModel);
		AntennaMarkersSessionComponent.AntennaHUDMarker antennaHUDMarker = new AntennaMarkersSessionComponent.AntennaHUDMarker
		{
			Name = "Alpha",
			Hidden = false
		};
		ProductionScreenModelStreamed productionScreenModelStreamed = new ProductionScreenModelStreamed
		{
			MainEntity = new EntityId()
		};
		byte[] array2 = new byte[Unsafe.SizeOf<DEntity>()];
		for (int j = 0; j < array2.Length; j++)
		{
			array2[j] = (byte)(49 + j);
		}
		productionScreenModelStreamed.LeakedId = MemoryMarshal.Read<DEntity>(array2);
		IDisposable disposable = ((ObservableObjectReplicationSessionComponent)server).RegisterObservable((ObservableObject)productionScreenModelStreamed);
		IDisposable disposable2 = ((ObservableObjectReplicationSessionComponent)server).RegisterObservable((ObservableObject)playerPcuModel);
		IDisposable disposable3 = ((ObservableObjectReplicationSessionComponent)server).RegisterObservable((ObservableObject)controlPanelListModel);
		IDisposable disposable4 = ((ObservableObjectReplicationSessionComponent)server).RegisterObservable((ObservableObject)antennaHUDMarker);
		PumpServer();
		uint? num = host.OidOnServer(playerPcuModel, serverRc);
		uint? num2 = host.OidOnServer(controlPanelListModel, serverRc);
		uint? num3 = host.OidOnServer(antennaHUDMarker, serverRc);
		uint? num4 = host.OidOnServer(productionScreenModelStreamed, serverRc);
		bool flag = num.HasValue && num2.HasValue && num3.HasValue && num4.HasValue;
		host.AddRoots(peerA, new uint[4] { num.Value, num2.Value, num3.Value, num4.Value });
		host.AddRoots(peerId, new uint[1] { num3.Value });
		Flush();
		(ObservableObjectReplicationClientSessionComponent Comp, ClientObservables Obs) tuple = joiners[peerA];
		ObservableObjectReplicationClientSessionComponent item = tuple.Comp;
		ClientObservables item2 = tuple.Obs;
		(ObservableObjectReplicationClientSessionComponent Comp, ClientObservables Obs) tuple2 = joiners[peerId];
		ObservableObjectReplicationClientSessionComponent item3 = tuple2.Comp;
		ClientObservables item4 = tuple2.Obs;
		PlayerPcuModel playerPcuModel2 = item2.Resolve(num.Value) as PlayerPcuModel;
		ControlPanelListModel controlPanelListModel2 = item2.Resolve(num2.Value) as ControlPanelListModel;
		AntennaMarkersSessionComponent.AntennaHUDMarker antennaHUDMarker2 = item2.Resolve(num3.Value) as AntennaMarkersSessionComponent.AntennaHUDMarker;
		AntennaMarkersSessionComponent.AntennaHUDMarker antennaHUDMarker3 = item4.Resolve(num3.Value) as AntennaMarkersSessionComponent.AntennaHUDMarker;
		int num5;
		if (playerPcuModel2 != null && playerPcuModel2.PCU == 5 && playerPcuModel2.MaxPCU == 400 && controlPanelListModel2 != null && controlPanelListModel2.MainEntity == controlPanelListModel.MainEntity && controlPanelListModel2.Blocks.Count == 1 && controlPanelListModel2.Blocks[0].EntityId == controlPanelEntityModel.EntityId)
		{
			HashSet<SubclassOf<BlockDetailModel>> detailViewTypes = controlPanelListModel2.Blocks[0].DetailViewTypes;
			if (detailViewTypes != null && detailViewTypes.Contains(typeof(LightBlockDetailModel)) && antennaHUDMarker2 != null && antennaHUDMarker2.Name == "Alpha" && !antennaHUDMarker2.Hidden && antennaHUDMarker3 != null && antennaHUDMarker3.Name == "Alpha" && item4.Resolve(num.Value) == null)
			{
				num5 = ((item4.Resolve(num2.Value) == null) ? 1 : 0);
				goto IL_04e4;
			}
		}
		num5 = 0;
		goto IL_04e4;
		IL_04e4:
		bool flag2 = (byte)num5 != 0;
		bool flag3 = item2.Resolve(num4.Value) is ProductionScreenModelStreamed productionScreenModelStreamed2 && productionScreenModelStreamed2.LeakedId.Equals(productionScreenModelStreamed.LeakedId) && productionScreenModelStreamed2.MainEntity == productionScreenModelStreamed.MainEntity && productionScreenModelStreamed2.Queue.Count == 0 && item2.IdOf(productionScreenModelStreamed2.Queue).HasValue && item2.IdOf(productionScreenModelStreamed2.Entities).HasValue;
		bool flag4 = ((ObservableObjectReplicationSessionComponent)item).ObservedObjects.Count == 5 && ((ObservableObjectReplicationSessionComponent)item3).ObservedObjects.Count == 1 && ((ObservableObjectReplicationSessionComponent)item).ObservedObjects.ContainsKey(controlPanelListModel2) && ((ObservableObjectReplicationSessionComponent)item3).ObservedObjects.ContainsKey(antennaHUDMarker3);
		object obj = controlPanelListModel2?.Blocks;
		string text = $"built A {item2.Count} (Keen observed {((ObservableObjectReplicationSessionComponent)item).ObservedObjects.Count}), B {item4.Count} (Keen observed {((ObservableObjectReplicationSessionComponent)item3).ObservedObjects.Count}); adopted list {((obj != null && item2.IdOf(obj).HasValue) ? "yes" : "NO")}";
		flag &= (flag2 & flag4 & flag3) && obj != null && item2.IdOf(obj).HasValue && blocks != obj;
		text = text + "; production model (read-only lists adopted, DEntity kept as the host's bytes) " + (flag3 ? "ok" : "WRONG");
		long num6 = opsTo(peerId);
		playerPcuModel.PCU = 42;
		ControlPanelEntityModel controlPanelEntityModel2 = new ControlPanelEntityModel
		{
			EntityId = new EntityId()
		};
		controlPanelListModel.Blocks.Add(controlPanelEntityModel2);
		Flush();
		bool flag5 = playerPcuModel2.PCU == 42 && controlPanelListModel2.Blocks.Count == 2 && controlPanelListModel2.Blocks[1].EntityId == controlPanelEntityModel2.EntityId && opsTo(peerId) == num6;
		int count = ((ObservableObjectReplicationSessionComponent)item).ObservedObjects.Count;
		controlPanelListModel.Blocks.RemoveAt(0);
		Flush();
		bool flag6 = controlPanelListModel2.Blocks.Count == 1 && controlPanelListModel2.Blocks[0].EntityId == controlPanelEntityModel2.EntityId && ((ObservableObjectReplicationSessionComponent)item).ObservedObjects.Count == count - 1;
		flag &= flag5 & flag6;
		long num7 = opsTo(peerA);
		num6 = opsTo(peerId);
		antennaHUDMarker2.Hidden = true;
		Flush();
		Flush();
		bool flag7 = antennaHUDMarker.Hidden && antennaHUDMarker3.Hidden && opsTo(peerA) == num7 && opsTo(peerId) > num6;
		AntennaMarkersSessionComponent.AntennaHUDMarker antennaHUDMarker4 = hostClientRc.TranslateObject(antennaHUDMarker) as AntennaMarkersSessionComponent.AntennaHUDMarker;
		antennaHUDMarker4.Name = "Bravo";
		PumpHostClient();
		Flush();
		bool flag8 = antennaHUDMarker.Name == "Bravo" && antennaHUDMarker2.Name == "Bravo" && antennaHUDMarker3.Name == "Bravo";
		flag &= flag7 & flag8;
		ObjectSpace objectSpace = new ObjectSpace
		{
			Name = "server",
			IdOf = (Entity _) => (uint?)null,
			Resolve = (uint _, bool _) => (Entity?)null,
			ObservableId = (object model) => host.OidOnServer(model, serverRc),
			ResolveObservable = (uint oid, bool inCollection) => host.ResolveOnServer(oid, inCollection, serverRc)
		};
		Dictionary<EntityId, List<ObservableObject>> o = new Dictionary<EntityId, List<ObservableObject>> { [controlPanelEntityModel.EntityId] = new List<ObservableObject> { antennaHUDMarker4 } };
		string fail = null;
		WireValue v = ObjectCodec.Encode(o, typeof(Dictionary<EntityId, List<ObservableObject>>), objectSpace, ref fail);
		Dictionary<EntityId, List<ObservableObject>> dictionary = ((fail == null) ? (ObjectCodec.Decode(v, typeof(Dictionary<EntityId, List<ObservableObject>>), JoinerSpace(item2), translated: false, ref fail) as Dictionary<EntityId, List<ObservableObject>>) : null);
		bool flag9 = fail == null && dictionary != null && dictionary.TryGetValue(controlPanelEntityModel.EntityId, out var value) && value[0] == antennaHUDMarker2 && objectSpace.Observed.Contains(num3.Value);
		SkipOnReplicationFail<ControlPanelListModel> skipOnReplicationFail = new SkipOnReplicationFail<ControlPanelListModel>(controlPanelListModel2);
		fail = null;
		WireValue v2 = ObjectCodec.Encode(skipOnReplicationFail, skipOnReplicationFail.GetType(), JoinerSpace(item2), ref fail);
		SkipOnReplicationFail<ControlPanelListModel> skipOnReplicationFail2 = ((fail == null) ? ((SkipOnReplicationFail<ControlPanelListModel>)ObjectCodec.Decode(v2, skipOnReplicationFail.GetType(), objectSpace, translated: false, ref fail)) : default(SkipOnReplicationFail<ControlPanelListModel>));
		List<ControlPanelListModel> list = ((fail == null) ? (ObjectCodec.Decode(ObjectCodec.Encode(new List<ControlPanelListModel> { controlPanelListModel2 }, typeof(List<ControlPanelListModel>), JoinerSpace(item2), ref fail), typeof(List<ControlPanelListModel>), objectSpace, translated: false, ref fail) as List<ControlPanelListModel>) : null);
		bool flag10 = fail == null && skipOnReplicationFail2.Value == controlPanelListModel && list != null && list[0] == hostClientRc.TranslateObject(controlPanelListModel);
		HashSet<SubclassOf<BlockDetailModel>> hashSet = new HashSet<SubclassOf<BlockDetailModel>> { typeof(LightBlockDetailModel) };
		HashSet<SubclassOf<BlockDetailModel>> hashSet2 = ObjectCodec.Decode(ObjectCodec.Encode(hashSet, hashSet.GetType(), objectSpace, ref fail), typeof(IReadOnlySet<SubclassOf<BlockDetailModel>>), objectSpace, translated: false, ref fail) as HashSet<SubclassOf<BlockDetailModel>>;
		bool flag11 = fail == null && hashSet2 != null && hashSet2.SetEquals(hashSet);
		flag &= flag9 & flag10 & flag11;
		int count2 = ((ObservableObjectReplicationSessionComponent)item).ObservedObjects.Count;
		disposable3.Dispose();
		Flush();
		bool flag12 = item2.Resolve(num2.Value) == null && ((ObservableObjectReplicationSessionComponent)item).ObservedObjects.Count == count2 - 2 && !((ObservableObjectReplicationSessionComponent)item).ObservedObjects.ContainsKey(controlPanelListModel2) && confirmsKept >= 2;
		disposable4.Dispose();
		disposable2.Dispose();
		disposable.Dispose();
		Flush();
		bool flag13 = ((ObservableObjectReplicationSessionComponent)item).ObservedObjects.Count == 0 && ((ObservableObjectReplicationSessionComponent)item3).ObservedObjects.Count == 0 && item2.Count == 0 && item4.Count == 0 && host.Count == 0;
		string fail2 = null;
		string fail3 = null;
		ObjectCodec.Encode(new SkipOnReplicationFail<ControlPanelListModel>(controlPanelListModel2), typeof(SkipOnReplicationFail<ControlPanelListModel>), JoinerSpace(item2), ref fail2);
		ObjectCodec.Encode(new SkipOnReplicationFail<ControlPanelListModel>(new ControlPanelListModel()), typeof(SkipOnReplicationFail<ControlPanelListModel>), JoinerSpace(item2), ref fail3);
		bool flag14 = item2.WasReleased(controlPanelListModel2) && fail2 != null && fail2.Contains("was already released by the host", StringComparison.Ordinal) && fail3 != null && !fail3.Contains("was already released by the host", StringComparison.Ordinal);
		flag &= flag12 & flag13 & flag14;
		string text2 = string.Join(" | ", obsHarnessLog.Lines.Where((string l) => !l.StartsWith("INFO", StringComparison.Ordinal)).Take(4));
		flag &= !obsHarnessLog.Lines.Any((string l) => l.StartsWith("WARN", StringComparison.Ordinal) || l.StartsWith("ERROR", StringComparison.Ordinal));
		return (Ok: flag, Detail: $"{text}; registration+snapshot {((flag2 & flag4) ? "ok" : "FAILED")}, server changes {(flag5 ? "ok" : "FAILED")}, item removal+release {(flag6 ? "ok" : "FAILED")}, joiner edit (server Hidden={antennaHUDMarker.Hidden}, origin skipped) {(flag7 ? "ok" : "FAILED")}, host edit {(flag8 ? "ok" : "FAILED")}, story result dict {(flag9 ? "ok" : "FAILED")}, story field -> server model / list item -> host copy {(flag10 ? "ok" : "FAILED")}, set {(flag11 ? "ok" : "FAILED")}, release {(flag12 ? "ok" : "FAILED")}, all released {(flag13 ? "ok" : "FAILED")}, a released copy named in a story {(flag14 ? "recognised (0.7.2)" : $"WRONG ({fail2} / {fail3})")}; {trace.Count} steps sent ({opsTo(peerA)} to A, {opsTo(peerId)} to B), confirmations kept here {confirmsKept}; {host.Describe()}" + ((fail != null) ? ("; codec: " + fail) : "") + ((text2.Length > 0) ? ("; log: " + text2) : ""));
		void Flush()
		{
			PumpServer();
			host.Flush((PeerId peer, byte[] msg) =>
			{
				List<ObservableOp> item6 = ObservableBatchMessage.Decode(ObservableBatchMessage.Encode(0u, ObservableBatchMessage.Decode(msg).Ops)).Ops;
				foreach (ObservableOp item7 in item6)
				{
					trace.Add($"{peer}: {item7}");
				}
				(ObservableObjectReplicationClientSessionComponent, ClientObservables) tuple7 = joiners[peer];
				tuple7.Item2.Apply(tuple7.Item1, item6, JoinerSpace(tuple7.Item2));
				return true;
			}, null);
			PumpHostClient();
			foreach (var (peerId3, tuple4) in joiners)
			{
				foreach (var (text3, tuple6) in DrainSignals(tuple4.Item1))
				{
					if (text3 == "ReplicateMemberChanged")
					{
						tuple4.Item2.OnLocalEdit(tuple6[0], (string)tuple6[1], tuple6[2], JoinerSpace(tuple4.Item2));
					}
					else
					{
						if (!text3.StartsWith("Confirm", StringComparison.Ordinal))
						{
							throw new InvalidOperationException("joiner sent " + text3);
						}
						confirmsKept++;
					}
				}
				List<MemberEdit> list2 = tuple4.Item2.TakeEdits();
				if (list2 != null)
				{
					List<MemberEdit> item5 = ObservableEditsMessage.Decode(ObservableEditsMessage.Encode(1u, list2)).Edits;
					host.QueueEdits(peerId3, (ushort)((peerId3 == peerA) ? 2 : 3), item5);
				}
			}
		}
		static ObjectSpace JoinerSpace(ClientObservables clientObservables)
		{
			return clientObservables.Attach(new ObjectSpace
			{
				Name = "joiner",
				IdOf = (Entity _) => (uint?)null,
				Resolve = (uint _, bool _) => (Entity?)null
			});
		}
		void PumpHostClient()
		{
			//IL_008c: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
			foreach (var (text3, tuple4) in DrainSignals(hostClient))
			{
				switch (text3)
				{
				case "ReplicateMemberChanged":
					((ObservableObjectReplicationSessionComponent)server).OnReplicateMemberChanged((ObservableObject)tuple4[0], (string)tuple4[1], tuple4[2]);
					break;
				case "ConfirmReplicatedObjectReleased":
					server.OnConfirmReplicatedObjectReleased((InProcessPairingKey<ObservableObject>)tuple4[0]);
					break;
				case "ConfirmReplicatedCollectionReleased":
					server.OnConfirmReplicatedCollectionReleased((InProcessPairingKey<IObservedCollection>)tuple4[0]);
					break;
				default:
					throw new InvalidOperationException("host client sent " + text3);
				}
			}
		}
		void PumpServer()
		{
			//IL_0096: Unknown result type (might be due to invalid IL or missing references)
			//IL_00de: Unknown result type (might be due to invalid IL or missing references)
			//IL_0135: Unknown result type (might be due to invalid IL or missing references)
			//IL_016e: Unknown result type (might be due to invalid IL or missing references)
			foreach (var (text3, tuple4) in DrainSignals(server))
			{
				switch (text3)
				{
				case "ReplicateNewObject":
					hostClient.OnReplicateNewObject((InProcessPairingKey<ObservableObject>)tuple4[0], (Type)tuple4[1], tuple4[2]);
					break;
				case "ReleaseReplicatedObject":
					host.OnRelease(tuple4[1], list: false, hostClientRc);
					hostClient.OnReleaseReplicatedObject((InProcessPairingKey<ObservableObject>)tuple4[0], (ObservableObject)tuple4[1]);
					break;
				case "ReplicateMemberChanged":
					((ObservableObjectReplicationSessionComponent)hostClient).OnReplicateMemberChanged((ObservableObject)tuple4[0], (string)tuple4[1], tuple4[2]);
					break;
				case "ReplicateNewCollection":
					hostClient.OnReplicateNewCollection((InProcessPairingKey<IObservedCollection>)tuple4[0], tuple4[1]);
					break;
				case "ReleaseReplicatedCollection":
					host.OnRelease(tuple4[1], list: true, hostClientRc);
					hostClient.OnReleaseReplicatedCollection((InProcessPairingKey<IObservedCollection>)tuple4[0], (IObservedCollection)tuple4[1]);
					break;
				case "ReplicateCollectionChanged":
					hostClient.OnReplicateCollectionChanged((IObservedCollection)tuple4[0], (ReplicatedCollectionChange)tuple4[1]);
					break;
				default:
					throw new InvalidOperationException("server sent " + text3);
				}
			}
		}
		long opsTo(PeerId p)
		{
			return trace.Count((string t) => t.StartsWith(p.ToString() + ":", StringComparison.Ordinal));
		}
	}

	public static (bool Ok, string Detail) ObservablePlumbingLayouts()
	{
		return WithFirstChance(() =>
		{
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			//IL_009c: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e6: Unknown result type (might be due to invalid IL or missing references)
			ObsHarnessLog log = new ObsHarnessLog();
			Entity entity = EntityAllocator.AllocateStub();
			Entity mine = EntityAllocator.AllocateStub();
			PlayerPcuModel playerPcuModel = new PlayerPcuModel();
			PlayerPcuModel playerPcuModel2 = new PlayerPcuModel();
			ReplicationCommandBuffer replicationCommandBuffer = new ReplicationCommandBuffer();
			VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader replicationCommandBufferWriterReader = new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(replicationCommandBuffer);
			int assemblyOffset = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
			replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_ObservableObjectReplicationSessionComponent_ReplicateMemberChangedSignal(assemblyOffset + 6, entity, (ObservableObject)playerPcuModel, "PCU", (object)17);
			replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_ObservableObjectReplicationSessionComponent_ConfirmReplicatedObjectReleasedSignal(assemblyOffset + 5, entity, new InProcessPairingKey<ObservableObject>((ObservableObject)playerPcuModel));
			int typeId = RuntimeDataInfo.Of(typeof(WorldTransform)).TypeId;
			replicationCommandBufferWriterReader.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(assemblyOffset + 1, entity, mine, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(typeId, "\t\t\t\t"u8));
			ClientUpstream clientUpstream = new ClientUpstream(log);
			List<(object?, string?, object?)> edits = new List<(object, string, object)>();
			clientUpstream.OnObservableEdit = (object? r, string? n, object? v) =>
			{
				edits.Add((r, n, v));
			};
			clientUpstream.Capture(replicationCommandBuffer, (Entity? e) => (e != mine) ? ((uint?)null) : new uint?(7u), new ObjectSpace
			{
				Name = "client",
				IdOf = (Entity _) => (uint?)null,
				Resolve = (uint _, bool _) => (Entity?)null
			}, null);
			int num;
			if (edits.Count == 1 && edits[0].Item1 == playerPcuModel && edits[0].Item2 == "PCU")
			{
				object item = edits[0].Item3;
				if (item is int && (int)item == 17 && clientUpstream.ObservableSwallowed == 1 && clientUpstream.CurrentInput.Count == 1)
				{
					num = ((clientUpstream.Unparsed == 0) ? 1 : 0);
					goto IL_01cb;
				}
			}
			num = 0;
			goto IL_01cb;
			IL_01cb:
			bool flag = (byte)num != 0;
			ReplicationCommandBuffer replicationCommandBuffer2 = new ReplicationCommandBuffer();
			new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(replicationCommandBuffer2).Write_Keen_VRage_Multiplayer_Replications_ObservableObjectReplicationSessionComponent_ReleaseReplicatedObjectSignal(assemblyOffset + 4, entity, new InProcessPairingKey<ObservableObject>((ObservableObject)playerPcuModel2), (ObservableObject)playerPcuModel2);
			byte[] array = CommandBuffers.Bytes(replicationCommandBuffer2, 0);
			CommandLayout commandLayout = CommandLayouts.ByName("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedObjectSignal");
			bool flag2 = commandLayout != null && BitConverter.ToInt32(array, 4) == commandLayout.Stub && commandLayout.PayloadBytes == array.Length - 8 && replicationCommandBuffer2.ReadObject(BitConverter.ToInt32(array, 16)) == playerPcuModel2;
			CommandLayout commandLayout2 = CommandLayouts.ByName("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedCollectionSignal");
			CommandLayout commandLayout3 = CommandLayouts.ByName("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReplicateMemberChangedSignal");
			bool flag3 = commandLayout2 != null && commandLayout2.PayloadBytes == 12 && !commandLayout2.Forwardable && commandLayout3 != null && commandLayout3.PayloadBytes == 16 && !commandLayout3.Forwardable;
			return (flag & flag2 & flag3, $"upstream: edit {(flag ? "captured (copy, PCU, 17), confirmation kept, the PostData after them still read" : ("WRONG: " + clientUpstream.Describe()))}; release tap slot {(flag2 ? "ok (third slot = the server object)" : "WRONG")}; layouts {(flag3 ? "sized, not forwarded" : "WRONG")}");
		});
	}

	private static Entity SessionEntity()
	{
		Session userObject = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
		Scene scene = (Scene)RuntimeHelpers.GetUninitializedObject(typeof(Scene));
		scene.UserObject = userObject;
		Entity entity = EntityAllocator.AllocateStub();
		typeof(Entity).GetProperty("Data").SetValue(entity, new DEntityContext(scene, default));
		return entity;
	}

	private static T Uninit<T>() where T : Component
	{
		T val = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
		ComponentEntity.SetValue(val, SessionEntity());
		return val;
	}

	private static ToolbarPerPlayerData Toolbar(int rows)
	{
		ToolbarPerPlayerData toolbarPerPlayerData = new ToolbarPerPlayerData();
		for (int i = 0; i < rows; i++)
		{
			toolbarPerPlayerData.ToolbarTiles.Add(new ObservableCollection<ToolbarTile>());
		}
		return toolbarPerPlayerData;
	}

	public static (bool Ok, string Detail) IdentityAllocatorTrap()
	{
		return WithFirstChance(() =>
		{
			OwnershipSessionComponent ownershipSessionComponent = (OwnershipSessionComponent)RuntimeHelpers.GetUninitializedObject(typeof(OwnershipSessionComponent));
			ownershipSessionComponent._identities = new HashSet<IdentityId>();
			long num = ownershipSessionComponent.AllocateIdentity();
			ownershipSessionComponent._identities.Clear();
			ownershipSessionComponent._identities.Add(InProcessPerPlayerDataSessionComponent.HotfixIdentity);
			long num2 = ownershipSessionComponent.AllocateIdentity();
			return (num == -1559595548 && num2 == 1649316165, $"AllocateIdentity on an empty set = {num} (HotfixIdentity: the trap, so Y51_SE2Online derives identities itself), on {{Hotfix}} = {num2}");
		});
	}

	public static (bool Ok, string Detail) PerPlayerDataSplit()
	{
		return WithFirstChance(() =>
		{
			HostIdentities current = HostIdentities.Current;
			if (current == null)
			{
				return (false, "HostIdentities not installed (host mode with identities on expected)");
			}
			IdentityId hotfixIdentity = InProcessPerPlayerDataSessionComponent.HotfixIdentity;
			IdentityId x = new IdentityId(PlayerIdentities.Derive("name:HarnessJoiner"));
			IdentityId identityId = new IdentityId(PlayerIdentities.Derive("name:Nobody"));
			InProcessPerPlayerDataSessionComponent inProcessPerPlayerDataSessionComponent = Uninit<InProcessPerPlayerDataSessionComponent>();
			Unsafe.AsRef(in inProcessPerPlayerDataSessionComponent._npc) = (INpcIdentityProvider)(object)new Npc();
			Unsafe.AsRef(in inProcessPerPlayerDataSessionComponent._serverTypeCache) = new ConcurrentDictionary<Type, Type>();
			Unsafe.AsRef(in inProcessPerPlayerDataSessionComponent._observedEntities) = new HashSet<Entity>();
			ToolbarPerPlayerData toolbarPerPlayerData = Toolbar(1);
			ToolbarPerPlayerData joinTb = Toolbar(2);
			PlayerCharacterData playerCharacterData = new PlayerCharacterData
			{
				PlayerName = "HarnessJoiner"
			};
			ToolbarPerPlayerData toolbarPerPlayerData2 = Toolbar(3);
			inProcessPerPlayerDataSessionComponent.Init(new PerPlayerDataSessionComponentObjectBuilder
			{
				PerPlayerData = 
				{
					{
						hotfixIdentity,
						(object)toolbarPerPlayerData
					},
					{
						x,
						(object)joinTb
					},
					{
						x,
						(object)playerCharacterData
					},
					{
						new IdentityId(1L),
						(object)toolbarPerPlayerData2
					}
				}
			});
			List<string> results = new List<string>();
			bool all = true;
			List<object> list = default;
			Expect("host's records unchanged (its toolbar wins over the NPC's, as vanilla folds NPC keys)", ((CollectionDictionary<IdentityId, List<object>, object>)(object)inProcessPerPlayerDataSessionComponent._perPlayerData).TryGet(hotfixIdentity, out list) && list.Count == 1 && list[0] == toolbarPerPlayerData);
			List<object> list2 = default;
			Expect("joiner's saved records kept as its own identity", ((CollectionDictionary<IdentityId, List<object>, object>)(object)inProcessPerPlayerDataSessionComponent._perPlayerData).TryGet(x, out list2) && list2.Count == 2 && list2.Contains(joinTb) && list2.Contains(playerCharacterData));
			Expect("joiner has its own replicated entry", inProcessPerPlayerDataSessionComponent._clientVisibleData.Data.Any((InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData d) => (long)d.IdentityId == (long)x && d.Data.Contains((PerPlayerData)(object)joinTb)));
			Expect("NPC provider wrapped", inProcessPerPlayerDataSessionComponent._npc is JoinerAwareNpcProvider);
			Expect("GetPerPlayerData(joiner) = its own toolbar", inProcessPerPlayerDataSessionComponent.GetPerPlayerData<ToolbarPerPlayerData>(x) == joinTb);
			Expect("GetPerPlayerData(joiner) = its own suit/name record", inProcessPerPlayerDataSessionComponent.GetPerPlayerData<PlayerCharacterData>(x) == playerCharacterData);
			Expect("an unknown identity still folds into the host's", inProcessPerPlayerDataSessionComponent.GetPerPlayerData<ToolbarPerPlayerData>(identityId) == toolbarPerPlayerData);
			Expect("wrapper: joiner and NPC un-folded, unknown folded", inProcessPerPlayerDataSessionComponent._npc.IsNpc(x) && inProcessPerPlayerDataSessionComponent._npc.IsNpc(new IdentityId(1L)) && !inProcessPerPlayerDataSessionComponent._npc.IsNpc(identityId));
			string text;
			try
			{
				int num = HostIdentities.ShareResearch(inProcessPerPlayerDataSessionComponent, x);
				PlayerProgressionData perPlayerData = inProcessPerPlayerDataSessionComponent.GetPerPlayerData<PlayerProgressionData>(hotfixIdentity);
				bool flag = inProcessPerPlayerDataSessionComponent.GetPerPlayerData<PlayerProgressionData>(x) == perPlayerData;
				bool flag2 = !inProcessPerPlayerDataSessionComponent._clientVisibleData.Data.First((InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData d) => (long)d.IdentityId == (long)x).Data.Contains((PerPlayerData)(object)perPlayerData);
				Expect($"research shared ({num} records aliased)", (num == HostIdentities.ResearchTypes.Length) & flag & flag2);
				text = "shared";
			}
			catch (Exception e)
			{
				text = $"research sharing not checkable here ({Innermost(e).GetType().Name}: {Innermost(e).Message})";
			}
			return (all, string.Join(", ", results) + "; " + text + "; " + current.Describe());
			void Expect(string what, bool ok)
			{
				all &= ok;
				results.Add(what + " " + (ok ? "ok" : "WRONG"));
			}
		});
	}

	private static Exception Innermost(Exception e)
	{
		while (e.InnerException != null)
		{
			e = e.InnerException;
		}
		return e;
	}

	public static (bool Ok, string Detail) NarratorSkip()
	{
		return WithFirstChance(() =>
		{
			HostIdentities current = HostIdentities.Current;
			if (current == null)
			{
				return (false, "HostIdentities not installed");
			}
			INetworkStoryNarrator networkStoryNarrator = (INetworkStoryNarrator)RuntimeHelpers.GetUninitializedObject(typeof(Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext.ServerSessionComponent));
			NetworkStoryMethodBuilder networkStoryMethodBuilder = NetworkStoryMethodBuilder.Create();
			networkStoryMethodBuilder.Skipped = true;
			long storiesSkipped = current.StoriesSkipped;
			Exception ex = null;
			Exception ex2 = null;
			try
			{
				networkStoryNarrator.MoveToClient(networkStoryMethodBuilder, new DummyStory(), new ClientRequest(new ClientId(2)));
			}
			catch (Exception ex3)
			{
				ex = ex3;
			}
			long storiesSkipped2 = current.StoriesSkipped;
			try
			{
				networkStoryNarrator.MoveToClient(networkStoryMethodBuilder, new DummyStory(), new ClientRequest(new ClientId(1)));
			}
			catch (Exception ex4)
			{
				ex2 = ex4;
			}
			return (ex == null && storiesSkipped2 == storiesSkipped + 1 && current.StoriesSkipped == storiesSkipped2, $"ClientId 2: prefix skipped it ({((ex == null) ? "no exception" : ex.GetType().Name)}, skipped {storiesSkipped} -> {storiesSkipped2}); ClientId 1: Keen's path ran ({((ex2 == null) ? "returned" : (ex2.GetType().Name + " on the stub component, as expected"))}), skipped stays {current.StoriesSkipped}");
		});
	}

	public static (bool Ok, string Detail) TeamPermissionRules()
	{
		return WithFirstChance(() =>
		{
			long num = PlayerIdentities.Derive("name:TeamJoiner");
			Entity entity = EntityAllocator.AllocateStub();
			Entity entity2 = EntityAllocator.AllocateStub();
			Entity entity3 = EntityAllocator.AllocateStub();
			Entity grid = EntityAllocator.AllocateStub();
			Dictionary<Entity, long> owners = new Dictionary<Entity, long>(ReferenceEqualityComparer.Instance)
			{
				[entity] = -1559595548L,
				[entity2] = num,
				[entity3] = 1L
			};
			bool enabled = TeamPermissions.Enabled;
			TeamPermissions.OwnerOverride = (Entity? e) => (e == null || !owners.TryGetValue(e, out var value)) ? 0 : value;
			try
			{
				IdentityId identityId = new IdentityId(num);
				TeamPermissions.Enabled = true;
				bool __result = false;
				bool __result2 = false;
				bool __result3 = false;
				bool __result4 = false;
				bool __result5 = false;
				bool flag = true;
				bool __result6 = false;
				TeamPermissions.IsOwnerPostfix(entity, identityId, ref __result);
				TeamPermissions.CanPlaceBlockPostfix(entity3, identityId, ref __result2);
				TeamPermissions.CanUseBlockActionPostfix(grid, identityId, ref __result3);
				TeamPermissions.CanLinkGridsPostfix(entity, entity2, ref __result4);
				TeamPermissions.CanMergeGridsPostfix(entity, entity, ref __result5);
				TeamPermissions.CanPlaceBlockPostfix(entity, identityId, ref __result6);
				TeamPermissions.Enabled = false;
				bool __result7 = false;
				TeamPermissions.IsOwnerPostfix(entity, identityId, ref __result7);
				return (((((__result && !__result2 && !__result3) & __result4) && !__result5) & flag & __result6) && !__result7, $"joiner on host's grid: owner {__result}, build {__result6}; on NPC grid: build {__result2}; unowned: use {__result3}; connect host+joiner grids {__result4}; merge a grid with itself {__result5}; strict (off): owner {__result7}");
			}
			finally
			{
				TeamPermissions.OwnerOverride = null;
				TeamPermissions.Enabled = enabled;
			}
		});
	}

	public static (bool Ok, string Detail) StrictGateLookups()
	{
		return WithFirstChance(() =>
		{
			PermissionGate permissionGate = new PermissionGate(buildIndex: false);
			permissionGate.BuildIndex(typeof(PermissionsSessionComponent).Assembly);
			Type type = typeof(SeatComponent).GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault((Type t) => t.Name.StartsWith("<SetPilotStory>", StringComparison.Ordinal));
			string text = ((type == null) ? "type not found" : permissionGate.StoryValidator(type));
			Type nestedType = typeof(SoundBlockSyncComponent).GetNestedType("RequestNewSoundSignal");
			string text2 = ((nestedType == null) ? "type not found" : permissionGate.SignalValidator(nestedType));
			Type nestedType2 = typeof(DoorServerComponent).GetNestedType("RequestDoorStateToggleSignal");
			string text3 = ((nestedType2 == null) ? "type not found" : permissionGate.SignalValidator(nestedType2));
			TeamPermissions.OwnerOverride = (Entity? e) => 1L;
			string text4 = permissionGate.Check("ValidateEntityOwnership", EntityAllocator.AllocateStub(), 12345678L, (PermissionsSessionComponent)RuntimeHelpers.GetUninitializedObject(typeof(PermissionsSessionComponent)));
			TeamPermissions.OwnerOverride = (Entity? e) => 12345678L;
			string text5 = permissionGate.Check("ValidateEntityOwnership", EntityAllocator.AllocateStub(), 12345678L, (PermissionsSessionComponent)RuntimeHelpers.GetUninitializedObject(typeof(PermissionsSessionComponent)));
			TeamPermissions.OwnerOverride = null;
			return (text == "ValidateEntityOwnership" && text2 == "ValidateEntityOwnership" && text3 == null && text4 == null && text5 == null, $"SeatComponent.SetPilotStory: {text ?? "none"}; SoundBlockSyncComponent.RequestNewSoundSignal (handled in SoundBlockServerComponent): {text2 ?? "none"}; door toggle: {text3 ?? "none (ValidateInteractedEntities is a no-op in 2.4)"}; NPC-owned target gated: {text4 != null}, own target gated: {text5 != null}; index {permissionGate.IndexState}");
		});
	}

	public static (bool Ok, string Detail) StorySourceAndGate()
	{
		return WithFirstChance(() =>
		{
			HarnessLog log = new HarnessLog();
			Entity clientE = EntityAllocator.AllocateStub();
			Entity hostE = EntityAllocator.AllocateStub();
			HarnessStories.Trace.Clear();
			HarnessStories.Tag = (Entity? e) =>
			{
				string result2 = ((e != clientE) ? ((e != hostE) ? "other" : "host-entity") : "client-entity");
				return result2;
			};
			ObjectSpace space = Space("client", new Dictionary<Entity, uint> { [clientE] = 40u });
			ObjectSpace space2 = Space("server", new Dictionary<Entity, uint> { [hostE] = 40u });
			ClientStories clientStories = new ClientStories(log);
			HostStories hostStories = new HostStories(log);
			FakeSink fakeSink = new FakeSink();
			PeerId peer = new PeerId(78uL);
			StringId stringId = StringId.Get("y51online_story_key");
			var (context, fakeClientNarrator, _) = StartClient<int>("RoundTrip", new Dictionary<string, object>
			{
				["target"] = clientE,
				["x"] = 1,
				["label"] = "p4",
				["key"] = stringId
			});
			hostStories.Run(peer, StoryMove.Decode(clientStories.Prepare(context, fakeClientNarrator.Moved, null, space).Encode()), fakeSink, space2, null, 2, 2);
			bool flag = HarnessStories.Trace.FirstOrDefault() == "server:host-entity:remote:2:y51online_story_key";
			fakeSink.ToClient.Clear();
			HarnessStories.Trace.Clear();
			hostStories.Gate = (ushort cid, object box, Session? _) => (cid != 3) ? null : "ValidateEntityOwnership: owned by player 1 (strict grids)";
			var (context2, fakeClientNarrator2, _) = StartClient<int>("RoundTrip", new Dictionary<string, object>
			{
				["target"] = clientE,
				["x"] = 2,
				["label"] = "p4",
				["key"] = stringId
			});
			hostStories.Run(peer, StoryMove.Decode(clientStories.Prepare(context2, fakeClientNarrator2.Moved, null, space).Encode()), fakeSink, space2, null, 3, 3);
			bool flag2 = HarnessStories.Trace.Count == 0 && fakeSink.ToClient.TryDequeue(out object result) && result is StoryFinish { Status: 2 } storyFinish && storyFinish.Text.StartsWith("not allowed", StringComparison.Ordinal);
			ClientStories clientStories2 = new ClientStories(log)
			{
				PerPlayer = true
			};
			Type type = typeof(CameraStatePerPlayerData).GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault((Type t) => t.Name.StartsWith("<StoreCameraData>", StringComparison.Ordinal));
			Type type2 = typeof(ControlPerPlayerData).GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault((Type t) => t.Name.StartsWith("<StoreControlledEntity>", StringComparison.Ordinal));
			StoryMove storyMove = ((type2 == null) ? null : clientStories2.Prepare(NetworkStoryMethodBuilder.Create(), Activator.CreateInstance(type2), null, space));
			object obj = ((type == null) ? null : clientStories2.Prepare(NetworkStoryMethodBuilder.Create(), Activator.CreateInstance(type), null, space));
			object obj2 = ((type == null) ? null : clientStories2.Prepare(NetworkStoryMethodBuilder.Create(), Activator.CreateInstance(type), null, space));
			bool flag3 = clientStories2.CameraCoalesced == 1 && obj2 == null;
			return (((flag & flag2) && storyMove != null) & flag3, $"story ran as ClientId 2: {flag}; strict gate refused before running: {flag2}; own identity: StoreControlledEntity sent {storyMove != null}, StoreCameraData first {((obj != null) ? "sent" : "not sent (cannot travel with a default camera value)")}, second within 100 ms coalesced {flag3}; {clientStories2.Describe()}");
		});
	}

	public static object AddClientPlayers(object sessionBuilder)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Expected Obj, but got Unknown
		PlayersSessionObjectBuilder val = new PlayersSessionObjectBuilder();
		((SessionBuilder)sessionBuilder).SessionComponents.WithComponent((ComponentType: typeof(ClientPlayersSessionComponent), ObjectBuilder: val));
		return val;
	}

	public static (bool Ok, string Detail) ClientIdentityCheck(object playersOb, long expected)
	{
		return WithFirstChance(() =>
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Expected Obj, but got Unknown
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0043: Unknown result type (might be due to invalid IL or missing references)
			//IL_0053: Unknown result type (might be due to invalid IL or missing references)
			//IL_0054: Unknown result type (might be due to invalid IL or missing references)
			//IL_0064: Unknown result type (might be due to invalid IL or missing references)
			//IL_0069: Unknown result type (might be due to invalid IL or missing references)
			//IL_0088: Unknown result type (might be due to invalid IL or missing references)
			PlayersSessionObjectBuilder val = (PlayersSessionObjectBuilder)playersOb;
			ClientPlayersSessionComponent clientPlayersSessionComponent = Uninit<ClientPlayersSessionComponent>();
			HarmonyLib.AccessTools.Method(typeof(PlayersSessionComponent), "Init", new[] { typeof(PlayersSessionObjectBuilder) }).Invoke(clientPlayersSessionComponent, new object[] { val });
			long num = clientPlayersSessionComponent.LocalPlayerIdentity;
			ClientWorld.RepairPlayers(val);
			PlayerIdentity val2 = new PlayerIdentity(1559595546L);
			bool flag = ((PlayersSessionComponent)clientPlayersSessionComponent).GetPlayerIdentity(InProcessPerPlayerDataSessionComponent.HotfixIdentity) == val2;
			PlayerIdentity? val3 = ((PlayersSessionComponent)clientPlayersSessionComponent).TryGetDefaultPlayer();
			PlayerIdentity val4 = val2;
			bool flag2 = val3.HasValue && val3.GetValueOrDefault() == val4;
			bool flag3 = ((PlayersSessionComponent)clientPlayersSessionComponent).IsPlayerIdentity(new IdentityId(expected)) && (long)((PlayersSessionComponent)clientPlayersSessionComponent).GetIdentityFromClient(((PlayersSessionComponent)clientPlayersSessionComponent).GetPlayerData(clientPlayersSessionComponent.LocalPlayer).ClientId) == (long)new IdentityId(expected);
			return ((num == expected) & flag & flag2 & flag3, $"LocalPlayerIdentity {num} (expected {expected}); after repair Hotfix -> host player {flag}; default player = host {flag2}; joiner's ClientId resolves to its identity {flag3}; {val.PlayerData.Count} players");
		});
	}

	public static (bool Ok, string Detail) ClientPerPlayerData()
	{
		return WithFirstChance(() =>
		{
			//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e3: Expected Obj, but got Unknown
			IdentityId hotfixIdentity = InProcessPerPlayerDataSessionComponent.HotfixIdentity;
			IdentityId identityId = new IdentityId(PlayerIdentities.Derive("name:ClientJoiner"));
			ToolbarPerPlayerData toolbarPerPlayerData = Toolbar(1);
			ToolbarPerPlayerData toolbarPerPlayerData2 = Toolbar(2);
			PerPlayerDataSessionComponentObjectBuilder objectBuilder = new PerPlayerDataSessionComponentObjectBuilder
			{
				PerPlayerData = 
				{
					{
						hotfixIdentity,
						(object)toolbarPerPlayerData
					},
					{
						identityId,
						(object)toolbarPerPlayerData2
					},
					{
						identityId,
						(object)new PlayerCharacterData
						{
							PlayerName = "ClientJoiner"
						}
					}
				}
			};
			object manuallyTranslatedData = ClientWorld.BuildPerPlayerData(new SessionComponentsSnapshot
			{
				Components = new SessionComponentsSnapshot.ComponentInfo[1]
				{
					new SessionComponentsSnapshot.ComponentInfo
					{
						Type = typeof(InProcessPerPlayerDataSessionComponent),
						ObjectBuilder = objectBuilder
					}
				}
			}, identityId, out string summary);
			InProcessPerPlayerDataClientSessionComponent inProcessPerPlayerDataClientSessionComponent = (InProcessPerPlayerDataClientSessionComponent)RuntimeHelpers.GetUninitializedObject(typeof(InProcessPerPlayerDataClientSessionComponent));
			inProcessPerPlayerDataClientSessionComponent.Init(new InProcessPerPlayerDataClientSessionComponentObjectBuilder
			{
				ManuallyTranslatedData = manuallyTranslatedData
			});
			bool flag = inProcessPerPlayerDataClientSessionComponent.GetPerPlayerData<ToolbarPerPlayerData>(identityId) == toolbarPerPlayerData2 && inProcessPerPlayerDataClientSessionComponent.GetPerPlayerData<PlayerCharacterData>(identityId).PlayerName == "ClientJoiner";
			bool flag2 = inProcessPerPlayerDataClientSessionComponent.GetPerPlayerData<ToolbarPerPlayerData>(hotfixIdentity) == toolbarPerPlayerData;
			bool flag3 = toolbarPerPlayerData2.ToolbarTiles.All((ObservableCollection<ToolbarTile> r) => r.Count >= 10);
			return (flag & flag2 & flag3, $"own toolbar + name {flag}, host's toolbar kept {flag2}, toolbar padded {flag3}; {summary}");
		});
	}
}
