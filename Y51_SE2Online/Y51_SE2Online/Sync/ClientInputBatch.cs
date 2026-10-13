using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class ClientInputBatch
{
	public PeerId Peer;

	public ushort ClientId;

	public uint CharacterNetId;

	public readonly List<(uint NetId, int TypeId, byte[] Bytes)> Data = new List<(uint, int, byte[])>();

	public readonly List<(uint NetId, int TypeId)> Removes = new List<(uint, int)>();

	public readonly List<(string Signal, WireCommand Command)> Commands = new List<(string, WireCommand)>();
}
