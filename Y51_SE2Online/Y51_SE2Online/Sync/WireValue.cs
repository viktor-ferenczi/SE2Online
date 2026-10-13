using System;
using System.Collections.Generic;
using System.Text;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class WireValue
{
	public WireKind Kind;

	public uint Id;

	public string? Text;

	public byte[]? Bytes;

	public Guid Guid;

	public List<(string Name, WireValue Value)>? Fields;

	public List<WireValue>? Items;

	public const int MaxDepth = 16;

	public const int MaxItems = 65536;

	public static readonly WireValue NullValue = new WireValue
	{
		Kind = WireKind.Null
	};

	public static WireValue Of(WireKind k)
	{
		return new WireValue
		{
			Kind = k
		};
	}

	public static WireValue Raw(byte[] b)
	{
		return new WireValue
		{
			Kind = WireKind.Bytes,
			Bytes = b
		};
	}

	public static WireValue Ent(uint id)
	{
		return new WireValue
		{
			Kind = WireKind.Entity,
			Id = id
		};
	}

	public static WireValue Comp(uint id, string type)
	{
		return new WireValue
		{
			Kind = WireKind.Component,
			Id = id,
			Text = type
		};
	}

	public static WireValue Def(Guid g)
	{
		return new WireValue
		{
			Kind = WireKind.Definition,
			Guid = g
		};
	}

	public static WireValue Str(string s)
	{
		return new WireValue
		{
			Kind = WireKind.String,
			Text = s
		};
	}

	public static WireValue Ser(string type, byte[] b)
	{
		return new WireValue
		{
			Kind = WireKind.Serialized,
			Text = type,
			Bytes = b
		};
	}

	public static WireValue Obj(string type, List<(string, WireValue)> fields)
	{
		return new WireValue
		{
			Kind = WireKind.Object,
			Text = type,
			Fields = fields
		};
	}

	public static WireValue Arr(string elemType, List<WireValue> items)
	{
		return new WireValue
		{
			Kind = WireKind.Array,
			Text = elemType,
			Items = items
		};
	}

	public static WireValue TypeName(string aqn)
	{
		return new WireValue
		{
			Kind = WireKind.Type,
			Text = aqn
		};
	}

	public static WireValue Observable(uint oid)
	{
		return new WireValue
		{
			Kind = WireKind.Observable,
			Id = oid
		};
	}

	public static WireValue List(uint oid)
	{
		return new WireValue
		{
			Kind = WireKind.ObservedList,
			Id = oid
		};
	}

	public void CollectOids(List<uint> into, int depth = 0)
	{
		if (depth > 16)
		{
			return;
		}
		switch (Kind)
		{
		case WireKind.Observable:
		case WireKind.ObservedList:
			into.Add(Id);
			break;
		case WireKind.Object:
		{
			foreach (var field in Fields)
			{
				field.Value.CollectOids(into, depth + 1);
			}
			break;
		}
		case WireKind.Array:
		{
			foreach (WireValue item in Items)
			{
				item.CollectOids(into, depth + 1);
			}
			break;
		}
		}
	}

	public static void Write(WireWriter w, WireValue v, int depth = 0)
	{
		if (depth > 16)
		{
			throw new ArgumentException("wire value nested too deeply");
		}
		w.U8((byte)v.Kind);
		switch (v.Kind)
		{
		case WireKind.Bytes:
			w.I32(v.Bytes.Length).Bytes(v.Bytes);
			break;
		case WireKind.Entity:
		case WireKind.Observable:
		case WireKind.ObservedList:
			w.U32(v.Id);
			break;
		case WireKind.Component:
			w.U32(v.Id).Str(v.Text);
			break;
		case WireKind.Definition:
		{
			Span<byte> span = stackalloc byte[16];
			v.Guid.TryWriteBytes(span);
			w.Bytes(span);
			break;
		}
		case WireKind.String:
		case WireKind.Type:
			LongStr(w, v.Text);
			break;
		case WireKind.Serialized:
			w.Str(v.Text).I32(v.Bytes.Length).Bytes(v.Bytes);
			break;
		case WireKind.Object:
			w.Str(v.Text).U16((ushort)v.Fields.Count);
			{
				foreach (var (s, v2) in v.Fields)
				{
					w.Str(s);
					Write(w, v2, depth + 1);
				}
				break;
			}
		case WireKind.Array:
			w.Str(v.Text).I32(v.Items.Count);
			{
				foreach (WireValue item in v.Items)
				{
					Write(w, item, depth + 1);
				}
				break;
			}
		case WireKind.Session:
		case WireKind.Builder:
		case WireKind.ServerRequest:
		case WireKind.Default:
			break;
		}
	}

	public static WireValue Read(ref WireReader r, int depth = 0)
	{
		if (depth > 16)
		{
			throw new ArgumentOutOfRangeException("depth", "wire value nested too deeply");
		}
		WireValue wireValue = new WireValue
		{
			Kind = (WireKind)r.U8()
		};
		switch (wireValue.Kind)
		{
		case WireKind.Bytes:
			wireValue.Bytes = r.Bytes(Len(ref r, 16777216)).ToArray();
			break;
		case WireKind.Entity:
		case WireKind.Observable:
		case WireKind.ObservedList:
			wireValue.Id = r.U32();
			break;
		case WireKind.Component:
			wireValue.Id = r.U32();
			wireValue.Text = r.Str();
			break;
		case WireKind.Definition:
			wireValue.Guid = new Guid(r.Bytes(16));
			break;
		case WireKind.String:
		case WireKind.Type:
			wireValue.Text = ReadLongStr(ref r);
			break;
		case WireKind.Serialized:
			wireValue.Text = r.Str();
			wireValue.Bytes = r.Bytes(Len(ref r, 67108864)).ToArray();
			break;
		case WireKind.Object:
		{
			wireValue.Text = r.Str();
			int num2 = r.U16();
			wireValue.Fields = new List<(string, WireValue)>(num2);
			for (int j = 0; j < num2; j++)
			{
				string item = r.Str();
				wireValue.Fields.Add((item, Read(ref r, depth + 1)));
			}
			break;
		}
		case WireKind.Array:
		{
			wireValue.Text = r.Str();
			int num = Len(ref r, 65536);
			wireValue.Items = new List<WireValue>(Math.Min(num, 1024));
			for (int i = 0; i < num; i++)
			{
				wireValue.Items.Add(Read(ref r, depth + 1));
			}
			break;
		}
		default:
			throw new ArgumentOutOfRangeException("Kind", $"unknown wire kind {(byte)wireValue.Kind}");
		case WireKind.Null:
		case WireKind.Session:
		case WireKind.Builder:
		case WireKind.ServerRequest:
		case WireKind.Default:
			break;
		}
		return wireValue;
	}

	private static int Len(ref WireReader r, int max)
	{
		int num = r.I32();
		if (num < 0 || num > max)
		{
			throw new ArgumentOutOfRangeException("n", $"length {num}");
		}
		return num;
	}

	private static void LongStr(WireWriter w, string s)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		w.I32(bytes.Length).Bytes(bytes);
	}

	private static string ReadLongStr(ref WireReader r)
	{
		return Encoding.UTF8.GetString(r.Bytes(Len(ref r, 1048576)));
	}

	public override string ToString()
	{
		return Kind switch
		{
			WireKind.Bytes => $"bytes[{Bytes.Length}]", 
			WireKind.Entity => $"entity#{Id}", 
			WireKind.Component => $"{Short(Text)}@{Id}", 
			WireKind.Definition => $"def {Guid}", 
			WireKind.String => "\"" + Text + "\"", 
			WireKind.Serialized => $"{Short(Text)}[{Bytes.Length} B]", 
			WireKind.Object => $"{Short(Text)}{{{Fields.Count}}}", 
			WireKind.Array => $"{Short(Text)}[{Items.Count}]", 
			WireKind.Type => "type " + Short(Text), 
			WireKind.Observable => $"observable#{Id}", 
			WireKind.ObservedList => $"list#{Id}", 
			_ => Kind.ToString(), 
		};
	}

	private static string Short(string? t)
	{
		if (t == null)
		{
			return "?";
		}
		int num = t.IndexOf(',');
		if (num >= 0)
		{
			t = t.Substring(0, num);
		}
		int num2 = t.LastIndexOfAny(new char[2] { '.', '+' });
		if (num2 < 0)
		{
			return t;
		}
		string text = t;
		int num3 = num2 + 1;
		return text.Substring(num3, text.Length - num3);
	}
}
