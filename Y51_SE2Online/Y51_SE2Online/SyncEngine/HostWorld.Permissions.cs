using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.SecureIds;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    // ponytail: one focused block per player, kept until their next control panel refresh; fine for a few hundred players.
    private readonly ConcurrentDictionary<long, (Entity Block, Guid Secure)> _focusBlocks = new();

    /// <summary>
    /// A request naming its target by a control panel EntityId gets a NetId instead: the block for block access
    /// changes, otherwise its grid. A Refresh also remembers the block so the snapshot can carry its access lists.
    /// </summary>
    private SocialRequest WithSecureTarget(Session session, long actor, SocialRequest request)
    {
        if (request.Secure == Guid.Empty) return request;
        EntityId id = default;
        Unsafe.AsRef(in id._entityGuid) = request.Secure;
        var entity = session.SessionComponents.Components.OfType<ISecureEntityIds>().FirstOrDefault()?.TryGetEntity(id, null);
        if (entity != null && EngineCodec.IsDead(entity)) entity = null;
        var block = entity == null ? null : WorldSocial.BlockOf(entity);
        if (block?.TryGet<BlockPermissionsComponent>() == null) block = null;
        if (request.Action == SocialAction.Refresh)
        {
            if (block != null) _focusBlocks[actor] = (block, request.Secure);
            else _focusBlocks.TryRemove(actor, out _);
        }
        var target = request.Action is SocialAction.BlockAllow or SocialAction.BlockDeny ? block : entity == null ? null : WorldSocial.GridOf(entity);
        return request with { Grid = target == null ? 0 : _ids.GetOrAssign(target), Secure = Guid.Empty };
    }

    private SocialBlock FocusBlockOf(long actor)
    {
        if (!_focusBlocks.TryGetValue(actor, out var focus)) return null;
        var block = focus.Block;
        if (EngineCodec.IsDead(block)) { _focusBlocks.TryRemove(actor, out _); return null; }
        var acl = WorldSocial.AclOf(block);
        string name = block.TryGet<CubeBlockComponent>()?.Definition?.DebugNameShort ?? "Block";
        return new SocialBlock(_ids.GetOrAssign(block), SocialState.Clean(name, 100), TeamPermissions.OwnerOf(block), acl.Allow, acl.Deny, focus.Secure);
    }
}
