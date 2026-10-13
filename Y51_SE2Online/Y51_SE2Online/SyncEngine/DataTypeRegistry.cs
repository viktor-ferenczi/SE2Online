using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.CoreData;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Physics.BreakHandler;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal static class DataTypeRegistry
{
	private static Task? _task;

	public static int Registered;

	public static int AlreadyKnown;

	public static int Skipped;

	public static int Failed;

	public static int BeforeAsymmetric = -1;

	public static int BeforeVelocityOverride = -1;

	public static string State = "not run";

	public static void Start(IMpLog log)
	{
		if (_task == null)
		{
			_task = Task.Run(() => Run(log));
		}
	}

	public static void EnsureDone(IMpLog log)
	{
		Task? task = _task;
		if (task == null)
		{
			Run(log);
		}
		else if (!task.IsCompleted && !task.Wait(TimeSpan.FromSeconds(20L)))
		{
			log.WarnOnce("datatypes-wait", "client: the DCS data type scan is still running after 20 s; building anyway");
		}
	}

	internal static void Run(IMpLog log)
	{
		var stopwatch = Stopwatch.StartNew();
		try
		{
			BeforeAsymmetric = TypeId<AsymmetricDestructionData>.Value;
			BeforeVelocityOverride = TypeId<BreakableBodyMinVelocityOverride>.Value;
			RuntimeDataInfo.Of(typeof(DEntity));
			RuntimeDataInfo.Of(typeof(ConcurrentInit));
			RuntimeDataInfo.Of(typeof(StagingTag));
			var found = new HashSet<Type>();
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string name = assembly.GetName().Name ?? "";
				if (!name.StartsWith("VRage", StringComparison.Ordinal) && !name.StartsWith("Game2", StringComparison.Ordinal) && !name.StartsWith("SpaceEngineers2", StringComparison.Ordinal))
				{
					continue;
				}
				Type?[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					types = ex.Types;
				}
				foreach (Type? type in types)
				{
					if (type == null)
					{
						continue;
					}
					Collect(type, found);
					foreach (MemberInfo member in type.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
					{
						Collect(member, found);
					}
				}
			}
			MethodInfo isReferenceOrContainsReferences = typeof(RuntimeHelpers).GetMethod("IsReferenceOrContainsReferences");
			foreach (Type type in found)
			{
				try
				{
					if (!type.IsValueType || type.IsEnum || type.IsPrimitive || type.ContainsGenericParameters || type.IsByRefLike || type == typeof(DEntity))
					{
						Skipped++;
						continue;
					}
					if ((bool)isReferenceOrContainsReferences.MakeGenericMethod(type).Invoke(null, null))
					{
						Skipped++;
						continue;
					}
					object? typeId = typeof(TypeId<>).MakeGenericType(type).GetField("InternalValue", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
					if (typeId is int && (int)typeId != 0)
					{
						AlreadyKnown++;
						continue;
					}
					RuntimeDataInfo.Of(type);
					Registered++;
				}
				catch (Exception)
				{
					Failed++;
				}
			}
			State = $"{Registered} data types registered up front ({AlreadyKnown} already known, {Skipped} not data, {Failed} failed) in {stopwatch.ElapsedMilliseconds} ms";
			log.Info($"client: {State}; flora sector data: AsymmetricDestructionData type id {BeforeAsymmetric} -> {TypeId<AsymmetricDestructionData>.Value}, BreakableBodyMinVelocityOverride {BeforeVelocityOverride} -> {TypeId<BreakableBodyMinVelocityOverride>.Value} (0 = unregistered: written over the entity's own id)");
		}
		catch (Exception ex)
		{
			State = $"scan failed ({ex.GetType().Name}: {ex.Message})";
			log.Warn($"client: registering the game's data types failed ({ex.GetType().Name}: {ex.Message}); planet worlds may be unstable");
		}
	}

	private static void Collect(MemberInfo m, HashSet<Type> found)
	{
		IList<CustomAttributeData> attributes;
		try
		{
			attributes = m.GetCustomAttributesData();
		}
		catch (Exception)
		{
			return;
		}
		foreach (CustomAttributeData attribute in attributes)
		{
			try
			{
				foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
				{
					Add(argument, found);
				}
				foreach (CustomAttributeNamedArgument namedArgument in attribute.NamedArguments)
				{
					Add(namedArgument.TypedValue, found);
				}
			}
			catch (Exception)
			{
			}
		}
	}

	private static void Add(CustomAttributeTypedArgument arg, HashSet<Type> found)
	{
		if (arg.Value is Type type)
		{
			found.Add(type);
			return;
		}
		if (arg.Value is not IReadOnlyCollection<CustomAttributeTypedArgument> elements)
		{
			return;
		}
		foreach (CustomAttributeTypedArgument element in elements)
		{
			if (element.Value is Type elementType)
			{
				found.Add(elementType);
			}
		}
	}
}
