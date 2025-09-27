/*
    Neko-Net Client — Utils.RollingList
    ----------------------------------
    Purpose
    - A simple fixed-size list that evicts the oldest element when the capacity is reached. Thread-safe Add using a lock,
      indexer for random access, and IEnumerable support for iteration.
*/
using System.Collections;

namespace NekoNetClient.Utils;

/// <summary>
/// Fixed-size FIFO collection that keeps at most <see cref="MaximumCount"/> elements.
/// </summary>
public class RollingList<T> : IEnumerable<T>
{
    private readonly object _addLock = new();
    private readonly LinkedList<T> _list = new();

    /// <summary>
    /// Creates a new rolling list with a maximum element count.
    /// </summary>
    /// <param name="maximumCount">Maximum number of elements to retain, must be positive.</param>
    public RollingList(int maximumCount)
    {
        if (maximumCount <= 0)
            throw new ArgumentException(message: null, nameof(maximumCount));

        MaximumCount = maximumCount;
    }

    /// <summary>Current element count.</summary>
    public int Count => _list.Count;
    /// <summary>Maximum number of elements retained by the list.</summary>
    public int MaximumCount { get; }

    /// <summary>Gets the element at the specified index.</summary>
    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _list.Skip(index).First();
        }
    }

    /// <summary>
    /// Adds a new value to the end of the list. When capacity is reached, the oldest element is removed.
    /// </summary>
    public void Add(T value)
    {
        lock (_addLock)
        {
            if (_list.Count == MaximumCount)
            {
                _list.RemoveFirst();
            }
            _list.AddLast(value);
        }
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}