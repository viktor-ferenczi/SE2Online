using System;
using System.Collections;
using System.Collections.Generic;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ObjectSpace
{
	public string Name;

	public Func<Entity, uint?> IdOf;

	public Func<uint, bool, Entity?> Resolve;

	public Func<uint, string>? Explain;

	public Session? Session;

	public Session? TranslatedSession;

	public readonly Dictionary<Entity, int> LocalIds = new Dictionary<Entity, int>(ReferenceEqualityComparer.Instance);

	public readonly Dictionary<int, Entity> LocalStubs = new Dictionary<int, Entity>();

	public readonly Dictionary<Entity, int> BundleLocalIds = new Dictionary<Entity, int>(ReferenceEqualityComparer.Instance);

	public Func<object, uint?>? ObservableId;

	public Func<IList, uint?>? ListId;

	public Func<uint, bool, object?>? ResolveObservable;

	public Func<uint, object?>? ResolveList;

	public Func<object, bool>? WasReleased;

	public readonly List<uint> Observed = new List<uint>();

	public bool OpaqueHandles;

	public int NextLocalId()
	{
		return -(LocalIds.Count + BundleLocalIds.Count + 2);
	}

	public void ClearLocalIds()
	{
		LocalIds.Clear();
		BundleLocalIds.Clear();
	}
}
