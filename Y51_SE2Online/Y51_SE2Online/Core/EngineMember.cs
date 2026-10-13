namespace Y51_SE2Online.Core;

public sealed class EngineMember
{
	public string Id { get; }

	public string Assembly { get; }

	public string Type { get; }

	public MemberKind Kind { get; }

	public string Name { get; }

	public string[]? Params { get; }

	public string Why { get; }

	public bool InterfaceDispatchOnly { get; init; }

	public EngineMember(string id, string assembly, string type, MemberKind kind, string name, string[]? @params, string why)
	{
		Id = id;
		Assembly = assembly;
		Type = type;
		Kind = kind;
		Name = name;
		Params = @params;
		Why = why;
	}

	public override string ToString()
	{
		return Type + "." + Name;
	}
}
