using System;
using System.Collections.Immutable;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Serialization;
using Keen.VRage.Library.Serialization;

namespace Y51_SE2Online.SyncEngine;

internal sealed class NetIdProxyContext : CustomSerializationContext, IEntityProxySerializationContext
{
	private readonly Func<Entity, int>? _toId;

	private readonly Func<int, Entity>? _toEntity;

	public int Written;

	public int Read;

	public override ImmutableArray<Type> Keys => ImmutableArray.Create(GetType(), typeof(IEntityProxySerializationContext));

	public NetIdProxyContext(Func<Entity, int>? toId, Func<int, Entity>? toEntity)
	{
		_toId = toId;
		_toEntity = toEntity;
	}

	public Entity GetEntity(int entityId)
	{
		Read++;
		if (_toEntity == null)
		{
			return EntityAllocator.AllocateStub();
		}
		return _toEntity(entityId);
	}

	public int GetEntityId(Entity entity)
	{
		Written++;
		if (_toId == null)
		{
			return 0;
		}
		return _toId(entity);
	}
}
