#if PULSAR
namespace System.Runtime.CompilerServices;

// Krafs.Publicizer injects this in the MSBuild build; Pulsar's from-source compile needs it in source.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
{
	public string AssemblyName { get; } = assemblyName;
}
#endif
