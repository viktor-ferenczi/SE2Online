using System;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class JoinRequest
{
	public ushort SyncVersion = SyncProtocol.Version;

	public string PluginVersion = "";

	public string GameVersion = "";

	public string PlayerName = "";

	public string Password = "";

	public byte[] Encode()
	{
		return new WireWriter().U8(32).U16(SyncVersion).Str(PluginVersion)
			.Str(GameVersion)
			.Str(PlayerName)
			.Str(Password)
			.ToArray();
	}

	public static JoinRequest Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		return new JoinRequest
		{
			SyncVersion = wireReader.U16(),
			PluginVersion = wireReader.Str(),
			GameVersion = wireReader.Str(),
			PlayerName = wireReader.Str(),
			Password = wireReader.Remaining > 0 ? wireReader.Str() : ""
		};
	}
}
