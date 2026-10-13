# Pulsar build notes

`VRage.Library.Generator.dll` in this folder is an empty assembly (no types). The PluginHub
registration lists it as a `Reference` asset so that it replaces the game's
`VRage.Library.Generator.dll` in Pulsar's compiler references.

Why: Pulsar 2.4.3 excludes `*.Generator.dll` from its reference globs, but still adds the game's
copy while following `VRage.Scripting`'s dependencies. That assembly is a Roslyn source generator
that embeds about 100 public `Keen.VRage.Library.*` types, so naming any of them (for example
`ObservableObject`, `BundleAttribute`) fails with CS0433. Pulsar keys references by assembly name and
lets a plugin's own references win, so an empty assembly with the same name removes the conflict.
It is a compile-time reference only; nothing loads it at runtime.

Rebuild and verify (deterministic, .NET SDK 10.0.400):

    cd GeneratorStub
    dotnet build -c Release
    sha256sum ../VRage.Library.Generator.dll
    # 379c2109e35d660709feb5f45fec19ba5df36e462f8ade75790ef828fa3b224f

Remove the folder and the `GeneratorStub` asset once Pulsar stops referencing generator assemblies.
