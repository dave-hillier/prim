; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PRIM001 | Prim.Continuation | Warning | [Continuable] on a member that is not a method
PRIM002 | Prim.Continuation | Warning | [Continuable] method shape not supported
PRIM003 | Prim.Continuation | Error | Yield point inside finally, lock or catch filter
