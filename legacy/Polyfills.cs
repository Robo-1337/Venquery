// Compiler-feature and BCL polyfills so the shared .NET 8 source compiles on .NET Framework 4.8.
// Legacy-only (this file lives in the legacy/ project dir, not linked into the net8 build). All types
// here are either empty marker attributes the compiler needs, or small shims for APIs net48 lacks.

using System.ComponentModel;

// ---- records / init / required (compiler-required marker types) ------------------------------------
namespace System.Runtime.CompilerServices
{
    /// <summary>Enables <c>init</c> accessors and records on net48.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }

    /// <summary>Enables the <c>required</c> modifier on net48.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Lets a constructor satisfy <c>required</c> members on net48.</summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}

// ---- Index / Range (needed for the ^ and .. operators the shared code uses on strings) -------------
namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;
        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public static Index FromStart(int value) => new(value);
        public static Index FromEnd(int value) => new(value, true);
        public int GetOffset(int length) => IsFromEnd ? length - (~_value) : _value;
        public static implicit operator Index(int value) => new(value);
        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Index i && Equals(i);
        public override int GetHashCode() => _value;
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new(start, Index.FromEnd(0));
        public static Range EndAt(Index end) => new(Index.FromStart(0), end);
        public static Range All => new(Index.FromStart(0), Index.FromEnd(0));
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range r && Equals(r);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
    }
}

// ---- string API shims net48 lacks (used by shared filter/format code) ------------------------------
namespace System
{
    internal static class LegacyStringShims
    {
        // net48 has no Contains(string, StringComparison) or StartsWith(char)/EndsWith(char) overloads.
        public static bool Contains(this string s, string value, StringComparison comparison)
            => s.IndexOf(value, comparison) >= 0;

        public static bool StartsWith(this string s, char value)
            => s.Length > 0 && s[0] == value;

        public static bool EndsWith(this string s, char value)
            => s.Length > 0 && s[s.Length - 1] == value;
    }
}
