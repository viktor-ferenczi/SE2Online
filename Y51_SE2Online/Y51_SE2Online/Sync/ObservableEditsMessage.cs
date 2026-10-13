using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class ObservableEditsMessage
{
	public static byte[] Encode(uint seq, IReadOnlyList<MemberEdit> edits)
	{
		WireWriter wireWriter = new WireWriter(32 + edits.Count * 24).U8(59).U32(seq).U16((ushort)edits.Count);
		foreach (MemberEdit edit in edits)
		{
			wireWriter.U32(edit.Oid).Str(edit.Name);
			WireValue.Write(wireWriter, edit.Value);
		}
		return wireWriter.ToArray();
	}

	public static (uint Seq, List<MemberEdit> Edits) Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		uint item = r.U32();
		int num = r.U16();
		List<MemberEdit> list = new List<MemberEdit>(num);
		for (int i = 0; i < num; i++)
		{
			uint oid = r.U32();
			string name = r.Str();
			list.Add(new MemberEdit(oid, name, WireValue.Read(ref r)));
		}
		return (Seq: item, Edits: list);
	}
}
