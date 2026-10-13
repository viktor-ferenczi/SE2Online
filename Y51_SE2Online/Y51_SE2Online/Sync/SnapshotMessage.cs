using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class SnapshotMessage
{
	public static byte[] Encode(ReadOnlySpan<byte> snapshot, IReadOnlyList<Guid>? requiredBlobs = null)
	{
		WireWriter wireWriter = new WireWriter(12 + snapshot.Length + (requiredBlobs?.Count ?? 0) * 16).U8(48).I32(snapshot.Length).Bytes(snapshot);
		WireGuids.Write(wireWriter, requiredBlobs);
		return wireWriter.ToArray();
	}

	public static byte[] Decode(ReadOnlySpan<byte> d)
	{
		List<Guid> requiredBlobs;
		return Decode(d, out requiredBlobs);
	}

	public static byte[] Decode(ReadOnlySpan<byte> d, out List<Guid> requiredBlobs)
	{
		WireReader r = new WireReader(d.Slice(1));
		int n = r.I32();
		byte[] result = r.Bytes(n).ToArray();
		requiredBlobs = WireGuids.Read(ref r);
		return result;
	}
}
