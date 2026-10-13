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
		List<uint> list = new List<uint>();
		Value.CollectOids(list);
		if (Members != null)
		{
			foreach (var member in Members)
			{
				member.Value.CollectOids(list);
			}
		}
		if (Items != null)
		{
			foreach (WireValue item in Items)
			{
				item.CollectOids(list);
			}
		}
		return list;
	}

	public void Write(WireWriter w)
	{
		w.U8((byte)Kind).U32(Oid);
		switch (Kind)
		{
		case ObservableOpKind.NewObject:
			w.Str(Text).U16((ushort)Members.Count);
			{
				foreach (var (s, v) in Members)
				{
					w.Str(s);
					WireValue.Write(w, v);
				}
				break;
			}
		case ObservableOpKind.NewList:
			w.Str(Text).I32(Items.Count);
			{
				foreach (WireValue item in Items)
				{
					WireValue.Write(w, item);
				}
				break;
			}
		case ObservableOpKind.Member:
			w.Str(Text);
			WireValue.Write(w, Value);
			break;
		case ObservableOpKind.ListChange:
			w.U8(Change).I32(Start).I32(Count)
				.I32(Items?.Count ?? (-1));
			if (Items == null)
			{
				break;
			}
			{
				foreach (WireValue item2 in Items)
				{
					WireValue.Write(w, item2);
				}
				break;
			}
		default:
			throw new ArgumentOutOfRangeException("Kind", $"observable op {Kind}");
		case ObservableOpKind.Release:
			break;
		}
	}

	public static ObservableOp Read(ref WireReader r)
	{
		ObservableOpKind observableOpKind = (ObservableOpKind)r.U8();
		ObservableOp observableOp = new ObservableOp
		{
			Kind = observableOpKind,
			Oid = r.U32()
		};
		switch (observableOpKind)
		{
		case ObservableOpKind.NewObject:
		{
			observableOp.Text = r.Str();
			int num2 = r.U16();
			if (num2 > 1024)
			{
				throw new ArgumentOutOfRangeException("n", $"{num2} members");
			}
			observableOp.Members = new List<(string, WireValue)>(num2);
			for (int j = 0; j < num2; j++)
			{
				string item = r.Str();
				observableOp.Members.Add((item, WireValue.Read(ref r)));
			}
			break;
		}
		case ObservableOpKind.NewList:
		{
			observableOp.Text = r.Str();
			int num3 = r.I32();
			if (num3 < 0 || num3 > 65536)
			{
				throw new ArgumentOutOfRangeException("n", $"{num3} items");
			}
			observableOp.Items = new List<WireValue>(Math.Min(num3, 1024));
			for (int k = 0; k < num3; k++)
			{
				observableOp.Items.Add(WireValue.Read(ref r));
			}
			break;
		}
		case ObservableOpKind.Member:
			observableOp.Text = r.Str();
			observableOp.Value = WireValue.Read(ref r);
			break;
		case ObservableOpKind.ListChange:
		{
			observableOp.Change = r.U8();
			observableOp.Start = r.I32();
			observableOp.Count = r.I32();
			int num = r.I32();
			if (num < -1 || num > 65536)
			{
				throw new ArgumentOutOfRangeException("n", $"{num} items");
			}
			if (num >= 0)
			{
				observableOp.Items = new List<WireValue>(Math.Min(num, 1024));
				for (int i = 0; i < num; i++)
				{
					observableOp.Items.Add(WireValue.Read(ref r));
				}
			}
			break;
		}
		default:
			throw new ArgumentOutOfRangeException("kind", $"unknown observable op {(byte)observableOpKind}");
		case ObservableOpKind.Release:
			break;
		}
		return observableOp;
	}

	public override string ToString()
	{
		return Kind switch
		{
			ObservableOpKind.NewObject => $"new #{Oid} {ShortType(Text)} ({Members.Count} members)", 
			ObservableOpKind.NewList => $"new list #{Oid} {ShortType(Text)} [{Items.Count}]", 
			ObservableOpKind.Member => $"#{Oid}.{Text} = {Value}", 
			ObservableOpKind.ListChange => $"list #{Oid} {ChangeName(Change)} at {Start}" + ((Change == 1) ? $" x{Count}" : $" [{Items?.Count ?? 0}]"), 
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
		int num = t.IndexOf(',');
		if (num >= 0)
		{
			t = t.Substring(0, num);
		}
		int num2 = t.IndexOf('`');
		int num3 = t.LastIndexOfAny(new char[2] { '.', '+' }, (num2 >= 0) ? num2 : (t.Length - 1));
		if (num3 < 0)
		{
			return t;
		}
		string text = t;
		int num4 = num3 + 1;
		return text.Substring(num4, text.Length - num4);
	}
}
