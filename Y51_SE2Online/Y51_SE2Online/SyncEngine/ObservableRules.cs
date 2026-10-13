using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Multiplayer.Annotations;

namespace Y51_SE2Online.SyncEngine;

internal static class ObservableRules
{
	public const string ReleaseObjectSignal = "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedObjectSignal";

	public const string ReleaseCollectionSignal = "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedCollectionSignal";

	public const string MemberChangedSignal = "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReplicateMemberChangedSignal";

	public const string Prefix = "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+";

	private static readonly ConcurrentDictionary<IMemberAccessor, bool> NoReplicate = new ConcurrentDictionary<IMemberAccessor, bool>(ReferenceEqualityComparer.Instance);

	private static readonly ConcurrentDictionary<IMemberAccessor, bool> FromClients = new ConcurrentDictionary<IMemberAccessor, bool>(ReferenceEqualityComparer.Instance);

	public static bool IsNoReplicate(AccessibleTypeInfo.MemberInfo m)
	{
		return NoReplicate.GetOrAdd(m.Accessor, (IMemberAccessor a) => a.Member.HasAttribute<NoReplicateAttribute>());
	}

	public static bool IsSyncFromClients(AccessibleTypeInfo.MemberInfo m)
	{
		return FromClients.GetOrAdd(m.Accessor, (IMemberAccessor a) => a.Member.HasAttribute<SyncFromClients>());
	}

	public static bool TryReplicating(AccessibleTypeInfo info, string name, out AccessibleTypeInfo.MemberInfo member)
	{
		if (info.Members.TryGetValue(name, out member) && member.CanWrite)
		{
			return !IsNoReplicate(member);
		}
		return false;
	}

	public static bool IsObservedList(object? value)
	{
		if (value is IList list)
		{
			return list is INotifyCollectionChanged;
		}
		return false;
	}

	public static Type ElementType(Type listType)
	{
		Type type = listType;
		while (type != null && type != typeof(object))
		{
			if (type.IsGenericType && type.GetGenericArguments().Length == 1)
			{
				return type.GetGenericArguments()[0];
			}
			type = type.BaseType;
		}
		Type[] interfaces = listType.GetInterfaces();
		foreach (Type type2 in interfaces)
		{
			if (type2.IsGenericType && type2.GetGenericTypeDefinition() == typeof(IList<>))
			{
				return type2.GetGenericArguments()[0];
			}
		}
		return typeof(object);
	}
}
