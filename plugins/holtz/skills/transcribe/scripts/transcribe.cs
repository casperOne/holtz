#!/usr/bin/env dotnet
#:project ../src/Holtz/Holtz.csproj

// holtz: long recordings to transcripts with a local Vibe engine.
//
//   dotnet run transcribe.cs -- run "C:\recordings\meeting.m4a"
//   dotnet run transcribe.cs -- help
//
// A .NET 10 file-based app: the first run builds the engine in src/Holtz (a few seconds),
// later runs reuse the build. Everything is in the base class library; no NuGet packages.

return Holtz.Cli.Main(args);
