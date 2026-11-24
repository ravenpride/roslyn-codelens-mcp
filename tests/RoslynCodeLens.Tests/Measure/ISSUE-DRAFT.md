# Upstream issue 2 — draft

**Title:** Framework types are split per target framework: usages in net48, netstandard2.0 and net8+ projects get lost

**MCP Tool:** find_implementations (several, see description)
**Bug type:** Incorrect results
**.NET version:** 11.0.100
**Package version:** 2.19.1

---

### Description

## Summary

A framework type such as `System.IDisposable` lives in a different assembly per target framework: `mscorlib` for
net48, `netstandard` for netstandard2.0 and `System.Runtime` for net8+. In a solution that mixes these target
frameworks, several tools report the usages of only one of those groups when queried with a framework symbol.
Which group is reported depends on the instance the type index keeps, so the result changes from one load to the
next (same cause as point 2 in #586).

This is a follow-up to #586 and independent of #587: #587 does not change any of the paths below, and the
measurements show the same gaps on 2.19.1 and on the #587 head.

## Repro

Three independent projects with identical code, no package references:

```xml
<!-- Net48Lib/Net48Lib.csproj; NetStdLib and Net10App alike with netstandard2.0 / net10.0 -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

```csharp
using System;

namespace Net48Lib
{
    public sealed class Net48LibResource : IDisposable
    {
        public void Dispose() { }
    }

    public static class Net48LibUsages
    {
        [Obsolete("Use something else.")]
        public static void Legacy() { }

        public static void Write() { Console.WriteLine("Net48Lib"); }
    }
}
```

Expected: every query below reports all three projects. Measured, by calling the tools' `*Logic` classes on the
loaded solution (all three compilations without errors):

| Query | 2.19.1 (index kept the `mscorlib` instance) | #587 head (index kept the `netstandard` instance) |
|---|---|---|
| `find_attribute_usages System.ObsoleteAttribute` | Net48Lib only | NetStdLib only |
| `find_implementations System.IDisposable` | Net48Lib only | NetStdLib only |
| `get_type_hierarchy System.IDisposable` | Net48Lib only | NetStdLib only |
| `find_references System.IDisposable` | all three | Net48Lib missing |
| `find_references System.Console.WriteLine` | all three | Net48Lib missing |
| `find_callers System.Console.WriteLine` | all three | Net48Lib missing |
| `analyze_change_impact System.IDisposable` | all three | Net48Lib missing |

Not affected: `find_attribute_usages Obsolete` (simple-name path), `find_event_subscribers`, `find_throw_sites`,
`find_catch_blocks`, `get_extension_methods`.

## Root cause

There are two mechanisms, and which one applies depends on the tool:

1. **Our comparer.** `NamedTypeSignatureComparer` compares `ContainingAssembly.Identity.Name`. The three
   instances of `System.IDisposable` therefore never match, and the tools that go through it (the inheritance
   maps in `SymbolResolver`, the full-name path of `find_attribute_usages`) only see the target framework group of
   the instance the index kept.
2. **Roslyn's `SymbolFinder` is asymmetric here.** I called `SymbolFinder.FindReferencesAsync()` with
   `System.IDisposable` taken from each compilation:

   | Start symbol from | Its assembly | Referenced core assemblies | Projects found |
   |---|---|---|---|
   | Net48Lib | `mscorlib` | `mscorlib` | all three |
   | NetStdLib | `netstandard` | `mscorlib`, `netstandard`, `System.Runtime` | NetStdLib, Net10App |
   | Net10App | `System.Runtime` | `mscorlib`, `netstandard`, `System.Runtime` | NetStdLib, Net10App |

   The netstandard2.0 and net10.0 reference sets contain facades that forward `mscorlib` types, so a start symbol
   from `mscorlib` can be matched there. The net48 compilation references only `mscorlib`; nothing in it forwards
   `netstandard` or `System.Runtime`, so a start symbol from those assemblies is never matched against net48.
   The `SymbolFinder`-based tools are complete only if the index happens to keep the net48 instance.

Comparing types without the assembly is not an option: two projects that each declare the same full name (the
`Duplicate.Helper` case in #586) must stay distinct types.

## Relation to #586 part B, and a design question

Part B of #586 keeps every type per full name and asks whether tools should merge the results of all candidates or
report an ambiguity. The three `System.IDisposable` instances above would become three such candidates, and here
merging is the only right answer — they are one logical type. For `Duplicate.Helper` they are two types. A
forwarding-aware type identity tells the two cases apart:

- At load time, walk the referenced assemblies of all compilations and collect `IAssemblySymbol.GetForwardedTypes()`.
  Every forwarder links (forwarding assembly, full metadata name) with (destination assembly, full metadata name);
  union-find over all links gives one canonical assembly per forwarded type. Because the map covers the whole
  solution, it also links net48's `mscorlib` with net10's `System.Runtime` through the facades of the other
  compilations, which Roslyn's per-compilation check cannot do.
- `NamedTypeSignatureComparer` compares the canonical assembly instead of the assembly name (and hashes it). The
  map is per solution, so the comparer becomes an instance owned by the loaded solution instead of a static
  singleton, and the call sites take it from there.
- With part B keeping all instances, the index groups the forwarded instances into one entry (merged results),
  while same-named types in different assemblies stay separate candidates. The `SymbolFinder`-based tools run once
  per instance of the entry and merge by location, which also covers the asymmetry above.

Questions:

1. Do you want mixed .NET Framework / modern .NET solutions covered at all?
2. If yes, would you take the forwarding map together with part B (one design for "which types are the same"),
   or as a separate step after it?

I have the fixture and the measurement on a branch and can turn the rows above into failing tests once the
direction is settled. Not part of this issue: `String.Split` binds to a different overload on net10.0 than on
net48 (correct behavior), and `get_call_graph` resolving `Console.WriteLine` to its parameterless overload is the
known "first overload wins" behavior.

### Steps to reproduce

See above.

### Error output / logs

(none)
