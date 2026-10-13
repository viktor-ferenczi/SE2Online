using System;
using System.Collections.Generic;
using System.Reflection;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

internal static class SocialGuard
{
    internal static bool IsOwnershipSignal(string name) => name.Contains("OwnershipComponent+", StringComparison.Ordinal)
        || name.Contains("GridPermissionsComponent+", StringComparison.Ordinal);

    internal static string CheckObject(object value, long actor)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int remaining = 256;
        return Inspect(value, 0);
        string Inspect(object obj, int depth)
        {
            if (obj == null || depth > 4 || remaining-- <= 0 || !visited.Add(obj)) return null;
            if (obj is Entity entity) return WorldSocial.Access(entity, actor) == false ? "This grid is private or shared with another faction." : null;
            if (obj is Component component) return Inspect(component.Entity, depth + 1);
            Type type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum || obj is string || obj is Delegate || obj is Type) return null;
            if (depth > 0 && !(type.IsValueType || type.Namespace?.StartsWith("Keen.Game2", StringComparison.Ordinal) == true)) return null;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.Name.Contains("awaiter", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("builder", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("context", StringComparison.OrdinalIgnoreCase)) continue;
                string denied = Inspect(field.GetValue(obj), depth + 1); if (denied != null) return denied;
            }
            return null;
        }
    }
}
