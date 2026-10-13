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

	public static readonly WireValue NullValue = new() { Kind = WireKind.Null };

	public static WireValue Of(WireKind k) =>
		new() { Kind = k };

	public static WireValue Raw(byte[] b) =>
		new() { Kind = WireKind.Bytes, Bytes = b };

	public static WireValue Ent(uint id) =>
		new() { Kind = WireKind.Entity, Id = id };

	public static WireValue Comp(uint id, string type) =>
		new() { Kind = WireKind.Component, Id = id, Text = type };

	public static WireValue Def(Guid g) =>
		new() { Kind = WireKind.Definition, Guid = g };

	public static WireValue Str(string s) =>
		new() { Kind = WireKind.String, Text = s };

	public static WireValue Ser(string type, byte[] b) =>
		new() { Kind = WireKind.Serialized, Text = type, Bytes = b };

	public static WireValue Obj(string type, List<(string, WireValue)> fields) =>
		new() { Kind = WireKind.Object, Text = type, Fields = fields };

	public static WireValue Arr(string elemType, List<WireValue> items) =>
		new() { Kind = WireKind.Array, Text = elemType, Items = items };

	public static WireValue TypeName(string aqn) =>
		new() { Kind = WireKind.Type, Text = aqn };

	public static WireValue Observable(uint oid) =>
		new() { Kind = WireKind.Observable, Id = oid };

	public static WireValue List(uint oid) =>
		new() { Kind = WireKind.ObservedList, Id = oid };

	public void CollectOids(List<uint> into, int depth = 0)
	{
		if (depth > MaxDepth)
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
			foreach (var field in Fields)
			{
				field.Value.CollectOids(into, depth + 1);
			}
			break;
		case WireKind.Array:
			foreach (WireValue item in Items)
			{
				item.CollectOids(into, depth + 1);
			}
			break;
		}
	}

	public static void Write(WireWriter w, WireValue v, int depth = 0)
	{
		if (depth > MaxDepth)
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
			foreach (var (name, value) in v.Fields)
			{
				w.Str(name);
				Write(w, value, depth + 1);
			}
			break;
		case WireKind.Array:
			w.Str(v.Text).I32(v.Items.Count);
			foreach (WireValue item in v.Items)
			{
				Write(w, item, depth + 1);
			}
			break;
		case WireKind.Session:
		case WireKind.Builder:
		case WireKind.ServerRequest:
		case WireKind.Default:
			break;
		}
	}

	public static WireValue Read(ref WireReader r, int depth = 0)
	{
		if (depth > MaxDepth)
		{
			throw new ArgumentOutOfRangeException(nameof(depth), "wire value nested too deeply");
		}
		var value = new WireValue
		{
			Kind = (WireKind)r.U8()
		};
		switch (value.Kind)
		{
		case WireKind.Bytes:
			value.Bytes = r.Bytes(Len(ref r, 16777216)).ToArray();
			break;
		case WireKind.Entity:
		case WireKind.Observable:
		case WireKind.ObservedList:
			value.Id = r.U32();
			break;
		case WireKind.Component:
			value.Id = r.U32();
			value.Text = r.Str();
			break;
		case WireKind.Definition:
			value.Guid = new Guid(r.Bytes(16));
			break;
		case WireKind.String:
		case WireKind.Type:
			value.Text = ReadLongStr(ref r);
			break;
		case WireKind.Serialized:
			value.Text = r.Str();
			value.Bytes = r.Bytes(Len(ref r, 67108864)).ToArray();
			break;
		case WireKind.Object:
		{
			value.Text = r.Str();
			int count = r.U16();
			value.Fields = new List<(string, WireValue)>(count);
			for (int i = 0; i < count; i++)
			{
				string name = r.Str();
				value.Fields.Add((name, Read(ref r, depth + 1)));
			}
			break;
		}
		case WireKind.Array:
		{
			value.Text = r.Str();
			int count = Len(ref r, MaxItems);
			// Cap the pre-allocation so a hostile length cannot reserve a huge list up front.
			value.Items = new List<WireValue>(Math.Min(count, 1024));
			for (int i = 0; i < count; i++)
			{
				value.Items.Add(Read(ref r, depth + 1));
			}
			break;
		}
		default:
			throw new ArgumentOutOfRangeException(nameof(Kind), $"unknown wire kind {(byte)value.Kind}");
		case WireKind.Null:
		case WireKind.Session:
		case WireKind.Builder:
		case WireKind.ServerRequest:
		case WireKind.Default:
			break;
		}
		return value;
	}

	private static int Len(ref WireReader r, int max)
	{
		int length = r.I32();
		if (length < 0 || length > max)
		{
			throw new ArgumentOutOfRangeException("n", $"length {length}");
		}
		return length;
	}

	private static void LongStr(WireWriter w, string s)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		w.I32(bytes.Length).Bytes(bytes);
	}

	private static string ReadLongStr(ref WireReader r) =>
		Encoding.UTF8.GetString(r.Bytes(Len(ref r, 1048576)));

	public override string ToString() =>
		Kind switch
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

	private static string Short(string? t)
	{
		if (t == null)
		{
			return "?";
		}
		int comma = t.IndexOf(',');
		if (comma >= 0)
		{
			t = t.Substring(0, comma);
		}
		int separator = t.LastIndexOfAny(new[] { '.', '+' });
		return separator >= 0 ? t[(separator + 1)..] : t;
	}
}
