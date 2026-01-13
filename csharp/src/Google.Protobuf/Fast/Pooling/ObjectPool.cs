using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

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

        // Pool protection mechanism: if _isInPool < 0, the object is protected from returning.
        // Each Return() increments _isInPool by 1. When _isInPool reaches 1, the object is returned.
        // Example with PoolProtectOneTime():
        //   _isInPool = -1 (protected)
        //   First Return():  Add(-1, 1) = 0, 0 <= 0 → skip return, object stays available
        //   Second Return(): _isInPool = 0 < 0 is false → skip this block → return to pool
        if (value._isInPool < 0)
        {
            var valueInPool = Interlocked.Add(ref value._isInPool, 1);

            // Still protected (negative or zero) — do not return to pool yet
            if (valueInPool <= 0)
            {
                return;
            }
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

        // Reset the object for safe reuse (Clear is abstract in PooledObjectBase).
        value.Clear();

        _queue.Enqueue(value);

        Interlocked.Increment(ref _count);
    }
}

