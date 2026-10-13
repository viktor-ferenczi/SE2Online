namespace Y51_SE2Online.Sync;

public static class ScreenTexts
{
	public const string LookingForFriend = "Looking for a friend who is hosting...";

	public const string WaitingForJoinGame = "Click 'Join Game' on a friend in Steam (Shift+Tab).";

	public const string HostStopped = "That host stopped; looking for another...";

	public const string FreshJoin = "Starting a fresh join...";

	public const string RelayStarting = "relay starting";

	public const string NoCharacter = "No character from the host: free camera.";

	public const string FreeCamera = "Free camera (no character).";

	public static string NoFriendHosting(string? filter, int seconds, string? reason)
	{
		return JoinProgress.Cut((reason == null) ? $"No friend{((filter != null) ? (" '" + JoinProgress.Cut(filter, 12) + "'") : "")} is hosting yet; checking every {seconds} s." : $"{reason}; checking every {seconds} s.", 56);
	}

	public static string JoinerRefused(string name, string why)
	{
		return JoinProgress.Cut("Refused " + JoinProgress.Cut(name, 16) + ": " + why, 56);
	}
}
