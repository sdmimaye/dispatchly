using System;
using System.Collections.Generic;

namespace Dispatchly.SourceGenerators;

internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    private readonly T[]? _values;

    public EquatableArray(T[] values) => _values = values;

    public int Length => _values?.Length ?? 0;

    public T this[int index] => _values![index];

    public bool Equals(EquatableArray<T> other)
    {
        var left = _values ?? [];
        var right = other._values ?? [];
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(other[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 0;
        if (_values is null)
        {
            return hash;
        }

        foreach (var value in _values)
        {
            hash = (hash * 397) ^ value.GetHashCode();
        }

        return hash;
    }
}
