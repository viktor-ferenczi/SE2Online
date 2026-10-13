namespace Y51_SE2Online.Sync;

public enum WireKind : byte
{
	Null,
	Bytes,
	Entity,
	Component,
	Definition,
	String,
	Serialized,
	Session,
	Object,
	Array,
	Builder,
	ServerRequest,
	Default,
	Type,
	Observable,
	ObservedList
}
