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
	public static bool IsBuilder(Type t) => typeof(NetworkStoryContext).IsAssignableFrom(t);

	public static List<(string, WireValue)>? Encode(object box, ObjectSpace sp, out string? fail)
	{
		fail = null;
		var members = new List<(string, WireValue)>();
		foreach (FieldInfo field in ObjectCodec.Fields(box.GetType()))
		{
			if (!field.IsStatic)
			{
				object value = field.GetValue(box);
				WireValue encoded;
				if (IsBuilder(field.FieldType))
				{
					encoded = WireValue.Of(WireKind.Builder);
				}
				else if (value is ServerRequest)
				{
					encoded = WireValue.Of(WireKind.ServerRequest);
				}
				else
				{
					encoded = !field.Name.StartsWith("<>u__", StringComparison.Ordinal) || value is ClientRequest
						? ObjectCodec.Encode(value, field.FieldType, sp, ref fail)
						: WireValue.Of(WireKind.Default);
				}
				if (fail != null)
				{
					fail = field.Name + ": " + fail;
					return null;
				}
				members.Add((field.Name, encoded));
			}
		}
		return members;
	}

	public static object? Decode(StoryMove m, ObjectSpace sp, object builder, Func<ServerRequest>? serverRequest, out string? fail)
	{
		fail = null;
		Type type = ObjectCodec.FindType(m.StateMachineType);
		if (type == null)
		{
			fail = "state machine type " + m.StateMachineType + " not found";
			return null;
		}
		object stateMachine = type.IsValueType ? Activator.CreateInstance(type) : RuntimeHelpers.GetUninitializedObject(type);
		FieldInfo[] fields = ObjectCodec.Fields(type);
		foreach (var entry in m.Fields)
		{
			string name = entry.Name;
			WireValue wire = entry.Value;
			FieldInfo field = fields.FirstOrDefault(x => x.Name == name);
			if (field == null)
			{
				fail = "field " + name + " not in this build";
				return null;
			}
			object value;
			switch (wire.Kind)
			{
			case WireKind.Builder:
				value = builder;
				break;
			case WireKind.ServerRequest:
				value = serverRequest?.Invoke();
				break;
			case WireKind.Default:
				continue;
			default:
				value = ObjectCodec.Decode(wire, field.FieldType, sp, translated: false, ref fail);
				break;
			}
			if (fail != null)
			{
				fail = name + ": " + fail;
				return null;
			}
			field.SetValue(stateMachine, value);
		}
		return stateMachine;
	}

	public static string Assess(Type sm)
	{
		var cannot = new List<string>();
		var dependsOnValues = new List<string>();
		var whileUnset = new List<string>();
		foreach (FieldInfo field in ObjectCodec.Fields(sm))
		{
			if (field.IsStatic || field.Name == "<>1__state" || IsBuilder(field.FieldType) || field.Name.StartsWith("<>u__", StringComparison.Ordinal) || field.FieldType == typeof(ServerRequest) || field.FieldType == typeof(ClientRequest))
			{
				continue;
			}
			string? problem = ObjectCodec.TypeProblem(field.FieldType);
			if (problem != null)
			{
				// Parameters, `this` and closures are always set; other hoisted locals (<x>5__n) may not be yet.
				bool alwaysSet = !field.Name.StartsWith("<", StringComparison.Ordinal) || field.Name == "<>4__this" || field.Name.StartsWith("<>8__", StringComparison.Ordinal);
				string entry = CleanField(field.Name) + ": " + problem;
				if (!alwaysSet)
				{
					whileUnset.Add(entry);
				}
				else if (problem.Contains("depends on the value", StringComparison.Ordinal))
				{
					dependsOnValues.Add(entry);
				}
				else
				{
					cannot.Add(entry);
				}
			}
		}
		if (cannot.Count > 0)
		{
			return "cannot (" + string.Join("; ", cannot) + ")";
		}
		if (dependsOnValues.Count > 0)
		{
			return "depends on values (" + string.Join("; ", dependsOnValues) + ")";
		}
		if (whileUnset.Count > 0)
		{
			return "travels while unset (" + string.Join("; ", whileUnset) + ")";
		}
		return "travels";
	}

	private static string CleanField(string n)
	{
		int open = n.IndexOf('<');
		int close = n.IndexOf('>');
		if (open != 0 || close <= 1)
		{
			return n;
		}
		return n.Substring(1, close - 1);
	}

	public static string Name(string stateMachineType)
	{
		string fullName = stateMachineType;
		int comma = fullName.IndexOf(',');
		if (comma >= 0)
		{
			fullName = fullName.Substring(0, comma);
		}
		int plus = fullName.LastIndexOf('+');
		string owner = plus > 0 ? fullName.Substring(0, plus) : fullName;
		int dot = owner.LastIndexOf('.');
		owner = dot < 0 ? owner : owner[(dot + 1)..];
		string method = plus <= 0 ? fullName : fullName[(plus + 1)..];
		int open = method.IndexOf('<');
		int close = method.IndexOf('>');
		if (open >= 0 && close > open)
		{
			method = method[(open + 1)..close];
		}
		return owner + "." + method;
	}

	public static string Shape(string text) => Regex.Replace(text, @"\d+", "N");

	public static string Describe(object? result)
	{
		if (result == null)
		{
			return " null";
		}
		if (result is DBNull)
		{
			return "";
		}
		if (result is string text)
		{
			return " \"" + (text.Length > 40 ? text.Substring(0, 40) + "..." : text) + "\"";
		}
		if (result is ICollection collection)
		{
			return $" {collection.GetType().Name} ({collection.Count})";
		}
		if (result.GetType().IsPrimitive || result.GetType().IsEnum)
		{
			return " " + result;
		}
		if (result is ITuple tuple)
		{
			string? shown = tuple.ToString();
			return " " + (shown != null && shown.Length <= 80 ? shown : result.GetType().Name);
		}
		return " " + result.GetType().Name;
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
