namespace Eve.Services;

/// <summary>Numbers the bound objects a process hands out (the <c>1</c> in <c>N=65450:1</c>), starting from 1.</summary>
public static class BoundObjectIds
{
    private static long _last;

    public static long Next() => Interlocked.Increment(ref _last);
}
