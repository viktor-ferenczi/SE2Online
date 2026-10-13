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

	private static readonly ConcurrentDictionary<IMemberAccessor, bool> NoReplicate = new(ReferenceEqualityComparer.Instance);

	private static readonly ConcurrentDictionary<IMemberAccessor, bool> FromClients = new(ReferenceEqualityComparer.Instance);

	public static bool IsNoReplicate(AccessibleTypeInfo.MemberInfo m) =>
		NoReplicate.GetOrAdd(m.Accessor, (IMemberAccessor a) => a.Member.HasAttribute<NoReplicateAttribute>());

	public static bool IsSyncFromClients(AccessibleTypeInfo.MemberInfo m) =>
		FromClients.GetOrAdd(m.Accessor, (IMemberAccessor a) => a.Member.HasAttribute<SyncFromClients>());

	public static bool TryReplicating(AccessibleTypeInfo info, string name, out AccessibleTypeInfo.MemberInfo member) =>
		info.Members.TryGetValue(name, out member) && member.CanWrite && !IsNoReplicate(member);

	public static bool IsObservedList(object? value) => value is IList list && list is INotifyCollectionChanged;

	public static Type ElementType(Type listType)
	{
		Type? current = listType;
		while (current != null && current != typeof(object))
		{
			if (current.IsGenericType && current.GetGenericArguments().Length == 1)
			{
				return current.GetGenericArguments()[0];
			}
			current = current.BaseType;
		}
		foreach (Type iface in listType.GetInterfaces())
		{
			if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
			{
				return iface.GetGenericArguments()[0];
			}
		}
		return typeof(object);
	}
}
