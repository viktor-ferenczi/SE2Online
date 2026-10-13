using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class CaptureBlobs
{
	public Dictionary<Guid, byte[]?> Data = new Dictionary<Guid, byte[]>();

	public List<Guid> Snapshot = new List<Guid>();

	public long Bytes
	{
		get
		{
			long num = 0L;
			foreach (byte[] value in Data.Values)
			{
				num += value?.Length ?? 0;
			}
			return num;
		}
	}
}
