using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Google.Protobuf.Fast.Abstractions;

namespace Google.Protobuf.Fast.Pooling;

#pragma warning disable CS1591

/// <summary>
/// Shared fixed-size object pool with preallocated instances.
/// </summary>
/// <remarks>
/// - One pool per type (typically a static field in generated code).
/// - If the pool is empty: creates a new object (no waiting).
/// - If the pool is full: drops the object (GC will collect it).
/// </remarks>
public sealed class ObjectPool<T> where T : PooledObjectBase
{
    readonly ConcurrentQueue<T> _queue;
    readonly int _capacity;
    readonly Func<T> _factory;
    int _count;

    public ObjectPool(int capacity = 128, Func<T> factory = null!)
    {
        _capacity = capacity < 0 ? 0 : capacity;
        _queue = new ConcurrentQueue<T>();
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

        _count = _capacity;
        for (int i = 0; i < _capacity; i++)
        {
            var obj = _factory();
            obj._isInPool = 1;
            _queue.Enqueue(obj);
        }
    }

    /// <summary>
    /// Rent an object from the pool. Must be returned back via <see cref="Return"/>.
    /// </summary>
    public T Rent()
    {
        if (_queue.TryDequeue(out var candidate))
        {
            Interlocked.Decrement(ref _count);

            candidate._isInPool = 0;

            return candidate;
        }

        return _factory();
    }

    /// <summary>
    /// Return an object to the pool.
    /// </summary>
    /// <param name="value">The object to return to the pool.</param>
    public void Return(T value)
    {
        // Reserve a slot first; Return() never blocks.
        if (_count + 1 > _capacity)
        {
            return;
        }

        // If it is a fast message, reset it for safe reuse.
        // This avoids requiring a custom reset delegate on the pool itself.
        if (value is IFastMessage msg)
        {
            msg.Clear();
        }

        // Prevent double-return.
        if (Interlocked.CompareExchange(ref value._isInPool, 1, 0) != 0)
        {
            if (Debugger.IsAttached)
            {
                Debug.WriteLine("WARNING: ObjectPool<T>.Return() received an object that is already in the pool. Ignored.");
            }

            return;
        }

        _queue.Enqueue(value);

        Interlocked.Increment(ref _count);
    }
}

