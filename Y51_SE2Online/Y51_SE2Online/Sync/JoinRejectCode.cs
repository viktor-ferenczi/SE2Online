namespace Y51_SE2Online.Sync;

public static class JoinRejectCode
{
	public const byte Generic = 1;

	public const byte Removed = 2;

	public const byte Password = 3;

	/// <summary>Different plugin build; older clients show the text like <see cref="Generic"/>.</summary>
	public const byte Version = 4;
}
