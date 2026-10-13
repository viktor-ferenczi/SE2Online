using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Y51_SE2Online.SyncEngine;

internal static class ReaderCallbacks
{
	private static Dictionary<short, OpCode>? _ops;

	public static Dictionary<Type, Dictionary<int, int>> Analyse()
	{
		var result = new Dictionary<Type, Dictionary<int, int>>();
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			string? name = assembly.GetName().Name;
			if (name == null || !(name.StartsWith("Game2", StringComparison.Ordinal) || name.StartsWith("VRage", StringComparison.Ordinal) || name == "SpaceEngineers2"))
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
				types = ex.Types.Where((Type t) => t != null).ToArray();
			}
			foreach (Type type in types)
			{
				if (type.Name != "InProcessReplicationReaderCallbacks")
				{
					continue;
				}
				foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					CustomAttributeData? attribute = method.GetCustomAttributesData().FirstOrDefault((CustomAttributeData a) => a.AttributeType.Name == "ReaderCallbackAttribute");
					if (attribute != null && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is Type signalType)
					{
						try
						{
							result[signalType] = Scan(method);
						}
						catch (Exception)
						{
						}
					}
				}
			}
		}
		return result;
	}

	// Walks the callback's IL and records, for each argument passed by address to a *TranslationTable.Translate
	// call, the int constant most recently loaded before it (the strategy the game applies when translation fails).
	public static Dictionary<int, int> Scan(MethodInfo m)
	{
		var result = new Dictionary<int, int>();
		byte[]? il = m.GetMethodBody()?.GetILAsByteArray();
		if (il == null)
		{
			return result;
		}
		Dictionary<short, OpCode> ops = Ops();
		int pos = 0;
		int argIndex = -1;
		int lastConstant = -1;
		while (pos < il.Length)
		{
			short code = il[pos++];
			if (code == 0xFE && pos < il.Length)
			{
				code = (short)(0xFE00 | il[pos++]);
			}
			if (!ops.TryGetValue(code, out OpCode op))
			{
				return result;
			}
			int operand = 0;
			switch (op.OperandType)
			{
			case OperandType.ShortInlineBrTarget:
			case OperandType.ShortInlineI:
			case OperandType.ShortInlineVar:
				operand = op.OperandType == OperandType.ShortInlineI ? (sbyte)il[pos] : il[pos];
				pos++;
				break;
			case OperandType.InlineVar:
				operand = BitConverter.ToUInt16(il, pos);
				pos += 2;
				break;
			case OperandType.InlineI8:
			case OperandType.InlineR:
				pos += 8;
				break;
			case OperandType.InlineSwitch:
				pos += 4 + 4 * BitConverter.ToInt32(il, pos);
				break;
			default:
				operand = BitConverter.ToInt32(il, pos);
				pos += 4;
				break;
			case OperandType.InlineNone:
				break;
			}
			if (op == OpCodes.Ldarga_S || op == OpCodes.Ldarga)
			{
				argIndex = operand;
			}
			else if (op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value)
			{
				lastConstant = op.Value - OpCodes.Ldc_I4_0.Value;
			}
			else if (op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4)
			{
				lastConstant = operand;
			}
			else if (op == OpCodes.Call)
			{
				MethodBase? callee = null;
				try
				{
					callee = m.Module.ResolveMethod(operand);
				}
				catch (Exception)
				{
				}
				if (callee != null && callee.Name == "Translate")
				{
					Type? declaringType = callee.DeclaringType;
					if (declaringType is not null && declaringType.Name.EndsWith("TranslationTable", StringComparison.Ordinal) && argIndex >= 1)
					{
						result[argIndex - 1] = lastConstant;
					}
				}
				argIndex = -1;
			}
		}
		return result;
	}

	private static Dictionary<short, OpCode> Ops()
	{
		if (_ops != null)
		{
			return _ops;
		}
		var ops = new Dictionary<short, OpCode>();
		foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public))
		{
			if (field.GetValue(null) is OpCode op)
			{
				ops[op.Value] = op;
			}
		}
		return _ops = ops;
	}
}
