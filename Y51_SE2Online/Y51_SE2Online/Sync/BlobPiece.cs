using System;

namespace Y51_SE2Online.Sync;

public readonly record struct BlobPiece(Guid Id, int Total, int Offset, byte[] Bytes)
{
	public bool MissingOnHost => Total < 0;

	public const int Overhead = 28;
}
