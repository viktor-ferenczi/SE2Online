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
			_task = Task.Run(() =>
			{
				Run(log);
			});
		}
	}

	public static void EnsureDone(IMpLog log)
	{
		Task task = _task;
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
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			BeforeAsymmetric = TypeId<AsymmetricDestructionData>.Value;
			BeforeVelocityOverride = TypeId<BreakableBodyMinVelocityOverride>.Value;
			RuntimeDataInfo.Of(typeof(DEntity));
			RuntimeDataInfo.Of(typeof(ConcurrentInit));
			RuntimeDataInfo.Of(typeof(StagingTag));
			HashSet<Type> hashSet = new HashSet<Type>();
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly assembly in assemblies)
			{
				string text = assembly.GetName().Name ?? "";
				if (!text.StartsWith("VRage", StringComparison.Ordinal) && !text.StartsWith("Game2", StringComparison.Ordinal) && !text.StartsWith("SpaceEngineers2", StringComparison.Ordinal))
				{
					continue;
				}
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					types = ex.Types;
				}
				Type[] array = types;
				foreach (Type type in array)
				{
					if (!(type == null))
					{
						Collect(type, hashSet);
						MemberInfo[] members = type.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
						for (int k = 0; k < members.Length; k++)
						{
							Collect(members[k], hashSet);
						}
					}
				}
			}
			MethodInfo method = typeof(RuntimeHelpers).GetMethod("IsReferenceOrContainsReferences");
			foreach (Type item in hashSet)
			{
				try
				{
					if (!item.IsValueType || item.IsEnum || item.IsPrimitive || item.ContainsGenericParameters || item.IsByRefLike || item == typeof(DEntity))
					{
						Skipped++;
						continue;
					}
					if ((bool)method.MakeGenericMethod(item).Invoke(null, null))
					{
						Skipped++;
						continue;
					}
					object obj = typeof(TypeId<>).MakeGenericType(item).GetField("InternalValue", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
					if (obj is int && (int)obj != 0)
					{
						AlreadyKnown++;
						continue;
					}
					RuntimeDataInfo.Of(item);
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
		catch (Exception ex3)
		{
			State = $"scan failed ({ex3.GetType().Name}: {ex3.Message})";
			log.Warn($"client: registering the game's data types failed ({ex3.GetType().Name}: {ex3.Message}); planet worlds may be unstable");
		}
	}

	private static void Collect(MemberInfo m, HashSet<Type> found)
	{
		IList<CustomAttributeData> customAttributesData;
		try
		{
			customAttributesData = m.GetCustomAttributesData();
		}
		catch (Exception)
		{
			return;
		}
		foreach (CustomAttributeData item in customAttributesData)
		{
			try
			{
				foreach (CustomAttributeTypedArgument constructorArgument in item.ConstructorArguments)
				{
					Add(constructorArgument, found);
				}
				foreach (CustomAttributeNamedArgument namedArgument in item.NamedArguments)
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
		if (arg.Value is Type item)
		{
			found.Add(item);
		}
		else
		{
			if (!(arg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> readOnlyCollection))
			{
				return;
			}
			foreach (CustomAttributeTypedArgument item3 in readOnlyCollection)
			{
				if (item3.Value is Type item2)
				{
					found.Add(item2);
				}
			}
		}
	}
}
