using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.UI;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.SecureIds;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class ObjectCodec
{
	private delegate bool TryAnyFn(DefinitionManager dm, Guid id, out Definition? definition);

	private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldsCache = new ConcurrentDictionary<Type, FieldInfo[]>();

	private static readonly ConcurrentDictionary<Type, Func<object, byte[]>> ToBytesCache = new ConcurrentDictionary<Type, Func<object, byte[]>>();

	private static readonly ConcurrentDictionary<Type, Func<byte[], object>> FromBytesCache = new ConcurrentDictionary<Type, Func<byte[], object>>();

	private static readonly ConcurrentDictionary<Type, bool> SerializableCache = new ConcurrentDictionary<Type, bool>();

	private static readonly ConcurrentDictionary<string, Type?> TypeCache = new ConcurrentDictionary<string, Type>();

	private static readonly HashSet<Type> PlainDataClasses = new HashSet<Type> { typeof(BlockPlacerEntityBundle) };

	private static Func<DefinitionManager, Guid, Definition?>? _tryAny;

	private static bool _tryAnyResolved;

	public const string DictPrefix = "Dict:";

	public const string SetPrefix = "Set:";

	public const string ReleasedModel = "was already released by the host";

	private static readonly MethodInfo IsRefMethod = typeof(RuntimeHelpers).GetMethod("IsReferenceOrContainsReferences");

	private static readonly ConcurrentDictionary<Type, bool> RefCache = new ConcurrentDictionary<Type, bool>();

	private static readonly MethodInfo SerializeGeneric = typeof(SerializationHelper).GetMethods(BindingFlags.Static | BindingFlags.Public).First((MethodInfo m) => m.Name == "Serialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(SerializationContext));

	private static readonly MethodInfo DeserializeGeneric = typeof(SerializationHelper).GetMethods(BindingFlags.Static | BindingFlags.Public).First((MethodInfo m) => m.Name == "Deserialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(SerializationContext));

	internal static bool DefinitionLookupBound => TryAny() != null;

	public static string TypeName(Type t)
	{
		return t.FullName + ", " + t.Assembly.GetName().Name;
	}

	public static Type? FindType(string name)
	{
		return TypeCache.GetOrAdd(name, (string n) =>
		{
			try
			{
				return Type.GetType(n, throwOnError: false);
			}
			catch (Exception)
			{
				return (Type?)null;
			}
		});
	}

	public static FieldInfo[] Fields(Type t)
	{
		return FieldsCache.GetOrAdd(t, (Type type) =>
		{
			List<FieldInfo> list = new List<FieldInfo>();
			Type type2 = type;
			while (type2 != null && type2 != typeof(object) && type2 != typeof(ValueType))
			{
				list.AddRange(type2.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
				type2 = type2.BaseType;
			}
			return list.ToArray();
		});
	}

	private static FieldInfo? Field(Type t, string name)
	{
		return Fields(t).FirstOrDefault((FieldInfo f) => f.Name == name);
	}

	public static WireValue Encode(object? o, Type declared, ObjectSpace sp, ref string? fail, int depth = 0, bool copyObservables = false)
	{
		if (fail != null)
		{
			return WireValue.NullValue;
		}
		if (o == null)
		{
			return WireValue.NullValue;
		}
		if (depth > 14)
		{
			fail = declared.Name + " nested too deeply";
			return WireValue.NullValue;
		}
		if (!(o is Entity entity))
		{
			if (!(o is Component component))
			{
				if (!(o is Session))
				{
					if (!(o is Definition definition))
					{
						if (!(o is string s))
						{
							if (!(o is StringId stringId))
							{
								if (!(o is Type t))
								{
									if (!(o is Delegate obj))
									{
										if (o is ObservableObject && !copyObservables)
										{
											if (sp.ObservableId == null)
											{
												fail = o.GetType().Name + " is an observable object (shared by replication pairing; this side does not name models)";
												return WireValue.NullValue;
											}
											uint? num = sp.ObservableId(o);
											if (num.HasValue)
											{
												uint valueOrDefault = num.GetValueOrDefault();
												sp.Observed.Add(valueOrDefault);
												return WireValue.Observable(valueOrDefault);
											}
											Func<object, bool>? wasReleased = sp.WasReleased;
											fail = ((wasReleased != null && wasReleased(o)) ? (o.GetType().Name + " was already released by the host") : (o.GetType().Name + " is an observable model the other side cannot have (not replicated)"));
											return WireValue.NullValue;
										}
										Type type = o.GetType();
										if (o is IList arg && o is INotifyCollectionChanged)
										{
											uint? num = sp.ListId?.Invoke(arg);
											if (num.HasValue)
											{
												uint valueOrDefault2 = num.GetValueOrDefault();
												sp.Observed.Add(valueOrDefault2);
												return WireValue.List(valueOrDefault2);
											}
										}
										if (type.IsArray && type.GetArrayRank() == 1)
										{
											Type elementType = type.GetElementType();
											Array array = (Array)o;
											List<WireValue> list = new List<WireValue>(array.Length);
											foreach (object item in array)
											{
												list.Add(Encode(item, elementType, sp, ref fail, depth + 1, copyObservables));
											}
											return WireValue.Arr(TypeName(elementType), list);
										}
										if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
										{
											Type type2 = type.GetGenericArguments()[0];
											List<WireValue> list2 = new List<WireValue>();
											foreach (object item2 in (IList)o)
											{
												list2.Add(Encode(item2, type2, sp, ref fail, depth + 1, copyObservables));
											}
											return WireValue.Arr("List:" + TypeName(type2), list2);
										}
										if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
										{
											Type type3 = type.GetGenericArguments()[0];
											List<WireValue> list3 = new List<WireValue>();
											foreach (object item3 in (IEnumerable)o)
											{
												list3.Add(Encode(item3, type3, sp, ref fail, depth + 1, copyObservables));
											}
											return WireValue.Arr("ImmutableArray:" + TypeName(type3), list3);
										}
										if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<, >))
										{
											Type[] genericArguments = type.GetGenericArguments();
											List<WireValue> list4 = new List<WireValue>();
											foreach (DictionaryEntry item4 in (IDictionary)o)
											{
												list4.Add(Encode(item4.Key, genericArguments[0], sp, ref fail, depth + 1, copyObservables));
												list4.Add(Encode(item4.Value, genericArguments[1], sp, ref fail, depth + 1, copyObservables));
											}
											return WireValue.Arr("Dict:" + TypeName(genericArguments[0]) + "|" + TypeName(genericArguments[1]), list4);
										}
										if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
										{
											Type type4 = type.GetGenericArguments()[0];
											List<WireValue> list5 = new List<WireValue>();
											foreach (object item5 in (IEnumerable)o)
											{
												list5.Add(Encode(item5, type4, sp, ref fail, depth + 1, copyObservables));
											}
											return WireValue.Arr("Set:" + TypeName(type4), list5);
										}
										bool flag = declared == type || Nullable.GetUnderlyingType(declared) == type || declared.IsEnum;
										if ((type.IsValueType && !ContainsReferences(type)) & flag)
										{
											if (ContainsStringId(type, 0))
											{
												return EncodeFields(o, type, sp, ref fail, depth, copyObservables);
											}
											byte[] array2 = ToBytes(o, type);
											string text = (sp.OpaqueHandles ? null : ProcessLocal(type, 0));
											if (text != null && Array.Exists(array2, (byte b) => b != 0))
											{
												fail = type.Name + " carries a process-local " + text;
												return WireValue.NullValue;
											}
											return WireValue.Raw(array2);
										}
										if (IsCompilerGenerated(type) || type.IsValueType || type == typeof(ReplicationEventContext))
										{
											return EncodeFields(o, type, sp, ref fail, depth, copyObservables || IsReleaseToServer(type));
										}
										if (PlainDataClasses.Contains(type))
										{
											return EncodeFields(o, type, sp, ref fail, depth, copyObservables);
										}
										if (IsKeenSerializable(type))
										{
											try
											{
												return WireValue.Ser(TypeName(type), SerializeKeen(o, type, sp));
											}
											catch (Exception ex)
											{
												fail = $"{type.Name} did not serialize ({ex.GetType().Name}: {ex.Message})";
												return WireValue.NullValue;
											}
										}
										fail = type.FullName + " cannot travel";
										return WireValue.NullValue;
									}
									fail = "a delegate (" + obj.Method.Name + ") cannot travel";
									return WireValue.NullValue;
								}
								return WireValue.TypeName(TypeName(t));
							}
							return WireValue.Str(stringId.String ?? "");
						}
						return WireValue.Str(s);
					}
					return WireValue.Def(definition.Guid);
				}
				return WireValue.Of(WireKind.Session);
			}
			uint? num2 = sp.IdOf(component.Entity);
			if (!num2.HasValue)
			{
				fail = component.GetType().Name + " on an entity the other side does not have";
				return WireValue.NullValue;
			}
			return WireValue.Comp(num2.Value, TypeName(component.GetType()));
		}
		uint? num3 = sp.IdOf(entity);
		if (num3.HasValue)
		{
			return WireValue.Ent(num3.Value);
		}
		if (!sp.LocalIds.TryGetValue(entity, out var value))
		{
			value = (sp.LocalIds[entity] = sp.NextLocalId());
		}
		return new WireValue
		{
			Kind = WireKind.Entity,
			Id = (uint)value
		};
	}

	private static WireValue EncodeFields(object o, Type type, ObjectSpace sp, ref string? fail, int depth, bool copyObservables)
	{
		List<(string, WireValue)> list = new List<(string, WireValue)>();
		FieldInfo[] array = Fields(type);
		foreach (FieldInfo fieldInfo in array)
		{
			if (!fieldInfo.IsLiteral && !fieldInfo.IsStatic)
			{
				list.Add((fieldInfo.Name, Encode(fieldInfo.GetValue(o), fieldInfo.FieldType, sp, ref fail, depth + 1, copyObservables)));
				if (fail != null)
				{
					fail = $"{type.Name}.{fieldInfo.Name}: {fail}";
					break;
				}
			}
		}
		return WireValue.Obj(TypeName(type), list);
	}

	internal static Definition? FindDefinition(Guid guid, Type declared)
	{
		DefinitionManager instance = Singleton<DefinitionManager>.Instance;
		if (typeof(Configuration).IsAssignableFrom(declared) && declared != typeof(Configuration))
		{
			if (!instance.TryGetConfiguration(declared, out Configuration configuration))
			{
				return null;
			}
			return configuration;
		}
		Func<DefinitionManager, Guid, Definition> func = TryAny();
		Definition definition;
		if (func != null)
		{
			definition = func(instance, guid);
		}
		else if (!instance.TryGetDefinition(guid, out definition))
		{
			definition = null;
		}
		if (definition is Configuration configuration2 && instance.TryGetConfiguration(configuration2.GetType(), out Configuration configuration3) && configuration3 != null)
		{
			return configuration3;
		}
		return definition;
	}

	private static Func<DefinitionManager, Guid, Definition?>? TryAny()
	{
		if (_tryAnyResolved)
		{
			return _tryAny;
		}
		try
		{
			MethodInfo methodInfo = EngineMembers.Method(HookTargets.Reflected.Single((EngineMember m) => m.Id == "defs-tryany"));
			TryAnyFn fn = methodInfo.CreateDelegate<TryAnyFn>();
			_tryAny = (DefinitionManager dm, Guid g) => (!fn(dm, g, out var definition)) ? null : definition;
		}
		catch (Exception)
		{
			_tryAny = null;
		}
		_tryAnyResolved = true;
		return _tryAny;
	}

	public static bool IsUnknownModel(string fail)
	{
		if (fail.Contains("is not on this side", StringComparison.Ordinal))
		{
			if (!fail.Contains("observable #", StringComparison.Ordinal))
			{
				return fail.Contains("observed list #", StringComparison.Ordinal);
			}
			return true;
		}
		return false;
	}

	public static object? Decode(WireValue v, Type declared, ObjectSpace sp, bool translated, ref string? fail, int depth = 0, bool inCollection = false)
	{
		if (fail != null)
		{
			return null;
		}
		if (declared.IsByRef)
		{
			declared = declared.GetElementType();
		}
		switch (v.Kind)
		{
		case WireKind.Null:
		case WireKind.Default:
			if (!declared.IsValueType || !(Nullable.GetUnderlyingType(declared) == null))
			{
				return null;
			}
			return Activator.CreateInstance(declared);
		case WireKind.Entity:
		{
			int id = (int)v.Id;
			if (id < -1)
			{
				if (!sp.LocalStubs.TryGetValue(id, out Entity value2))
				{
					value2 = (sp.LocalStubs[id] = EntityAllocator.AllocateStub());
				}
				return value2;
			}
			Entity entity2 = sp.Resolve(v.Id, translated);
			if (entity2 == null)
			{
				fail = $"entity #{v.Id} is not on this side{sp.Explain?.Invoke(v.Id)}";
			}
			return entity2;
		}
		case WireKind.Component:
		{
			Entity entity3 = sp.Resolve(v.Id, translated);
			if (entity3 == null)
			{
				fail = $"component's entity #{v.Id} is not on this side{sp.Explain?.Invoke(v.Id)}";
				return null;
			}
			Type type3 = FindType(v.Text);
			Component component = null;
			foreach (Component component2 in entity3.Components)
			{
				if (declared.IsInstanceOfType(component2))
				{
					if (type3 != null && component2.GetType() == type3)
					{
						return component2;
					}
					if (component == null)
					{
						component = component2;
					}
				}
			}
			if (component == null)
			{
				fail = $"no {declared.Name} on entity #{v.Id}";
			}
			return component;
		}
		case WireKind.Session:
		{
			Session? session = (translated ? sp.TranslatedSession : sp.Session);
			if (session == null)
			{
				fail = (translated ? "no session to translate on this side" : "no session on this side");
			}
			return session;
		}
		case WireKind.Observable:
		{
			object obj6 = sp.ResolveObservable?.Invoke(v.Id, inCollection);
			if (obj6 == null)
			{
				fail = $"observable #{v.Id} is not on this side";
				return null;
			}
			if (!(Nullable.GetUnderlyingType(declared) ?? declared).IsInstanceOfType(obj6))
			{
				fail = $"observable #{v.Id} is a {obj6.GetType().Name} here, not a {declared.Name}";
				return null;
			}
			return obj6;
		}
		case WireKind.ObservedList:
		{
			object? obj2 = sp.ResolveList?.Invoke(v.Id);
			if (obj2 == null)
			{
				fail = $"observed list #{v.Id} is not on this side";
			}
			return obj2;
		}
		case WireKind.Definition:
		{
			Definition definition = FindDefinition(v.Guid, Nullable.GetUnderlyingType(declared) ?? declared);
			if (definition != null)
			{
				return definition;
			}
			fail = $"definition {v.Guid} is not loaded here";
			return null;
		}
		case WireKind.String:
			if (declared == typeof(StringId) || declared == typeof(StringId?))
			{
				return StringId.Get(v.Text);
			}
			return v.Text;
		case WireKind.Type:
		{
			Type? type4 = FindType(v.Text);
			if (type4 == null)
			{
				fail = "type " + v.Text + " not found";
			}
			return type4;
		}
		case WireKind.Bytes:
		{
			Type type9 = Nullable.GetUnderlyingType(declared) ?? declared;
			if (!type9.IsValueType || ContainsReferences(type9))
			{
				fail = "raw bytes for " + declared.Name;
				return null;
			}
			if (CommandLayouts.SizeOf(type9) != v.Bytes.Length)
			{
				fail = $"{type9.Name}: {v.Bytes.Length} B, expected {CommandLayouts.SizeOf(type9)} B";
				return null;
			}
			return FromBytes(v.Bytes, type9);
		}
		case WireKind.Array:
		{
			if (v.Text.StartsWith("Dict:", StringComparison.Ordinal))
			{
				string text = v.Text;
				int length = "Dict:".Length;
				string[] array = text.Substring(length, text.Length - length).Split('|');
				Type type5 = ((array.Length == 2) ? FindType(array[0]) : null);
				Type type6 = ((array.Length == 2) ? FindType(array[1]) : null);
				if (type5 == null || type6 == null || v.Items.Count % 2 != 0)
				{
					fail = "dictionary " + v.Text + " not readable here";
					return null;
				}
				IDictionary dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<, >).MakeGenericType(type5, type6));
				for (int i = 0; i + 1 < v.Items.Count; i += 2)
				{
					object obj3 = Decode(v.Items[i], type5, sp, translated: false, ref fail, depth + 1, inCollection: true);
					object value3 = Decode(v.Items[i + 1], type6, sp, translated: false, ref fail, depth + 1, inCollection: true);
					if (fail != null)
					{
						return null;
					}
					if (obj3 != null)
					{
						dictionary[obj3] = value3;
					}
				}
				return dictionary;
			}
			if (v.Text.StartsWith("Set:", StringComparison.Ordinal))
			{
				string text = v.Text;
				int length = "Set:".Length;
				Type type7 = FindType(text.Substring(length, text.Length - length));
				if (type7 == null)
				{
					fail = "set element type " + v.Text + " not found";
					return null;
				}
				object obj4 = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(type7));
				MethodInfo method = obj4.GetType().GetMethod("Add");
				{
					foreach (WireValue item3 in v.Items)
					{
						object obj5 = Decode(item3, type7, sp, translated: false, ref fail, depth + 1, inCollection: true);
						if (fail != null)
						{
							return null;
						}
						method.Invoke(obj4, new object[1] { obj5 });
					}
					return obj4;
				}
			}
			string text2 = v.Text;
			bool flag = text2.StartsWith("List:", StringComparison.Ordinal);
			bool flag2 = text2.StartsWith("ImmutableArray:", StringComparison.Ordinal);
			string name;
			if (!flag)
			{
				if (!flag2)
				{
					name = text2;
				}
				else
				{
					string text = text2;
					name = text.Substring(15, text.Length - 15);
				}
			}
			else
			{
				string text = text2;
				name = text.Substring(5, text.Length - 5);
			}
			Type type8 = FindType(name);
			if (type8 == null)
			{
				fail = "element type " + text2 + " not found";
				return null;
			}
			Array array2 = Array.CreateInstance(type8, v.Items.Count);
			for (int j = 0; j < v.Items.Count; j++)
			{
				array2.SetValue(Decode(v.Items[j], type8, sp, translated: false, ref fail, depth + 1, inCollection: true), j);
			}
			if (flag)
			{
				IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type8));
				{
					foreach (object item4 in array2)
					{
						list.Add(item4);
					}
					return list;
				}
			}
			if (flag2)
			{
				return typeof(ImmutableArray).GetMethods().First((MethodInfo m) =>
				{
					if (m.Name == "Create")
					{
						ParameterInfo[] parameters = m.GetParameters();
						if (parameters != null && parameters.Length == 1)
						{
							return parameters[0].ParameterType.IsArray;
						}
					}
					return false;
				}).MakeGenericMethod(type8)
					.Invoke(null, new object[1] { array2 });
			}
			return array2;
		}
		case WireKind.Object:
		{
			Type type2 = FindType(v.Text);
			if (type2 == null)
			{
				fail = "type " + v.Text + " not found";
				return null;
			}
			object obj = (type2.IsValueType ? Activator.CreateInstance(type2) : RuntimeHelpers.GetUninitializedObject(type2));
			{
				foreach (var field in v.Fields)
				{
					string item = field.Name;
					WireValue item2 = field.Value;
					FieldInfo fieldInfo = Field(type2, item);
					if (fieldInfo == null)
					{
						fail = type2.Name + " has no field " + item + " here";
						return null;
					}
					object value = Decode(item2, fieldInfo.FieldType, sp, translated: false, ref fail, depth + 1, inCollection);
					if (fail != null)
					{
						fail = $"{type2.Name}.{item}: {fail}";
						return null;
					}
					fieldInfo.SetValue(obj, value);
				}
				return obj;
			}
		}
		case WireKind.Serialized:
		{
			Type type = FindType(v.Text);
			if (type == null)
			{
				fail = "type " + v.Text + " not found";
				return null;
			}
			try
			{
				return DeserializeKeen(v.Bytes, type, sp);
			}
			catch (Exception ex)
			{
				fail = $"{type.Name} did not deserialize ({ex.GetType().Name}: {ex.Message})";
				return null;
			}
		}
		default:
			fail = $"{v.Kind} cannot be decoded as {declared.Name}";
			return null;
		}
	}

	public static bool ContainsReferences(Type t)
	{
		return RefCache.GetOrAdd(t, (Type x) => (bool)IsRefMethod.MakeGenericMethod(x).Invoke(null, null));
	}

	public static bool IsReleaseToServer(Type t)
	{
		if (t.IsGenericType)
		{
			return t.GetGenericTypeDefinition() == typeof(ReleaseToServer<>);
		}
		return false;
	}

	public static string? TypeProblem(Type t, bool copyObservables = false, int depth = 0)
	{
		if (t.IsByRef)
		{
			t = t.GetElementType();
		}
		t = Nullable.GetUnderlyingType(t) ?? t;
		if (depth > 6)
		{
			return null;
		}
		if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(StringId) || t == typeof(Type) || t == typeof(decimal) || t == typeof(Guid))
		{
			return null;
		}
		if (typeof(Entity).IsAssignableFrom(t) || typeof(Component).IsAssignableFrom(t) || typeof(Session).IsAssignableFrom(t) || typeof(Definition).IsAssignableFrom(t))
		{
			return null;
		}
		if (typeof(Delegate).IsAssignableFrom(t))
		{
			return "delegate";
		}
		if (typeof(ObservableObject).IsAssignableFrom(t))
		{
			return null;
		}
		if (t.IsArray)
		{
			return TypeProblem(t.GetElementType(), copyObservables, depth + 1);
		}
		if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(ImmutableArray<>) || t.GetGenericTypeDefinition() == typeof(HashSet<>)))
		{
			return TypeProblem(t.GetGenericArguments()[0], copyObservables, depth + 1);
		}
		if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<, >))
		{
			return TypeProblem(t.GetGenericArguments()[0], copyObservables, depth + 1) ?? TypeProblem(t.GetGenericArguments()[1], copyObservables, depth + 1);
		}
		if (t.IsValueType && !ContainsReferences(t))
		{
			string text = (ContainsStringId(t, 0) ? null : ProcessLocal(t, 0));
			if (text != null)
			{
				return t.Name + " (" + text + ": only while unassigned)";
			}
			return null;
		}
		if (IsCompilerGenerated(t) || t.IsValueType || t == typeof(ReplicationEventContext))
		{
			bool copyObservables2 = copyObservables || IsReleaseToServer(t);
			FieldInfo[] array = Fields(t);
			foreach (FieldInfo fieldInfo in array)
			{
				if (!fieldInfo.IsStatic && !fieldInfo.IsLiteral)
				{
					string text2 = TypeProblem(fieldInfo.FieldType, copyObservables2, depth + 1);
					if (text2 != null)
					{
						return $"{t.Name}.{fieldInfo.Name}: {text2}";
					}
				}
			}
			return null;
		}
		if (IsKeenSerializable(t))
		{
			return null;
		}
		if (t == typeof(object) || t.IsInterface || t.IsAbstract)
		{
			return t.Name + " (depends on the value)";
		}
		return t.Name + " cannot travel";
	}

	public static bool IsCompilerGenerated(Type t)
	{
		if (!t.Name.Contains('<') && t.GetCustomAttribute<CompilerGeneratedAttribute>() == null)
		{
			if (t.DeclaringType != null && IsCompilerGenerated(t.DeclaringType))
			{
				return t.Name.Contains("DisplayClass");
			}
			return false;
		}
		return true;
	}

	private static bool ContainsStringId(Type t, int depth)
	{
		if (t == typeof(StringId))
		{
			return true;
		}
		if (t.IsPrimitive || t.IsEnum || depth > 4)
		{
			return false;
		}
		FieldInfo[] array = Fields(t);
		foreach (FieldInfo fieldInfo in array)
		{
			if (!fieldInfo.IsStatic && ContainsStringId(fieldInfo.FieldType, depth + 1))
			{
				return true;
			}
		}
		return false;
	}

	public static string? ProcessLocal(Type t, int depth)
	{
		if (t.IsPointer || t == typeof(nint) || t == typeof(nuint))
		{
			return "pointer";
		}
		if (t == typeof(EntityId))
		{
			return null;
		}
		if (t.FullName == "Keen.VRage.DCS.Accessors.DEntity" || t.Name == "EntityId")
		{
			return t.Name;
		}
		if (t.IsPrimitive || t.IsEnum || depth > 4)
		{
			return null;
		}
		FieldInfo[] array = Fields(t);
		foreach (FieldInfo fieldInfo in array)
		{
			if (!fieldInfo.IsStatic)
			{
				string text = ProcessLocal(fieldInfo.FieldType, depth + 1);
				if (text != null)
				{
					return text;
				}
			}
		}
		return null;
	}

	private static bool IsKeenSerializable(Type t)
	{
		return SerializableCache.GetOrAdd(t, (Type x) => typeof(ISerializable).IsAssignableFrom(x));
	}

	public static byte[] ToBytes(object box, Type t)
	{
		return ToBytesCache.GetOrAdd(t, (Type x) => (Func<object, byte[]>)typeof(ObjectCodec).GetMethod("ToBytesT", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(x).CreateDelegate(typeof(Func<object, byte[]>)))(box);
	}

	public static object FromBytes(byte[] b, Type t)
	{
		return FromBytesCache.GetOrAdd(t, (Type x) => (Func<byte[], object>)typeof(ObjectCodec).GetMethod("FromBytesT", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(x).CreateDelegate(typeof(Func<byte[], object>)))(b);
	}

	private static byte[] ToBytesT<T>(object box) where T : struct
	{
		T reference = (T)box;
		return MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref reference, 1)).ToArray();
	}

	private static object FromBytesT<T>(byte[] b) where T : struct
	{
		return MemoryMarshal.Read<T>(b);
	}

	private static byte[] SerializeKeen(object o, Type t, ObjectSpace sp)
	{
		bool bundle = typeof(EntityBundle).IsAssignableFrom(t);
		NetIdProxyContext proxies = new NetIdProxyContext((Entity e) =>
		{
			uint? num = sp.IdOf(e);
			if (num.HasValue)
			{
				return (int)num.Value;
			}
			if (sp.LocalIds.TryGetValue(e, out var value) || sp.BundleLocalIds.TryGetValue(e, out value))
			{
				return value;
			}
			int num2 = sp.NextLocalId();
			if (bundle)
			{
				sp.BundleLocalIds[e] = num2;
			}
			else
			{
				sp.LocalIds[e] = num2;
			}
			return num2;
		}, null);
		return EngineCodec.SerializeObject(o, t, proxies, SerializeGeneric);
	}

	private static object? DeserializeKeen(byte[] b, Type t, ObjectSpace sp)
	{
		NetIdProxyContext proxies = new NetIdProxyContext(null, (int id) =>
		{
			if (id < -1)
			{
				if (!sp.LocalStubs.TryGetValue(id, out Entity value))
				{
					value = (sp.LocalStubs[id] = EntityAllocator.AllocateStub());
				}
				return value;
			}
			return sp.Resolve((uint)id, arg2: false) ?? EntityAllocator.AllocateStub();
		});
		return EngineCodec.DeserializeObject(b, t, proxies, DeserializeGeneric);
	}
}
