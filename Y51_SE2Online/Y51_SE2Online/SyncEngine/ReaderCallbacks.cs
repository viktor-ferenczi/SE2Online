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
		Dictionary<Type, Dictionary<int, int>> dictionary = new Dictionary<Type, Dictionary<int, int>>();
		Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
		foreach (Assembly assembly in assemblies)
		{
			string name = assembly.GetName().Name;
			if (name == null || (!name.StartsWith("Game2", StringComparison.Ordinal) && !name.StartsWith("VRage", StringComparison.Ordinal) && !(name == "SpaceEngineers2")))
			{
				continue;
			}
			Type[] array;
			try
			{
				array = assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				array = ex.Types.Where((Type t) => t != null).ToArray();
			}
			Type[] array2 = array;
			foreach (Type type in array2)
			{
				if (type.Name != "InProcessReplicationReaderCallbacks")
				{
					continue;
				}
				MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (MethodInfo methodInfo in methods)
				{
					CustomAttributeData customAttributeData = methodInfo.GetCustomAttributesData().FirstOrDefault((CustomAttributeData a) => a.AttributeType.Name == "ReaderCallbackAttribute");
					if (customAttributeData != null && customAttributeData.ConstructorArguments.Count == 1 && customAttributeData.ConstructorArguments[0].Value is Type key)
					{
						try
						{
							dictionary[key] = Scan(methodInfo);
						}
						catch (Exception)
						{
						}
					}
				}
			}
		}
		return dictionary;
	}

	public static Dictionary<int, int> Scan(MethodInfo m)
	{
		Dictionary<int, int> dictionary = new Dictionary<int, int>();
		byte[] array = m.GetMethodBody()?.GetILAsByteArray();
		if (array == null)
		{
			return dictionary;
		}
		Dictionary<short, OpCode> dictionary2 = Ops();
		int num = 0;
		int num2 = -1;
		int value = -1;
		while (num < array.Length)
		{
			short num3 = array[num++];
			if (num3 == 254 && num < array.Length)
			{
				num3 = (short)(0xFE00 | array[num++]);
			}
			if (!dictionary2.TryGetValue(num3, out var value2))
			{
				return dictionary;
			}
			int num4 = 0;
			switch (value2.OperandType)
			{
			case OperandType.ShortInlineBrTarget:
			case OperandType.ShortInlineI:
			case OperandType.ShortInlineVar:
				num4 = ((value2.OperandType == OperandType.ShortInlineI) ? ((int)(sbyte)array[num]) : ((int)array[num]));
				num++;
				break;
			case OperandType.InlineVar:
				num4 = BitConverter.ToUInt16(array, num);
				num += 2;
				break;
			case OperandType.InlineI8:
			case OperandType.InlineR:
				num += 8;
				break;
			case OperandType.InlineSwitch:
				num += 4 + 4 * BitConverter.ToInt32(array, num);
				break;
			default:
				num4 = BitConverter.ToInt32(array, num);
				num += 4;
				break;
			case OperandType.InlineNone:
				break;
			}
			if (value2 == OpCodes.Ldarga_S || value2 == OpCodes.Ldarga)
			{
				num2 = num4;
			}
			else if (value2.Value >= OpCodes.Ldc_I4_0.Value && value2.Value <= OpCodes.Ldc_I4_8.Value)
			{
				value = value2.Value - OpCodes.Ldc_I4_0.Value;
			}
			else if (value2 == OpCodes.Ldc_I4_S || value2 == OpCodes.Ldc_I4)
			{
				value = num4;
			}
			else
			{
				if (!(value2 == OpCodes.Call))
				{
					continue;
				}
				MethodBase methodBase = null;
				try
				{
					methodBase = m.Module.ResolveMethod(num4);
				}
				catch (Exception)
				{
				}
				if (methodBase != null && methodBase.Name == "Translate")
				{
					Type? declaringType = methodBase.DeclaringType;
					if ((object)declaringType != null && declaringType.Name.EndsWith("TranslationTable", StringComparison.Ordinal) && num2 >= 1)
					{
						dictionary[num2 - 1] = value;
					}
				}
				num2 = -1;
			}
		}
		return dictionary;
	}

	private static Dictionary<short, OpCode> Ops()
	{
		if (_ops != null)
		{
			return _ops;
		}
		Dictionary<short, OpCode> dictionary = new Dictionary<short, OpCode>();
		FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public);
		for (int i = 0; i < fields.Length; i++)
		{
			if (fields[i].GetValue(null) is OpCode value)
			{
				dictionary[value.Value] = value;
			}
		}
		return _ops = dictionary;
	}
}
