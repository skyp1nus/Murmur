using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Fake COM objects for Linux-runnable tests of the raw vtable layer (see Vtables.cs): a native block whose first word
/// points to a vtable of <c>[UnmanagedCallersOnly]</c> stubs, backed by managed state looked up by the object pointer.
/// Slots without a stub return E_NOTIMPL (extra arguments are ignored: x64 / arm64 calling conventions are
/// caller-cleaned). AddRef / Release keep a real reference count (shared with the owner for interface faces) and return
/// the new count, like Media Foundation's objects. Dispose frees the native memory (not the reference count's job).
/// </summary>
internal abstract unsafe class FakeCom : IDisposable
{
    public const int ENotImpl = unchecked((int)0x80004001);

    private static readonly ConcurrentDictionary<nint, FakeCom> Objects = new();
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    private nint* _vtable;
    private nint* _self;

    protected FakeCom(int slots)
    {
        _vtable = (nint*)NativeMemory.AllocZeroed((nuint)(slots * sizeof(nint)));
        for (var i = 0; i < slots; i++)
        {
            _vtable[i] = (nint)(delegate* unmanaged<nint, int>)&NotImplemented;
        }

        _vtable[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceStub;
        _vtable[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefStub;
        _vtable[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseStub;
        _self = (nint*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(nint)));
        _self[0] = (nint)_vtable;
        Objects[(nint)_self] = this;
    }

    /// <summary>The COM interface pointer.</summary>
    public nint Pointer => (nint)_self;

    private int _references = 1;

    /// <summary>Reference count (starts at 1: the creator's reference). Faces count on their owner.</summary>
    public int References => Volatile.Read(ref Root._references);

    /// <summary>For an interface face: the object whose identity and reference count it shares.</summary>
    public FakeCom? Owner { get; init; }

    /// <summary>QueryInterface answers: IID -> face (or other object). IUnknown always answers with the owner.</summary>
    public Dictionary<Guid, FakeCom> Interfaces { get; } = [];

    /// <summary>The current reference count as seen through the vtable (the probe reference is dropped again).</summary>
    public uint ProbeReferences()
    {
        var count = ComCall.AddRef(Pointer);
        ComCall.ReleaseRaw(Pointer);
        return count - 1;
    }

    public static T Of<T>(nint self)
        where T : FakeCom => (T)Objects[self];

    public void Dispose()
    {
        if (_self == null)
        {
            return;
        }

        Objects.TryRemove((nint)_self, out _);
        NativeMemory.Free(_self);
        NativeMemory.Free(_vtable);
        _self = null;
        _vtable = null;
        DisposeCore();
    }

    protected virtual void DisposeCore()
    {
    }

    protected void Set(int slot, void* function) => _vtable[slot] = (nint)function;

    private FakeCom Root => Owner ?? this;

    private int Increment() => Interlocked.Increment(ref Root._references);

    private int Decrement() => Interlocked.Decrement(ref Root._references);

    [UnmanagedCallersOnly]
    private static int NotImplemented(nint self) => ENotImpl;

    [UnmanagedCallersOnly]
    private static int QueryInterfaceStub(nint self, Guid* iid, nint* result)
    {
        var fake = Objects[self];
        *result = 0;
        if (*iid == IidIUnknown)
        {
            fake.Increment();
            *result = fake.Root.Pointer;
            return 0;
        }

        if (fake.Root.Interfaces.TryGetValue(*iid, out var face) || fake.Interfaces.TryGetValue(*iid, out face))
        {
            face.Increment();
            *result = face.Pointer;
            return 0;
        }

        return ScreenConstants.ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRefStub(nint self) => (uint)Objects[self].Increment();

    [UnmanagedCallersOnly]
    private static uint ReleaseStub(nint self) => (uint)Objects[self].Decrement();
}

/// <summary>IMFMediaBuffer over native memory (8 slots), optionally with IMFDXGIBuffer / IMF2DBuffer faces.</summary>
internal sealed unsafe class FakeMediaBuffer : FakeCom
{
    public FakeMediaBuffer(uint maxLength)
        : base(8)
    {
        MaxLength = maxLength;
        Memory = (byte*)NativeMemory.AllocZeroed(Math.Max(1u, maxLength));
        Set(3, (delegate* unmanaged<nint, byte**, uint*, uint*, int>)&Lock);
        Set(4, (delegate* unmanaged<nint, int>)&Unlock);
        Set(5, (delegate* unmanaged<nint, uint*, int>)&GetCurrentLength);
        Set(6, (delegate* unmanaged<nint, uint, int>)&SetCurrentLength);
        Set(7, (delegate* unmanaged<nint, uint*, int>)&GetMaxLength);
    }

    public byte* Memory { get; private set; }

    public uint MaxLength { get; set; }

    public uint CurrentLength { get; set; }

    public int LockDepth { get; private set; }

    public int Locks { get; private set; }

    public int SetLengthCalls { get; private set; }

    public int LockResult { get; set; }

    public int MaxLengthResult { get; set; }

    public Span<byte> Bytes => new(Memory, (int)MaxLength);

    protected override void DisposeCore()
    {
        NativeMemory.Free(Memory);
        Memory = null;
    }

    [UnmanagedCallersOnly]
    private static int Lock(nint self, byte** buffer, uint* maxLength, uint* currentLength)
    {
        var fake = Of<FakeMediaBuffer>(self);
        if (fake.LockResult < 0)
        {
            return fake.LockResult;
        }

        fake.LockDepth++;
        fake.Locks++;
        *buffer = fake.Memory;
        if (maxLength != null)
        {
            *maxLength = fake.MaxLength;
        }

        if (currentLength != null)
        {
            *currentLength = fake.CurrentLength;
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static int Unlock(nint self)
    {
        Of<FakeMediaBuffer>(self).LockDepth--;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int GetCurrentLength(nint self, uint* length)
    {
        *length = Of<FakeMediaBuffer>(self).CurrentLength;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int SetCurrentLength(nint self, uint length)
    {
        var fake = Of<FakeMediaBuffer>(self);
        fake.SetLengthCalls++;
        if (length > fake.MaxLength)
        {
            return ScreenConstants.EInvalidArg;
        }

        fake.CurrentLength = length;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int GetMaxLength(nint self, uint* length)
    {
        var fake = Of<FakeMediaBuffer>(self);
        if (fake.MaxLengthResult < 0)
        {
            return fake.MaxLengthResult;
        }

        *length = fake.MaxLength;
        return 0;
    }
}

/// <summary>The IMFDXGIBuffer face of a <see cref="FakeMediaBuffer"/> (GetResource = 3, GetSubresourceIndex = 4).</summary>
internal sealed unsafe class FakeDxgiBufferFace : FakeCom
{
    public FakeDxgiBufferFace(FakeCom owner, FakeTexture texture, uint subresource)
        : base(7)
    {
        Owner = owner;
        Texture = texture;
        Subresource = subresource;
        Set(3, (delegate* unmanaged<nint, Guid*, nint*, int>)&GetResource);
        Set(4, (delegate* unmanaged<nint, uint*, int>)&GetSubresourceIndex);
    }

    public FakeTexture Texture { get; }

    public uint Subresource { get; }

    [UnmanagedCallersOnly]
    private static int GetResource(nint self, Guid* iid, nint* resource)
    {
        var fake = Of<FakeDxgiBufferFace>(self);
        *resource = 0;
        if (*iid != ScreenConstants.IidID3D11Texture2D)
        {
            return ScreenConstants.ENoInterface;
        }

        ComCall.AddRef(fake.Texture.Pointer);
        *resource = fake.Texture.Pointer;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int GetSubresourceIndex(nint self, uint* subresource)
    {
        *subresource = Of<FakeDxgiBufferFace>(self).Subresource;
        return 0;
    }
}

/// <summary>The IMF2DBuffer face of a <see cref="FakeMediaBuffer"/> (GetContiguousLength = 7).</summary>
internal sealed unsafe class Fake2DBufferFace : FakeCom
{
    public Fake2DBufferFace(FakeCom owner, uint contiguousLength)
        : base(10)
    {
        Owner = owner;
        ContiguousLength = contiguousLength;
        Set(7, (delegate* unmanaged<nint, uint*, int>)&GetContiguousLength);
    }

    public uint ContiguousLength { get; set; }

    [UnmanagedCallersOnly]
    private static int GetContiguousLength(nint self, uint* length)
    {
        *length = Of<Fake2DBufferFace>(self).ContiguousLength;
        return 0;
    }
}

/// <summary>IMFSample (47 slots): one buffer, sample time and duration.</summary>
internal sealed unsafe class FakeSample : FakeCom
{
    public FakeSample()
        : base(47)
    {
        Set(36, (delegate* unmanaged<nint, long, int>)&SetSampleTime);
        Set(38, (delegate* unmanaged<nint, long, int>)&SetSampleDuration);
        Set(40, (delegate* unmanaged<nint, uint, nint*, int>)&GetBufferByIndex);
        Set(42, (delegate* unmanaged<nint, nint, int>)&AddBuffer);
    }

    public FakeMediaBuffer? Buffer { get; private set; }

    public long Time { get; private set; } = -1;

    public long Duration { get; private set; } = -1;

    [UnmanagedCallersOnly]
    private static int SetSampleTime(nint self, long time)
    {
        Of<FakeSample>(self).Time = time;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int SetSampleDuration(nint self, long duration)
    {
        Of<FakeSample>(self).Duration = duration;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int GetBufferByIndex(nint self, uint index, nint* buffer)
    {
        var fake = Of<FakeSample>(self);
        *buffer = 0;
        if (index != 0 || fake.Buffer is null)
        {
            return ScreenConstants.EInvalidArg;
        }

        ComCall.AddRef(fake.Buffer.Pointer);
        *buffer = fake.Buffer.Pointer;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int AddBuffer(nint self, nint buffer)
    {
        var fake = Of<FakeSample>(self);
        if (fake.Buffer is not null)
        {
            return ScreenConstants.EFail;
        }

        ComCall.AddRef(buffer);
        fake.Buffer = Of<FakeMediaBuffer>(buffer);
        return 0;
    }
}

/// <summary>ID3D11Texture2D (GetDesc = 10) with BGRA pixel memory of a padded row pitch (single subresource).</summary>
internal sealed unsafe class FakeTexture : FakeCom
{
    public FakeTexture(D3D11Texture2DDesc desc, int rowPadding = 64)
        : base(11)
    {
        Desc = desc;
        Pitch = ((int)desc.Width * 4) + rowPadding;
        Pixels = (byte*)NativeMemory.AllocZeroed((nuint)(Pitch * (int)Math.Max(1, desc.Height)));
        Set(10, (delegate* unmanaged<nint, D3D11Texture2DDesc*, void>)&GetDesc);
    }

    public D3D11Texture2DDesc Desc { get; }

    public int Pitch { get; }

    public byte* Pixels { get; private set; }

    public static D3D11Texture2DDesc Bgra(int width, int height, uint format = ScreenConstants.DxgiFormatB8G8R8A8Unorm) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDesc = new DxgiSampleDesc { Count = 1 },
        Usage = ScreenConstants.D3D11UsageDefault,
        BindFlags = ScreenConstants.D3D11BindRenderTarget | ScreenConstants.D3D11BindShaderResource,
    };

    public uint this[int x, int y]
    {
        get => ((uint*)(Pixels + ((long)y * Pitch)))[x];
        set => ((uint*)(Pixels + ((long)y * Pitch)))[x] = value;
    }

    /// <summary>Fills every pixel with <paramref name="pixel"/>(x, y).</summary>
    public void Fill(Func<int, int, uint> pixel)
    {
        for (var y = 0; y < Desc.Height; y++)
        {
            for (var x = 0; x < Desc.Width; x++)
            {
                this[x, y] = pixel(x, y);
            }
        }
    }

    protected override void DisposeCore()
    {
        NativeMemory.Free(Pixels);
        Pixels = null;
    }

    [UnmanagedCallersOnly]
    private static void GetDesc(nint self, D3D11Texture2DDesc* desc) => *desc = Of<FakeTexture>(self).Desc;
}

/// <summary>ID3D11Device (CreateTexture2D = 5): textures with optional initial data; QueryInterface answers nothing extra by default.</summary>
internal sealed unsafe class FakeDevice : FakeCom
{
    public FakeDevice()
        : base(48)
    {
        Set(5, (delegate* unmanaged<nint, D3D11Texture2DDesc*, D3D11SubresourceData*, nint*, int>)&CreateTexture2D);
    }

    public List<FakeTexture> Created { get; } = [];

    public int CreateResult { get; set; }

    protected override void DisposeCore()
    {
        foreach (var texture in Created)
        {
            texture.Dispose();
        }
    }

    [UnmanagedCallersOnly]
    private static int CreateTexture2D(nint self, D3D11Texture2DDesc* desc, D3D11SubresourceData* initial, nint* texture)
    {
        var fake = Of<FakeDevice>(self);
        *texture = 0;
        if (fake.CreateResult < 0)
        {
            return fake.CreateResult;
        }

        var created = new FakeTexture(*desc);
        if (initial != null)
        {
            for (var y = 0; y < desc->Height; y++)
            {
                new ReadOnlySpan<byte>((byte*)initial->SysMem + ((long)y * initial->SysMemPitch), (int)desc->Width * 4)
                    .CopyTo(new Span<byte>(created.Pixels + ((long)y * created.Pitch), (int)desc->Width * 4));
            }
        }

        fake.Created.Add(created);
        *texture = created.Pointer;
        return 0;
    }
}

/// <summary>ID3D11DeviceContext (Map = 14, Unmap = 15, CopySubresourceRegion = 46): copies pixels between fake textures.</summary>
internal sealed unsafe class FakeDeviceContext : FakeCom
{
    public FakeDeviceContext()
        : base(120)
    {
        Set(14, (delegate* unmanaged<nint, nint, uint, uint, uint, D3D11MappedSubresource*, int>)&Map);
        Set(15, (delegate* unmanaged<nint, nint, uint, void>)&Unmap);
        Set(46, (delegate* unmanaged<nint, nint, uint, uint, uint, uint, nint, uint, D3D11Box*, void>)&CopySubresourceRegion);
    }

    public int Maps { get; private set; }

    public int Unmaps { get; private set; }

    public int Copies { get; private set; }

    public D3D11Box? LastBox { get; private set; }

    public uint LastMapType { get; private set; }

    public uint LastDestinationSubresource { get; private set; }

    public int MapResult { get; set; }

    [UnmanagedCallersOnly]
    private static int Map(nint self, nint resource, uint subresource, uint mapType, uint flags, D3D11MappedSubresource* mapped)
    {
        var fake = Of<FakeDeviceContext>(self);
        if (fake.MapResult < 0)
        {
            return fake.MapResult;
        }

        var texture = Of<FakeTexture>(resource);
        fake.Maps++;
        fake.LastMapType = mapType;
        mapped->Data = texture.Pixels;
        mapped->RowPitch = (uint)texture.Pitch;
        mapped->DepthPitch = (uint)(texture.Pitch * (int)texture.Desc.Height);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static void Unmap(nint self, nint resource, uint subresource) => Of<FakeDeviceContext>(self).Unmaps++;

    [UnmanagedCallersOnly]
    private static void CopySubresourceRegion(nint self, nint destination, uint destinationSubresource, uint x, uint y, uint z, nint source, uint sourceSubresource, D3D11Box* box)
    {
        var fake = Of<FakeDeviceContext>(self);
        var to = Of<FakeTexture>(destination);
        var from = Of<FakeTexture>(source);
        fake.Copies++;
        fake.LastDestinationSubresource = destinationSubresource;
        var region = box != null ? *box : new D3D11Box { Right = from.Desc.Width, Bottom = from.Desc.Height, Back = 1 };
        fake.LastBox = box != null ? *box : null;
        var columns = (int)Math.Min(region.Right - region.Left, to.Desc.Width - x);
        var rows = (int)Math.Min(region.Bottom - region.Top, to.Desc.Height - y);
        for (var row = 0; row < rows; row++)
        {
            new ReadOnlySpan<byte>(from.Pixels + ((long)(region.Top + row) * from.Pitch) + (region.Left * 4), columns * 4)
                .CopyTo(new Span<byte>(to.Pixels + ((long)(y + row) * to.Pitch) + (x * 4), columns * 4));
        }
    }
}

/// <summary>Creates <see cref="FakeSample"/> + <see cref="FakeMediaBuffer"/> pairs for the memory sample pool and owns them.</summary>
internal sealed class FakeSampleFactory : Murmur.Capture.Windows.Screen.IMemorySampleFactory, IDisposable
{
    public List<FakeSample> Samples { get; } = [];

    public List<FakeMediaBuffer> Buffers { get; } = [];

    public int Result { get; set; }

    /// <summary>Extra references the "sample implementation" keeps on itself (calibration test).</summary>
    public int InternalSampleReferences { get; set; }

    public int Create(uint length, out nint sample, out nint buffer)
    {
        sample = buffer = 0;
        if (Result < 0)
        {
            return Result;
        }

        var s = new FakeSample();
        var b = new FakeMediaBuffer(length);
        Samples.Add(s);
        Buffers.Add(b);
        AttachBuffer(s, b); // the sample now holds one buffer reference, like IMFSample::AddBuffer
        for (var i = 0; i < InternalSampleReferences; i++)
        {
            ComCall.AddRef(s.Pointer);
        }

        sample = s.Pointer;
        buffer = b.Pointer;
        return 0;
    }

    public void Dispose()
    {
        foreach (var s in Samples)
        {
            s.Dispose();
        }

        foreach (var b in Buffers)
        {
            b.Dispose();
        }
    }

    private static void AttachBuffer(FakeSample sample, FakeMediaBuffer buffer) =>
        Assert.True(IMFSample.AddBuffer(sample.Pointer, buffer.Pointer) >= 0);
}

/// <summary>
/// IMFVideoSampleAllocatorEx (8 slots: UninitializeSampleAllocator = 4, AllocateSample = 6) handing out the queued
/// samples in order (MF_E_SAMPLEALLOCATOR_EMPTY when none is left), with an IMFVideoSampleAllocatorCallback face
/// (GetFreeSampleCount = 4) answering <see cref="FreeCount"/>.
/// </summary>
internal sealed unsafe class FakeVideoSampleAllocator : FakeCom
{
    public FakeVideoSampleAllocator(bool withCallback = true)
        : base(8)
    {
        Set(4, (delegate* unmanaged<nint, int>)&Uninitialize);
        Set(6, (delegate* unmanaged<nint, nint*, int>)&AllocateSample);
        if (withCallback)
        {
            Callback = new FakeAllocatorCallbackFace(this);
            Interfaces[ScreenConstants.IidIMFVideoSampleAllocatorCallback] = Callback;
        }
    }

    public FakeAllocatorCallbackFace? Callback { get; }

    public Queue<FakeSample> Available { get; } = new();

    public int FreeCount { get; set; }

    public int FreeCountResult { get; set; }

    public int AllocateCalls { get; private set; }

    public int Uninitialized { get; private set; }

    protected override void DisposeCore() => Callback?.Dispose();

    [UnmanagedCallersOnly]
    private static int Uninitialize(nint self)
    {
        Of<FakeVideoSampleAllocator>(self).Uninitialized++;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int AllocateSample(nint self, nint* sample)
    {
        var fake = Of<FakeVideoSampleAllocator>(self);
        fake.AllocateCalls++;
        *sample = 0;
        if (!fake.Available.TryDequeue(out var next))
        {
            return ScreenConstants.MfESampleAllocatorEmpty;
        }

        ComCall.AddRef(next.Pointer);
        *sample = next.Pointer;
        return 0;
    }
}

/// <summary>The IMFVideoSampleAllocatorCallback face of a <see cref="FakeVideoSampleAllocator"/>.</summary>
internal sealed unsafe class FakeAllocatorCallbackFace : FakeCom
{
    public FakeAllocatorCallbackFace(FakeVideoSampleAllocator owner)
        : base(5)
    {
        Owner = owner;
        Set(4, (delegate* unmanaged<nint, int*, int>)&GetFreeSampleCount);
    }

    [UnmanagedCallersOnly]
    private static int GetFreeSampleCount(nint self, int* samples)
    {
        var allocator = (FakeVideoSampleAllocator)Of<FakeAllocatorCallbackFace>(self).Owner!;
        if (allocator.FreeCountResult < 0)
        {
            return allocator.FreeCountResult;
        }

        *samples = allocator.FreeCount;
        return 0;
    }
}
