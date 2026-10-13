using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Y51_SE2Online.Core;

namespace Y51_SE2Online;

internal static class EngineMembers
{
	private const BindingFlags All = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	public static Type? Type(EngineMember m)
	{
		return System.Type.GetType(m.Type + ", " + m.Assembly, throwOnError: false) ?? AccessTools.TypeByName(m.Type);
	}

	public static MethodInfo Method(EngineMember m)
	{
		List<MethodInfo> list = (from x in (Type(m) ?? throw new MissingMemberException("type " + m.Type + " not found in " + m.Assembly)).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where x.Name == m.Name && (m.Params == null || ParamsMatch(x, m.Params))
			select x).ToList();
		if (list.Count != 1)
		{
			throw new MissingMemberException($"{m}: {list.Count} matching methods (expected 1, params {string.Join(", ", m.Params ?? Array.Empty<string>())})");
		}
		return list[0];
	}

	public static FieldInfo Field(EngineMember m)
	{
		return (Type(m) ?? throw new MissingMemberException("type " + m.Type + " not found in " + m.Assembly)).GetField(m.Name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException($"field {m} not found");
	}

	public static PropertyInfo Property(EngineMember m)
	{
		return (Type(m) ?? throw new MissingMemberException("type " + m.Type + " not found in " + m.Assembly)).GetProperty(m.Name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException($"property {m} not found");
	}

	private static bool ParamsMatch(MethodInfo mi, string[] expected)
	{
		ParameterInfo[] parameters = mi.GetParameters();
		if (parameters.Length != expected.Length)
		{
			return false;
		}
		for (int i = 0; i < parameters.Length; i++)
		{
			if (parameters[i].ParameterType.FullName != expected[i])
			{
				return false;
			}
		}
		return true;
	}

	public static Func<object, TResult> Getter<TResult>(PropertyInfo p)
	{
		MethodInfo method = p.GetGetMethod(nonPublic: true) ?? throw new MissingMemberException(p.Name + " has no getter");
		ParameterExpression parameterExpression = Expression.Parameter(typeof(object), "o");
		return Expression.Lambda<Func<object, TResult>>(Expression.Convert(Expression.Call(Expression.Convert(parameterExpression, p.DeclaringType), method), typeof(TResult)), new ParameterExpression[1] { parameterExpression }).Compile();
	}
}
