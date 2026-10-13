using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.DCS.Internal;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.DCS.Scenes;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class EngineCodec
{
	public readonly struct KeyedListChildren(KeyedList<Guid, EntityObjectBuilder>? list)
	{
		private readonly KeyedList<Guid, EntityObjectBuilder>? _list = list;

		public int Count => _list?.Count ?? 0;

		public EntityObjectBuilder this[int i] => _list[i];
	}

	private static readonly FieldInfo? PendingCloseField = typeof(Session).GetField("PendingEntitiesForClose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static readonly FieldInfo? CloseField = typeof(Session).GetField("EntitiesForClose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static readonly FieldInfo?[] CloseFields = new FieldInfo[2] { PendingCloseField, CloseField };

	public static bool CloseQueuesReachable
	{
		get
		{
			if (PendingCloseField != null)
			{
				return CloseField != null;
			}
			return false;
		}
	}

	private static SerializationContext Context(Stream s, string name, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		if (blobs != null)
		{
			return new SerializationContext(s, name, proxies, new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance), new BlobSetIndexer(blobs));
		}
		return new SerializationContext(s, name, proxies, new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance));
	}

	private static T WithBump<T>(Func<T> f)
	{
		BumpAllocator instance = BumpAllocator.Instance;
		bool flag = !instance.Initialized;
		if (flag)
		{
			instance.Initialize();
		}
		try
		{
			return f();
		}
		finally
		{
			if (flag)
			{
				instance.Dispose();
			}
		}
	}

	public static byte[] SerializeBundle(EntityBundle bundle, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using MemoryStream memoryStream = new MemoryStream();
			using (SerializationContext context = Context(memoryStream, "y51online-bundle.vrb", proxies, blobs))
			{
				SerializationHelper.Serialize(context, in bundle, SerializerFormat.Binary);
			}
			return memoryStream.ToArray();
		});
	}

	public static EntityBundle DeserializeBundle(byte[] bytes, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using MemoryStream s = new MemoryStream(bytes);
			using SerializationContext context = Context(s, "y51online-bundle.vrb", proxies, blobs);
			return SerializationHelper.Deserialize<EntityBundle>(context, SerializerFormat.Binary) ?? throw new InvalidDataException("bundle deserialized to null");
		});
	}

	public static byte[] SerializeSnapshot(SessionComponentsSnapshot snapshot, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using MemoryStream memoryStream = new MemoryStream();
			using (SerializationContext context = Context(memoryStream, "y51online-snapshot.vrb", proxies, blobs))
			{
				SerializationHelper.Serialize(context, in snapshot, SerializerFormat.Binary);
			}
			return memoryStream.ToArray();
		});
	}

	public static SessionComponentsSnapshot DeserializeSnapshot(byte[] bytes, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using MemoryStream s = new MemoryStream(bytes);
			using SerializationContext context = Context(s, "y51online-snapshot.vrb", proxies, blobs);
			return SerializationHelper.Deserialize<SessionComponentsSnapshot>(context, SerializerFormat.Binary);
		});
	}

	public static byte[] SerializeObject(object o, Type t, NetIdProxyContext proxies, MethodInfo serializeGeneric)
	{
		return WithBump(() =>
		{
			using MemoryStream memoryStream = new MemoryStream();
			using (SerializationContext serializationContext = Context(memoryStream, "y51online-object.vrb", proxies))
			{
				serializeGeneric.MakeGenericMethod(t).Invoke(null, new object[3]
				{
					serializationContext,
					o,
					SerializerFormat.Binary
				});
			}
			return memoryStream.ToArray();
		});
	}

	public static object? DeserializeObject(byte[] b, Type t, NetIdProxyContext proxies, MethodInfo deserializeGeneric)
	{
		return WithBump(() =>
		{
			using MemoryStream s = new MemoryStream(b);
			using SerializationContext serializationContext = Context(s, "y51online-object.vrb", proxies);
			return deserializeGeneric.MakeGenericMethod(t).Invoke(null, new object[2]
			{
				serializationContext,
				SerializerFormat.Binary
			});
		});
	}

	public static Guid Id(Blob b)
	{
		return Guid.Parse(b.ToString());
	}

	public static List<Guid> Ids(IEnumerable<Blob> blobs)
	{
		List<Guid> list = new List<Guid>();
		foreach (Blob blob in blobs)
		{
			list.Add(Id(blob));
		}
		return list;
	}

	public static HashSet<Blob> SnapshotBlobs(SessionComponentsSnapshot snapshot, Func<Type, bool> clientUses, out int serverOnly, out List<string> owners)
	{
		HashSet<Blob> hashSet = new HashSet<Blob>();
		serverOnly = 0;
		owners = new List<string>();
		NetIdProxyContext proxies = new NetIdProxyContext((Entity _) => 0, null);
		SessionComponentsSnapshot.ComponentInfo[] array = snapshot.Components ?? Array.Empty<SessionComponentsSnapshot.ComponentInfo>();
		for (int num = 0; num < array.Length; num++)
		{
			SessionComponentsSnapshot.ComponentInfo componentInfo = array[num];
			if (componentInfo.ObjectBuilder == null)
			{
				continue;
			}
			HashSet<Blob> hashSet2 = new HashSet<Blob>();
			SessionComponentsSnapshot sessionComponentsSnapshot = new SessionComponentsSnapshot();
			sessionComponentsSnapshot.Components = new SessionComponentsSnapshot.ComponentInfo[1] { componentInfo };
			SerializeSnapshot(sessionComponentsSnapshot, proxies, hashSet2);
			if (hashSet2.Count != 0)
			{
				if (clientUses(componentInfo.ObjectBuilder.GetType()))
				{
					hashSet.UnionWith(hashSet2);
					owners.Add($"{componentInfo.Type?.Name ?? "?"} {hashSet2.Count}");
				}
				else
				{
					serverOnly += hashSet2.Count;
					owners.Add($"{componentInfo.Type?.Name ?? "?"} {hashSet2.Count} (server-only, not sent)");
				}
			}
		}
		return hashSet;
	}

	public static HashSet<Type> SessionObjectBuilderTypes(Session session)
	{
		HashSet<Type> hashSet = new HashSet<Type>();
		foreach (Component component in session.SessionComponents.Components)
		{
			Type objectBuilder = RuntimeComponentInfo.For(component.GetType()).ObjectBuilder;
			if (objectBuilder != null)
			{
				hashSet.Add(objectBuilder);
			}
		}
		return hashSet;
	}

	public static Dictionary<Guid, byte[]?> LoadBlobs(IReadOnlyDataOffloading offloading, IEnumerable<Blob> blobs, TimeSpan timeout, out int failed, out string? firstError)
	{
		Dictionary<Guid, byte[]> dictionary = new Dictionary<Guid, byte[]>();
		failed = 0;
		firstError = null;
		foreach (Blob blob in blobs)
		{
			Guid guid = Id(blob);
			if (dictionary.ContainsKey(guid))
			{
				continue;
			}
			byte[] result = null;
			try
			{
				Task<byte[]> task = offloading.LoadData(blob);
				if (task.IsCompleted)
				{
					result = task.GetAwaiter().GetResult();
				}
				else if (!task.WaitBlocking(timeout, out result))
				{
					throw new TimeoutException($"LoadData did not finish in {timeout.TotalSeconds:0} s");
				}
			}
			catch (Exception ex)
			{
				failed++;
				if (firstError == null)
				{
					firstError = $"{guid}: {ex.GetType().Name}: {ex.Message}";
				}
				result = null;
			}
			dictionary[guid] = result;
		}
		return dictionary;
	}

	public static void Register(IStreamedOutBlobs target, Guid id, byte[] data)
	{
		target.AddKnownBlob(Blob.FromGuid(id), () => new Task<Stream>(new MemoryStream(data, writable: false)));
	}

	public static bool IsDead(Entity? e)
	{
		try
		{
			if (e == null || EntityAllocator.IsStub(e))
			{
				return true;
			}
			Scene scene = e.Data.Scene;
			return scene == null || !scene.IsEntityAlive(e.DEntity);
		}
		catch (Exception)
		{
			return true;
		}
	}

	public static bool IsClosing(Session session, Entity e)
	{
		HashSet<Entity> obj = PendingCloseField?.GetValue(session) as HashSet<Entity>;
		if (obj == null || !obj.Contains(e))
		{
			return (CloseField?.GetValue(session) as HashSet<Entity>)?.Contains(e) ?? false;
		}
		return true;
	}

	public static int PurgeDeadFromCloseQueues(Session session, out string? sample)
	{
		sample = null;
		int num = 0;
		FieldInfo[] closeFields = CloseFields;
		for (int i = 0; i < closeFields.Length; i++)
		{
			if (!(closeFields[i]?.GetValue(session) is HashSet<Entity> { Count: not 0 } hashSet))
			{
				continue;
			}
			List<Entity> list = null;
			foreach (Entity item in hashSet)
			{
				if (IsDead(item))
				{
					(list ?? (list = new List<Entity>())).Add(item);
				}
			}
			if (list == null)
			{
				continue;
			}
			foreach (Entity item2 in list)
			{
				hashSet.Remove(item2);
				num++;
				if (sample == null)
				{
					sample = Describe(item2);
				}
			}
		}
		return num;
	}

	public static int RemoveFromCloseQueues(Session session, IEnumerable<Entity> entities)
	{
		HashSet<Entity> hashSet = PendingCloseField?.GetValue(session) as HashSet<Entity>;
		HashSet<Entity> hashSet2 = CloseField?.GetValue(session) as HashSet<Entity>;
		if ((hashSet == null || hashSet.Count == 0) && (hashSet2 == null || hashSet2.Count == 0))
		{
			return 0;
		}
		int num = 0;
		foreach (Entity entity in entities)
		{
			if (hashSet != null && hashSet.Remove(entity))
			{
				num++;
			}
			if (hashSet2 != null && hashSet2.Remove(entity))
			{
				num++;
			}
		}
		return num;
	}

	public static string Describe(Entity e)
	{
		try
		{
			if (EntityAllocator.IsStub(e))
			{
				return "a never-built entity stub";
			}
			return $"a destroyed entity (composition {((e.CompositionData != null) ? "set" : "none")}, DEntity {e.DEntity})";
		}
		catch (Exception ex)
		{
			return "an unreadable entity (" + ex.GetType().Name + ")";
		}
	}

	public static KeyedListChildren Children(EntityObjectBuilder eob)
	{
		EntityCompositeDefinition definition = eob.Definition;
		if (definition == null || !definition.TryGetIndexOf<HierarchyComponent>(out var index) || index < 0 || index >= eob.ObjectBuilders.Count)
		{
			return default;
		}
		return new KeyedListChildren((eob.ObjectBuilders[index] as HierarchyComponentObjectBuilder)?.Children);
	}

	public static List<EntityLocator> Locate(EntityBundle bundle, IReadOnlyDictionary<Entity, EntityObjectBuilder> eobs, Func<Entity, uint> netId, out int unlocated)
	{
		Dictionary<EntityObjectBuilder, Entity> eobToEntity = new Dictionary<EntityObjectBuilder, Entity>(ReferenceEqualityComparer.Instance);
		foreach (KeyValuePair<Entity, EntityObjectBuilder> eob in eobs)
		{
			eobToEntity[eob.Value] = eob.Key;
		}
		List<EntityLocator> result = new List<EntityLocator>(eobs.Count);
		HashSet<Entity> seen = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		List<int> path = new List<int>();
		for (int i = 0; i < bundle.Builders.Count; i++)
		{
			Walk(bundle.Builders[i], i, 0);
		}
		for (int j = 0; j < bundle.Entities.Count; j++)
		{
			Entity entity = bundle.Entities[j];
			if (entity != null && eobs.ContainsKey(entity) && seen.Add(entity))
			{
				result.Add(new EntityLocator(netId(entity), j, Array.Empty<int>()));
			}
		}
		unlocated = eobs.Count - seen.Count;
		return result;
		void Walk(EntityObjectBuilder? eob, int root, int depth)
		{
			if (eob != null && depth <= 64)
			{
				if (eobToEntity.TryGetValue(eob, out Entity value) && seen.Add(value))
				{
					result.Add(new EntityLocator(netId(value), root, path.ToArray()));
				}
				KeyedListChildren keyedListChildren = Children(eob);
				for (int k = 0; k < keyedListChildren.Count; k++)
				{
					path.Add(k);
					Walk(keyedListChildren[k], root, depth + 1);
					path.RemoveAt(path.Count - 1);
				}
			}
		}
	}

	public static EntityObjectBuilder? Resolve(EntityBundle bundle, EntityLocator loc)
	{
		if (loc.Root < 0 || loc.Root >= bundle.Builders.Count)
		{
			return null;
		}
		EntityObjectBuilder entityObjectBuilder = bundle.Builders[loc.Root];
		int[] path = loc.Path;
		foreach (int num in path)
		{
			KeyedListChildren keyedListChildren = Children(entityObjectBuilder);
			if (num < 0 || num >= keyedListChildren.Count)
			{
				return null;
			}
			entityObjectBuilder = keyedListChildren[num];
			if (entityObjectBuilder == null)
			{
				return null;
			}
		}
		return entityObjectBuilder;
	}

	public static (string Name, int Size) DescribeType(int typeId)
	{
		RuntimeDataInfo.Info info = RuntimeDataInfo.Of(typeId);
		Type runtimeType = info.RuntimeType;
		return (Name: (runtimeType == null) ? $"typeId#{typeId}" : (runtimeType.FullName + ", " + runtimeType.Assembly.GetName().Name), Size: info.Size);
	}

	public static (int TypeId, int Size)? ResolveType(string name)
	{
		Type type = Type.GetType(name, throwOnError: false);
		if (type == null || !type.IsValueType)
		{
			return null;
		}
		RuntimeDataInfo.Info info = RuntimeDataInfo.Of(type);
		return (info.TypeId, info.Size);
	}

	public static List<DataBlob> Blobs(ref DEntityBuilder builder, TypeKeyTable keys)
	{
		List<DataBlob> list = new List<DataBlob>();
		foreach (KeyValuePair<int, Memory<byte>?> datum in builder.Data)
		{
			Memory<byte>? value = datum.Value;
			if (value.HasValue)
			{
				Memory<byte> valueOrDefault = value.GetValueOrDefault();
				list.Add(new DataBlob(keys.GetOrAdd(datum.Key, DescribeType).Key, valueOrDefault.ToArray()));
			}
		}
		return list;
	}
}
