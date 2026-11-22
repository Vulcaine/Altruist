// Polyfill: `record`/`init` accessors compile against
// System.Runtime.CompilerServices.IsExternalInit, which is not present in
// netstandard2.1. Defining it ourselves under the same FQN makes records
// work without a runtime dependency. On net5+ the framework's own type
// shadows ours and this file is effectively dead — declared internal so it
// doesn't pollute the public API surface.

#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
