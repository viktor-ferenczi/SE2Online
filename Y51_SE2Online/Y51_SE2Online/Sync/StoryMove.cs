using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class StoryMove
{
	public uint StoryId;

	public string StateMachineType;

	public List<(string Name, WireValue Value)> Fields = new List<(string, WireValue)>();

	public byte[] Encode()
	{
		WireWriter wireWriter = new WireWriter(128).U8(41).U32(StoryId).Str(StateMachineType)
			.U16((ushort)Fields.Count);
		foreach (var (s, v) in Fields)
		{
			wireWriter.Str(s);
			WireValue.Write(wireWriter, v);
		}
		return wireWriter.ToArray();
	}

	public static StoryMove Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		StoryMove storyMove = new StoryMove
		{
			StoryId = r.U32(),
			StateMachineType = r.Str()
		};
		int num = r.U16();
		for (int i = 0; i < num; i++)
		{
			string item = r.Str();
			storyMove.Fields.Add((item, WireValue.Read(ref r)));
		}
		return storyMove;
	}
}
