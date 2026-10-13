using System;
using System.Linq;
using System.Reflection;
using Keen.VRage.DCS.Components;
using Keen.VRage.Physics.Data;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Whether SE2's ragdoll drives an entity's transform (the data type is internal to VRage.Physics).</summary>
internal static class RagdollGuard
{
    private static readonly Type Tag = typeof(RigidBodyData).Assembly.GetType("Keen.VRage.Physics.Data.RagdollDrivesWorldTransform");
    private static MethodInfo _has;

    public static bool DrivesTransform(Entity entity)
    {
        if (Tag == null) return false;
        object data = entity.Data;
        _has ??= data.GetType().GetMethods().First(m => m.Name == "Has" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0).MakeGenericMethod(Tag);
        return (bool)_has.Invoke(data, null);
    }
}
