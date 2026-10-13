using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Keen.VRage.Multiplayer.NetworkStories;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class StoryState
{
	public static bool IsBuilder(Type t)
	{
		return typeof(NetworkStoryContext).IsAssignableFrom(t);
	}

	public static List<(string, WireValue)>? Encode(object box, ObjectSpace sp, out string? fail)
	{
		fail = null;
		List<(string, WireValue)> list = new List<(string, WireValue)>();
		FieldInfo[] array = ObjectCodec.Fields(box.GetType());
		foreach (FieldInfo fieldInfo in array)
		{
			if (!fieldInfo.IsStatic)
			{
				object value = fieldInfo.GetValue(box);
				WireValue item;
				if (IsBuilder(fieldInfo.FieldType))
				{
					item = WireValue.Of(WireKind.Builder);
				}
				else if (value is ServerRequest)
				{
					item = WireValue.Of(WireKind.ServerRequest);
				}
				else
				{
					item = ((!fieldInfo.Name.StartsWith("<>u__", StringComparison.Ordinal) || value is ClientRequest) ? ObjectCodec.Encode(value, fieldInfo.FieldType, sp, ref fail) : WireValue.Of(WireKind.Default));
				}
				if (fail != null)
				{
					fail = fieldInfo.Name + ": " + fail;
					return null;
				}
				list.Add((fieldInfo.Name, item));
			}
		}
		return list;
	}

	public static object? Decode(StoryMove m, ObjectSpace sp, object builder, Func<ServerRequest>? serverRequest, out string? fail)
	{
		//IL_00fd: Expected Obj, but got VT
		//IL_00f5->IL00fb: Incompatible stack types: Obj vs VT
		fail = null;
		Type type = ObjectCodec.FindType(m.StateMachineType);
		if (type == null)
		{
			fail = "state machine type " + m.StateMachineType + " not found";
			return null;
		}
		object obj = (type.IsValueType ? Activator.CreateInstance(type) : RuntimeHelpers.GetUninitializedObject(type));
		FieldInfo[] source = ObjectCodec.Fields(type);
		foreach (var field in m.Fields)
		{
			string name = field.Name;
			WireValue item = field.Value;
			FieldInfo fieldInfo = source.FirstOrDefault((FieldInfo x) => x.Name == name);
			if (fieldInfo == null)
			{
				fail = "field " + name + " not in this build";
				return null;
			}
			object value;
			switch (item.Kind)
			{
			case WireKind.Builder:
				value = builder;
				break;
			case WireKind.ServerRequest:
				value = serverRequest?.Invoke() ?? ((object)null);
				break;
			default:
				value = ObjectCodec.Decode(item, fieldInfo.FieldType, sp, translated: false, ref fail);
				break;
			case WireKind.Default:
				continue;
			}
			if (fail != null)
			{
				fail = name + ": " + fail;
				return null;
			}
			fieldInfo.SetValue(obj, value);
		}
		return obj;
	}

	public static string Assess(Type sm)
	{
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		List<string> list3 = new List<string>();
		FieldInfo[] array = ObjectCodec.Fields(sm);
		foreach (FieldInfo fieldInfo in array)
		{
			if (fieldInfo.IsStatic || fieldInfo.Name == "<>1__state" || IsBuilder(fieldInfo.FieldType) || fieldInfo.Name.StartsWith("<>u__", StringComparison.Ordinal) || fieldInfo.FieldType == typeof(ServerRequest) || fieldInfo.FieldType == typeof(ClientRequest))
			{
				continue;
			}
			string text = ObjectCodec.TypeProblem(fieldInfo.FieldType);
			if (text != null)
			{
				bool num = !fieldInfo.Name.StartsWith("<", StringComparison.Ordinal) || fieldInfo.Name == "<>4__this" || fieldInfo.Name.StartsWith("<>8__", StringComparison.Ordinal);
				string item = CleanField(fieldInfo.Name) + ": " + text;
				if (!num)
				{
					list3.Add(item);
				}
				else if (text.Contains("depends on the value", StringComparison.Ordinal))
				{
					list2.Add(item);
				}
				else
				{
					list.Add(item);
				}
			}
		}
		if (list.Count > 0)
		{
			return "cannot (" + string.Join("; ", list) + ")";
		}
		if (list2.Count > 0)
		{
			return "depends on values (" + string.Join("; ", list2) + ")";
		}
		if (list3.Count > 0)
		{
			return "travels while unset (" + string.Join("; ", list3) + ")";
		}
		return "travels";
	}

	private static string CleanField(string n)
	{
		int num = n.IndexOf('<');
		int num2 = n.IndexOf('>');
		if (num != 0 || num2 <= 1)
		{
			return n;
		}
		return n.Substring(1, num2 - 1);
	}

	public static string Name(string stateMachineType)
	{
		string text = stateMachineType;
		int num = text.IndexOf(',');
		if (num >= 0)
		{
			text = text.Substring(0, num);
		}
		int num2 = text.LastIndexOf('+');
		string text2 = ((num2 > 0) ? text.Substring(0, num2) : text);
		int num3 = text2.LastIndexOf('.');
		string text3;
		if (num3 < 0)
		{
			text3 = text2;
		}
		else
		{
			string text4 = text2;
			int num4 = num3 + 1;
			text3 = text4.Substring(num4, text4.Length - num4);
		}
		text2 = text3;
		string text5;
		if (num2 <= 0)
		{
			text5 = text;
		}
		else
		{
			string text4 = text;
			int num4 = num2 + 1;
			text5 = text4.Substring(num4, text4.Length - num4);
		}
		string text6 = text5;
		int num5 = text6.IndexOf('<');
		int num6 = text6.IndexOf('>');
		if (num5 >= 0 && num6 > num5)
		{
			string text7 = text6;
			int num4 = num5 + 1;
			text6 = text7.Substring(num4, num6 - num4);
		}
		return text2 + "." + text6;
	}

	public static string Shape(string text)
	{
		return Regex.Replace(text, "\\d+", "N");
	}

	public static string Describe(object? result)
	{
		if (result != null)
		{
			if (!(result is DBNull))
			{
				if (!(result is string text))
				{
					if (result is ICollection collection)
					{
						return $" {collection.GetType().Name} ({collection.Count})";
					}
					if (!result.GetType().IsPrimitive && !result.GetType().IsEnum)
					{
						if (result is ITuple tuple)
						{
							string text2 = tuple.ToString();
							return " " + ((text2 != null && text2.Length <= 80) ? text2 : result.GetType().Name);
						}
						return " " + result.GetType().Name;
					}
					return " " + result;
				}
				return " \"" + ((text.Length > 40) ? (text.Substring(0, 40) + "...") : text) + "\"";
			}
			return "";
		}
		return " null";
	}

	public static Type ResultType(object context)
	{
		Type type = context.GetType();
		while (type != null)
		{
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NetworkStoryMethodBuilder<>))
			{
				return type.GetGenericArguments()[0];
			}
			type = type.BaseType;
		}
		return typeof(DBNull);
	}
}
