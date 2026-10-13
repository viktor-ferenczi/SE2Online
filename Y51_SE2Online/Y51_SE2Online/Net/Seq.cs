namespace Y51_SE2Online.Net;

public static class Seq
{
	public static bool Newer(uint a, uint b)
	{
		return (int)(a - b) > 0;
	}

	public static int Diff(uint a, uint b)
	{
		return (int)(a - b);
	}
}
