using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class CommandLayouts
{
	public const string PostInvokeSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostInvokeInternalSignal";

	public const string PostDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal";

	public const string RemoveDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+RemoveDataSignal";

	private static CommandLayout?[]? _byStub;

	private static Dictionary<string, CommandLayout>? _byName;

	private static readonly object Sync = new object();

	public static int CallbacksAnalysed;

	public static int CallbacksMissing;

	private static readonly HashSet<Type> ForwardableStructs = new HashSet<Type> { typeof(LocalizableText), typeof(Keen.Game2.Simulation.GameSystems.Explosions.AudioParameter?) };

	private static readonly MethodInfo SizeOfMethod = typeof(Unsafe).GetMethod("SizeOf");

	private static readonly MethodInfo IsRefMethod = typeof(RuntimeHelpers).GetMethod("IsReferenceOrContainsReferences");

	public static int Count => _byStub?.Length ?? 0;

	public static CommandLayout?[] All
	{
		get
		{
			Build();
			return _byStub;
		}
	}

	public static CommandLayout? ByStub(int stub)
	{
		CommandLayout[] all = All;
		if ((uint)stub >= (uint)all.Length)
		{
			return null;
		}
		return all[stub];
	}

	public static CommandLayout? ByName(string name)
	{
		Build();
		if (!_byName.TryGetValue(name, out CommandLayout value))
		{
			return null;
		}
		return value;
	}

	public static string? Check(SignalKeyDef def)
	{
		try
		{
			CommandLayout commandLayout = ByName(def.Name);
			if (commandLayout == null)
			{
				return "unknown signal in this game build";
			}
			if (!commandLayout.Forwardable)
			{
				return "not forwardable here: " + commandLayout.Why;
			}
			SignalKeyDef signalKeyDef = commandLayout.Def(def.Key);
			if (signalKeyDef.PayloadBytes != def.PayloadBytes || signalKeyDef.RefOffsets != def.RefOffsets)
			{
				return $"layout differs (here {signalKeyDef.PayloadBytes} B [{signalKeyDef.RefOffsets}], peer {def.PayloadBytes} B [{def.RefOffsets}])";
			}
			return null;
		}
		catch (Exception ex)
		{
			return "check failed: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static void Build()
	{
		if (_byStub != null)
		{
			return;
		}
		lock (Sync)
		{
			if (_byStub != null)
			{
				return;
			}
			ReplicationCommandBufferIndexer.CommandBufferCommand[] table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
			CommandLayout[] array = new CommandLayout[table.Length];
			Dictionary<string, CommandLayout> dictionary = new Dictionary<string, CommandLayout>(StringComparer.Ordinal);
			Dictionary<Type, Dictionary<int, int>> dictionary2;
			try
			{
				dictionary2 = ReaderCallbacks.Analyse();
			}
			catch (Exception)
			{
				dictionary2 = new Dictionary<Type, Dictionary<int, int>>();
			}
			for (int i = 0; i < table.Length; i++)
			{
				Type signalType = table[i].SignalType;
				if ((object)signalType != null && signalType.FullName != null)
				{
					CommandLayout commandLayout = For(i, signalType.FullName, table[i].WriterInfo);
					commandLayout.SignalType = signalType;
					if (dictionary2.TryGetValue(signalType, out var value))
					{
						commandLayout.Translated = value;
						CallbacksAnalysed++;
					}
					else if (commandLayout.Kind == CommandKind.Signal)
					{
						CallbacksMissing++;
					}
					array[i] = commandLayout;
					dictionary.TryAdd(commandLayout.Name, commandLayout);
				}
			}
			_byName = dictionary;
			_byStub = array;
		}
	}

	private static CommandLayout For(int stub, string name, MethodInfo? writer)
	{
		switch (name)
		{
		case "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal":
		{
			CommandLayout commandLayout2 = new CommandLayout();
			commandLayout2.Stub = stub;
			commandLayout2.Name = name;
			commandLayout2.Kind = CommandKind.PostData;
			commandLayout2.RefOffsets = new int[2] { 0, 4 };
			return commandLayout2;
		}
		case "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+RemoveDataSignal":
		{
			CommandLayout commandLayout2 = new CommandLayout();
			commandLayout2.Stub = stub;
			commandLayout2.Name = name;
			commandLayout2.Kind = CommandKind.RemoveData;
			commandLayout2.PayloadBytes = 12;
			commandLayout2.RefOffsets = new int[2] { 0, 4 };
			return commandLayout2;
		}
		case "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostInvokeInternalSignal":
		{
			CommandLayout commandLayout2 = new CommandLayout();
			commandLayout2.Stub = stub;
			commandLayout2.Name = name;
			commandLayout2.Kind = CommandKind.PostInvoke;
			commandLayout2.PayloadBytes = 8;
			commandLayout2.RefOffsets = new int[2] { 0, 4 };
			commandLayout2.Why = "closure";
			return commandLayout2;
		}
		default:
		{
			CommandLayout commandLayout = new CommandLayout
			{
				Stub = stub,
				Name = name,
				Kind = CommandKind.Signal
			};
			if (writer == null)
			{
				commandLayout.Why = "no writer";
				return commandLayout;
			}
			ParameterInfo[] parameters = writer.GetParameters();
			if (parameters.Length == 0)
			{
				commandLayout.Why = "writer has no entity parameter";
				return commandLayout;
			}
			if (name.StartsWith("Keen.VRage.Multiplayer.", StringComparison.Ordinal))
			{
				commandLayout.Why = "replication infrastructure (observables, pairing): not forwarded";
				commandLayout.PayloadBytes = -1;
			}
			List<Segment> list = new List<Segment>
			{
				new Segment(SegmentKind.Ref, 4, 0, parameters[0].ParameterType)
			};
			bool flag = false;
			int num = 4;
			List<int> list2 = new List<int>();
			for (int i = 1; i < parameters.Length; i++)
			{
				Type type = parameters[i].ParameterType;
				if (type.IsByRef)
				{
					type = type.GetElementType();
				}
				if (!type.IsValueType || IsGeneric(type, typeof(ImmutableArray<>)))
				{
					list.Add(new Segment(SegmentKind.Ref, 4, i, type));
					num += 4;
					continue;
				}
				if (IsGeneric(type, typeof(ReadOnlySpan<>)) || IsGeneric(type, typeof(Span<>)))
				{
					Type type2 = type.GetGenericArguments()[0];
					if (RuntimeHelpersIsReferenceOrContainsReferences(type2) || ProcessLocal(type2, 0) != null)
					{
						commandLayout.Why = $"span of {type2.Name} ({parameters[i].Name})";
						return commandLayout;
					}
					List<int> list3 = new List<int>();
					CollectStringIds(type2, 0, list3, 0);
					if (list3.Count > 0)
					{
						commandLayout.Why = $"span of {type2.Name} ({parameters[i].Name}) holds StringIds";
						return commandLayout;
					}
					list.Add(new Segment(SegmentKind.Span, 0, i, type));
					flag = true;
					continue;
				}
				if (RuntimeHelpersIsReferenceOrContainsReferences(type))
				{
					num += 4;
					list.Add(new Segment(SegmentKind.Ref, 4, i, type));
					if (!ForwardableStructs.Contains(type))
					{
						CommandLayout commandLayout2 = commandLayout;
						if (commandLayout2.Why == null)
						{
							commandLayout2.Why = $"managed struct argument {parameters[i].Name} ({type.Name})";
						}
					}
					continue;
				}
				string text = ProcessLocal(type, 0);
				if (text != null)
				{
					commandLayout.Why = "argument " + parameters[i].Name + " carries " + text;
					return commandLayout;
				}
				int num2 = SizeOf(type);
				List<int> list4 = new List<int>();
				CollectStringIds(type, 0, list4, 0);
				if (list4.Count > 0)
				{
					if (flag)
					{
						commandLayout.Why = "argument " + parameters[i].Name + " holds a StringId after a span";
						return commandLayout;
					}
					foreach (int item in list4)
					{
						list2.Add(num + item);
					}
				}
				num += num2;
				if (list[list.Count - 1].Kind == SegmentKind.Bytes)
				{
					int index = list.Count - 1;
					Segment value = list[list.Count - 1];
					value = value with { Size = list[list.Count - 1].Size + num2 };
					list[index] = value;
				}
				else
				{
					list.Add(new Segment(SegmentKind.Bytes, num2, i, type));
				}
			}
			commandLayout.Segments = list.ToArray();
			commandLayout.StringIdOffsets = list2.ToArray();
			if (!flag)
			{
				int num3 = 0;
				List<int> list5 = new List<int>();
				foreach (Segment item2 in list)
				{
					if (item2.Kind == SegmentKind.Ref)
					{
						list5.Add(num3);
					}
					num3 += ((item2.Kind == SegmentKind.Ref) ? 4 : item2.Size);
				}
				commandLayout.PayloadBytes = num3;
				commandLayout.RefOffsets = list5.ToArray();
			}
			commandLayout.Forwardable = commandLayout.Why == null;
			return commandLayout;
		}
		}
	}

	internal static void CollectStringIds(Type t, int baseOffset, List<int> found, int depth)
	{
		if (t == typeof(StringId))
		{
			found.Add(baseOffset);
		}
		else
		{
			if (!t.IsValueType || t.IsPrimitive || t.IsEnum || t.IsPointer || depth > 6)
			{
				return;
			}
			FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo fieldInfo in fields)
			{
				Type fieldType = fieldInfo.FieldType;
				if (fieldType.IsValueType && !fieldType.IsPrimitive && !fieldType.IsEnum)
				{
					CollectStringIds(fieldType, baseOffset + (int)fieldInfo.GetFieldOffset(), found, depth + 1);
				}
			}
		}
	}

	private static bool IsGeneric(Type t, Type def)
	{
		if (t.IsGenericType)
		{
			return t.GetGenericTypeDefinition() == def;
		}
		return false;
	}

	public static int SizeOf(Type t)
	{
		return (int)SizeOfMethod.MakeGenericMethod(t).Invoke(null, null);
	}

	private static bool RuntimeHelpersIsReferenceOrContainsReferences(Type t)
	{
		return (bool)IsRefMethod.MakeGenericMethod(t).Invoke(null, null);
	}

	private static string? ProcessLocal(Type t, int depth)
	{
		if (t.IsPointer || t == typeof(nint) || t == typeof(nuint))
		{
			return "a pointer";
		}
		string fullName = t.FullName;
		bool flag = ((fullName == "Keen.VRage.DCS.Accessors.DEntity" || fullName == "Keen.VRage.Multiplayer.EntityId") ? true : false);
		if (flag || t.Name == "EntityId")
		{
			return t.Name;
		}
		if (t.IsPrimitive || t.IsEnum || depth > 4)
		{
			return null;
		}
		FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		for (int i = 0; i < fields.Length; i++)
		{
			string text = ProcessLocal(fields[i].FieldType, depth + 1);
			if (text != null)
			{
				return text;
			}
		}
		return null;
	}

	public static int WriterAllocationSize(MethodInfo trampoline)
	{
		string text;
		if (!trampoline.Name.EndsWith("_Trampoline", StringComparison.Ordinal))
		{
			text = trampoline.Name;
		}
		else
		{
			string name = trampoline.Name;
			int length = "_Trampoline".Length;
			text = name.Substring(0, name.Length - length);
		}
		string name2 = text;
		MethodInfo methodInfo = trampoline.DeclaringType?.GetMethod(name2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		byte[] array = methodInfo?.GetMethodBody()?.GetILAsByteArray();
		if (methodInfo == null || array == null || array.Length < 2)
		{
			return -1;
		}
		Stack<long> stack = new Stack<long>();
		int num = 0;
		while (num < array.Length)
		{
			byte b = array[num++];
			byte b2 = b;
			switch (b2)
			{
			case 32:
				if (b2 != 32)
				{
					break;
				}
				stack.Push(BitConverter.ToInt32(array, num));
				num += 4;
				continue;
			case 22:
			case 23:
			case 24:
			case 25:
			case 26:
			case 27:
			case 28:
			case 29:
			case 30:
				stack.Push(b - 22);
				continue;
			case 21:
				stack.Push(-1L);
				continue;
			case 31:
				stack.Push((sbyte)array[num++]);
				continue;
			case 88:
			case 90:
			{
				if (stack.Count < 2)
				{
					return -1;
				}
				long num2 = stack.Pop();
				long num3 = stack.Pop();
				stack.Push((b == 88) ? (num3 + num2) : (num3 * num2));
				continue;
			}
			case 254:
				if (num < array.Length && array[num] == 28)
				{
					int metadataToken = BitConverter.ToInt32(array, num + 1);
					num += 5;
					Type t;
					try
					{
						t = methodInfo.Module.ResolveType(metadataToken, methodInfo.DeclaringType?.GetGenericArguments(), methodInfo.GetGenericArguments());
					}
					catch (Exception)
					{
						return -1;
					}
					stack.Push(SizeOf(t));
					continue;
				}
				break;
			case 10:
			case 11:
			case 12:
			case 13:
			case 19:
				if (stack.Count != 1)
				{
					return -1;
				}
				return (int)stack.Pop();
			}
			return -1;
		}
		return -1;
	}
}
