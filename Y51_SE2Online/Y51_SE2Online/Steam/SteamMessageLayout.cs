using System.Runtime.InteropServices;
using Steamworks;

namespace Y51_SE2Online.Steam;

public static class SteamMessageLayout
{
	public static readonly int Data = Off("m_pData");

	public static readonly int Size = Off("m_cbSize");

	public static readonly int Conn = Off("m_conn");

	public static readonly int IdentityPeer = Off("m_identityPeer");

	public static readonly int ConnUserData = Off("m_nConnUserData");

	public static readonly int Flags = Off("m_nFlags");

	public static readonly int Channel = Off("m_nChannel");

	public static readonly int UserData = Off("m_nUserData");

	public static readonly int Lane = Off("m_idxLane");

	public static readonly int TotalSize = Marshal.SizeOf<SteamNetworkingMessage_t>();

	public static readonly (string Field, int Offset)[] NativeX64 = new (string, int)[10]
	{
		("m_pData", 0),
		("m_cbSize", 8),
		("m_conn", 12),
		("m_identityPeer", 16),
		("m_nConnUserData", 152),
		("m_nChannel", 192),
		("m_nFlags", 196),
		("m_nUserData", 200),
		("m_idxLane", 208),
		("sizeof", 216)
	};

	public static (string Field, int Offset)[] Actual => new (string, int)[10]
	{
		("m_pData", Data),
		("m_cbSize", Size),
		("m_conn", Conn),
		("m_identityPeer", IdentityPeer),
		("m_nConnUserData", ConnUserData),
		("m_nChannel", Channel),
		("m_nFlags", Flags),
		("m_nUserData", UserData),
		("m_idxLane", Lane),
		("sizeof", TotalSize)
	};

	public static bool MatchesNative(out string detail)
	{
		(string, int)[] actual = Actual;
		for (int i = 0; i < NativeX64.Length; i++)
		{
			if (actual[i].Item2 != NativeX64[i].Offset)
			{
				detail = $"{NativeX64[i].Field}: managed {actual[i].Item2}, native {NativeX64[i].Offset}";
				return false;
			}
		}
		detail = "ok";
		return true;
	}

	private static int Off(string field)
	{
		return (int)Marshal.OffsetOf<SteamNetworkingMessage_t>(field);
	}
}
