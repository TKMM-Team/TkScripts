using System.Numerics;

namespace TkScripts.MinFsCompiler.Extensions;

public static class SpanExtensions
{   
    public static unsafe void ReplaceInline<T>(this ReadOnlySpan<T> span, T old, T replace) where T : unmanaged, IEqualityOperators<T, T, bool>
    {
        fixed (T* ptr = span) {
            new Span<T>(ptr, span.Length)
                .ReplaceInline(old, replace);
        }
    }
    
    public static void ReplaceInline<T>(this Span<T> span, T old, T replace) where T : unmanaged, IEqualityOperators<T, T, bool>
    {
        for (int i = 0; i < span.Length; i++) {
            if (span[i] == old) {
                span[i] = replace;
            }
        }
    }
}