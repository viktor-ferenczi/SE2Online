namespace Y51_SE2Online.Sync;

public enum JoinStage : byte
{
	Idle,
	WaitingForSteam,
	LookingForHost,
	Connecting,
	WaitingForHostWorld,
	Receiving,
	Loading,
	Building,
	WaitingForCharacter,
	Playing,
	Left,
	Lost,
	Rejected,
	GaveUp,
	Failed
}
