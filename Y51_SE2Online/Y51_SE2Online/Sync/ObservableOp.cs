using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class ObservableOp
{
	public ObservableOpKind Kind;

	public uint Oid;

	public string Text = "";

	public WireValue Value = WireValue.NullValue;

	public List<(string Name, WireValue Value)>? Members;

	public List<WireValue>? Items;

	public byte Change;

	public int Start;

	public int Count;

	public const int MaxMembers = 1024;

	public static ObservableOp NewObject(uint oid, string type, List<(string, WireValue)> members)
	{
		return new ObservableOp
		{
			Kind = ObservableOpKind.NewObject,
			Oid = oid,
			Text = type,
			Members = members
		};
	}

	public static ObservableOp NewList(uint oid, string type, List<WireValue> items)
	{
		return new ObservableOp
		{
			Kind = ObservableOpKind.NewList,
			Oid = oid,
			Text = type,
			Items = items
		};
	}

	public static ObservableOp Member(uint oid, string name, WireValue value)
	{
		return new ObservableOp
		{
			Kind = ObservableOpKind.Member,
			Oid = oid,
			Text = name,
			Value = value
		};
	}

	public static ObservableOp Release(uint oid)
	{
		return new ObservableOp
		{
			Kind = ObservableOpKind.Release,
			Oid = oid
		};
	}

	public static ObservableOp ListChange(uint oid, byte change, int start, int removeCount, List<WireValue>? items)
	{
		return new ObservableOp
		{
			Kind = ObservableOpKind.ListChange,
			Oid = oid,
			Change = change,
			Start = start,
			Count = removeCount,
			Items = items
		};
	}

	public List<uint> Refs()
	{
		var oids = new List<uint>();
		Value.CollectOids(oids);
		if (Members != null)
		{
			foreach (var member in Members)
			{
				member.Value.CollectOids(oids);
			}
		}
		if (Items != null)
		{
			foreach (WireValue item in Items)
			{
				item.CollectOids(oids);
			}
		}
		return oids;
	}

	public void Write(WireWriter w)
	{
		w.U8((byte)Kind).U32(Oid);
		switch (Kind)
		{
		case ObservableOpKind.NewObject:
			w.Str(Text).U16((ushort)Members.Count);
			foreach (var (name, value) in Members)
			{
				w.Str(name);
				WireValue.Write(w, value);
			}
			break;
		case ObservableOpKind.NewList:
			w.Str(Text).I32(Items.Count);
			foreach (WireValue item in Items)
			{
				WireValue.Write(w, item);
			}
			break;
		case ObservableOpKind.Member:
			w.Str(Text);
			WireValue.Write(w, Value);
			break;
		case ObservableOpKind.ListChange:
			w.U8(Change).I32(Start).I32(Count).I32(Items?.Count ?? -1);
			if (Items != null)
			{
				foreach (WireValue item in Items)
				{
					WireValue.Write(w, item);
				}
			}
			break;
		case ObservableOpKind.Release:
			break;
		default:
			throw new ArgumentOutOfRangeException("Kind", $"observable op {Kind}");
		}
	}

	public static ObservableOp Read(ref WireReader r)
	{
		var kind = (ObservableOpKind)r.U8();
		var op = new ObservableOp
		{
			Kind = kind,
			Oid = r.U32()
		};
		switch (kind)
		{
		case ObservableOpKind.NewObject:
		{
			op.Text = r.Str();
			int count = r.U16();
			if (count > 1024)
			{
				throw new ArgumentOutOfRangeException("n", $"{count} members");
			}
			op.Members = new List<(string, WireValue)>(count);
			for (int i = 0; i < count; i++)
			{
				string name = r.Str();
				op.Members.Add((name, WireValue.Read(ref r)));
			}
			break;
		}
		case ObservableOpKind.NewList:
		{
			op.Text = r.Str();
			int count = r.I32();
			if (count < 0 || count > 65536)
			{
				throw new ArgumentOutOfRangeException("n", $"{count} items");
			}
			op.Items = new List<WireValue>(Math.Min(count, 1024));
			for (int i = 0; i < count; i++)
			{
				op.Items.Add(WireValue.Read(ref r));
			}
			break;
		}
		case ObservableOpKind.Member:
			op.Text = r.Str();
			op.Value = WireValue.Read(ref r);
			break;
		case ObservableOpKind.ListChange:
		{
			op.Change = r.U8();
			op.Start = r.I32();
			op.Count = r.I32();
			int count = r.I32();
			if (count < -1 || count > 65536)
			{
				throw new ArgumentOutOfRangeException("n", $"{count} items");
			}
			if (count >= 0)
			{
				op.Items = new List<WireValue>(Math.Min(count, 1024));
				for (int i = 0; i < count; i++)
				{
					op.Items.Add(WireValue.Read(ref r));
				}
			}
			break;
		}
		case ObservableOpKind.Release:
			break;
		default:
			throw new ArgumentOutOfRangeException("kind", $"unknown observable op {(byte)kind}");
		}
		return op;
	}

	public override string ToString()
	{
		return Kind switch
		{
			ObservableOpKind.NewObject => $"new #{Oid} {ShortType(Text)} ({Members.Count} members)",
			ObservableOpKind.NewList => $"new list #{Oid} {ShortType(Text)} [{Items.Count}]",
			ObservableOpKind.Member => $"#{Oid}.{Text} = {Value}",
			ObservableOpKind.ListChange => $"list #{Oid} {ChangeName(Change)} at {Start}" + (Change == 1 ? $" x{Count}" : $" [{Items?.Count ?? 0}]"),
			ObservableOpKind.Release => $"release #{Oid}",
			_ => Kind.ToString(),
		};
	}

	public static string ChangeName(byte c)
	{
		return c switch
		{
			0 => "add",
			1 => "remove",
			2 => "replace",
			3 => "reset",
			_ => $"kind {c}",
		};
	}

	public static string ShortType(string t)
	{
		if (t.Length == 0)
		{
			return t;
		}
		int comma = t.IndexOf(',');
		if (comma >= 0)
		{
			t = t.Substring(0, comma);
		}
		int tick = t.IndexOf('`');
		int dot = t.LastIndexOfAny(new[] { '.', '+' }, tick >= 0 ? tick : t.Length - 1);
		if (dot < 0)
		{
			return t;
		}
		return t[(dot + 1)..];
	}
}
