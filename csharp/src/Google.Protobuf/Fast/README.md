# Google.Protobuf.Fast

This is the fast layer for `FastDotnet.Protobuf`, intended for:

- **shared pool (128)** per message type (request/response)
- fast protobuf (de)serialization for generated DTOs (`IFastMessage`)

## How user code rents/returns pooled objects

### Rent (primary API)
Each generated message type provides `Rent()`:

```csharp
var req = GetCandlesRequestFast.Rent();
try
{
    req.InstrumentId = instrumentId;
    req.FromSeconds  = fromSeconds;
    req.ToSeconds    = toSeconds;
}
finally
{
    GetCandlesRequestFast.Return(req);
}
```

### Secondary option (Rent + using)
### Async scenario
Responses are returned back to the pool the same way:

```csharp
var resp = await client.GetCandlesAsync(req, ct);
// ... use resp ...
```

IMPORTANT:
- do not store `resp`/`req` in fields/cache after the scope ends - the object is returned to the pool and may be reused

## Shared pool

Each message type has a **shared** `ObjectPool<T>` with `capacity=128`.

Notes:
- the pool is thread-safe (uses a short `lock` section)
- it is valid to `Rent()` on one thread and `Return()` on another thread

