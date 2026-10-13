namespace Y51_SE2Online.Sync;

public enum ObservableOpKind : byte
{
	NewList = 1,
	NewObject,
	Member,
	ListChange,
	Release
}
