using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using WorkRouter.Abstractions;
using WorkRouter.Models;

namespace WorkRouter.Core.Networking;

/// <summary>
/// Fail-closed WFP policy for the WORK interface. Filters are added in a single
/// native transaction and are persistent, so a process crash cannot silently
/// turn a running hotspot into an unfiltered bridge.
/// </summary>
public sealed class WfpNetworkIsolation : INetworkIsolation
{
    private const uint ErrorSuccess = 0;
    private const uint RpcAuthnWinNt = 10;
    private const uint FilterPersistent = 0x00000001;
    private const uint FwpActionBlock = 0x00001001;
    private const uint FwpActionPermit = 0x00001002;
    private const uint FwpDataUint8 = 1;
    private const uint FwpDataUint16 = 2;
    private const uint FwpDataUint32 = 3;
    private const uint FwpDataUint64 = 4;
    private const uint FwpDataV4AddrMask = 0x100;
    private const uint FwpDataV6AddrMask = 0x101;
    private const uint FwpMatchEqual = 0;
    private const uint FwpFilterNotFound = 0x80320003;

    // A dedicated high-weight sublayer ensures our terminating block filters
    // are evaluated before permissive third-party/Windows Firewall rules.
    private static readonly Guid WorkRouterSublayer = new("9e8d9f6b-4f64-4f35-a7de-9a02b57e00a1");
    private static readonly Guid LayerInboundIpV4 = new("c86fd1bf-21cd-497e-a0bb-17425c885c58");
    private static readonly Guid LayerInboundIpV6 = new("f52032cb-991c-46e7-971d-2601459a91ca");
    private static readonly Guid LayerIpForwardV4 = new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4");
    private static readonly Guid LayerIpForwardV6 = new("7b964818-19c7-493a-b71f-832c3684d28c");
    private static readonly Guid LayerInboundTransportV4 = new("5926dfc8-e3cf-4426-a283-dc393f5d0f9d");
    private static readonly Guid LayerInboundTransportV6 = new("634a869f-fc23-4b90-b0c1-bf620a36ae6f");
    private static readonly Guid ConditionInterfaceIndex = new("667fd755-d695-434a-8af5-d3835a1259bc");
    private static readonly Guid ConditionSourceInterfaceIndex = new("2311334d-c92d-45bf-9496-edf447820e2d");
    private static readonly Guid ConditionDestinationAddress = new("2d79133b-b390-45c6-8699-acaceaafed33");
    private static readonly Guid ConditionLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid ConditionProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid ConditionLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");

    private readonly object _sync = new();
    private readonly HashSet<Guid> _filterKeys = new();
    private readonly HashSet<Guid> _quarantineKeys = new();
    private IntPtr _engine;
    private bool _active;
    private int _interfaceIndex;
    private bool _disposed;

    public async Task EnterQuarantineAsync(IReadOnlyList<int> interfaceIndexes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(interfaceIndexes);
        await Task.Run(() => EnterQuarantineCore(interfaceIndexes, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateAsync(NetworkTopology topology, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(topology);
        await Task.Run(() => ActivateCore(topology, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<IsolationHealth> InspectAsync(NetworkTopology? topology, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_engine == IntPtr.Zero || _filterKeys.Count == 0)
                return Task.FromResult(new IsolationHealth(false, false, false, false, "WFP policy is not active."));
            var present = _filterKeys.All(FilterExists);
            var v4 = present && _active;
            var v6 = present && _active;
            return Task.FromResult(new IsolationHealth(_active && present, present, v4, v6,
                _active && present ? $"WFP filters active on interface {_interfaceIndex}." : "WFP filter set is incomplete."));
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await Task.Run(() => RemoveCore(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        try { RemoveCore(CancellationToken.None); }
        catch { /* disposal is best effort; policy remains blocking if native teardown fails */ }
        return ValueTask.CompletedTask;
    }

    private void EnterQuarantineCore(IReadOnlyList<int> interfaceIndexes, CancellationToken cancellationToken)
    {
        var indexes = interfaceIndexes.Where(i => i > 0).Distinct().ToArray();
        if (indexes.Length == 0)
            throw new ArgumentException("At least one hotspot interface index is required.", nameof(interfaceIndexes));
        lock (_sync)
        {
            EnsureEngine();
            var previousKeys = EnumerateOwnedFilterKeys();
            BeginTransaction();
            try
            {
                // A prior service process may have crashed while persistent filters
                // were installed. Replace the entire owned set atomically so restart
                // recovery never leaves an unknown policy behind or opens a gap.
                foreach (var key in previousKeys)
                    DeleteNativeByKey(key);
                _filterKeys.Clear();
                _quarantineKeys.Clear();
                foreach (var index in indexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ipv4Key = StableKey(index, LayerInboundIpV4, "quarantine");
                    var ipv6Key = StableKey(index, LayerInboundIpV6, "quarantine");
                    AddFilter(CreateFilter(
                        ipv4Key, LayerInboundIpV4,
                        FwpActionBlock, Condition(ConditionInterfaceIndex, Uint32((uint)index))));
                    AddFilter(CreateFilter(
                        ipv6Key, LayerInboundIpV6,
                        FwpActionBlock, Condition(ConditionInterfaceIndex, Uint32((uint)index))));
                    _quarantineKeys.Add(ipv4Key);
                    _quarantineKeys.Add(ipv6Key);
                }
                CommitTransaction();
                _active = false;
            }
            catch
            {
                AbortTransaction();
                RefreshTrackedKeys();
                throw;
            }
        }
    }

    private void ActivateCore(NetworkTopology topology, CancellationToken cancellationToken)
    {
        if (topology.InterfaceIndex <= 0 || topology.GatewayAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !topology.WorkNetwork.IsIPv4)
            throw new ArgumentException("A valid IPv4 WORK topology is required.", nameof(topology));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureEngine();
            BeginTransaction();
            try
            {
                AddFilter(CreateFilter(StableKey(topology.InterfaceIndex, LayerInboundTransportV4, "desktop-block"), LayerInboundTransportV4,
                    FwpActionBlock, Condition(ConditionInterfaceIndex, Uint32((uint)topology.InterfaceIndex))));
                AddFilter(CreateFilter(StableKey(topology.InterfaceIndex, LayerInboundTransportV6, "ipv6-block"), LayerInboundTransportV6,
                    FwpActionBlock, Condition(ConditionInterfaceIndex, Uint32((uint)topology.InterfaceIndex))));

                // SMB is the sole host service exception. Higher weight makes this permit
                // win over the interface-wide transport block at the same layer.
                AddFilter(CreateFilter(StableKey(topology.InterfaceIndex, LayerInboundTransportV4, "smb-permit"), LayerInboundTransportV4,
                    FwpActionPermit,
                    new[]
                    {
                        Condition(ConditionInterfaceIndex, Uint32((uint)topology.InterfaceIndex)),
                        V4MaskCondition(ConditionLocalAddress, IpNetwork.FromAddress(topology.GatewayAddress, 32)),
                        Condition(ConditionProtocol, Uint8(6)),
                        Condition(ConditionLocalPort, Uint16(445)),
                    }, 100));

                // ICS owns DHCP and DNS on the hotspot gateway. These are the only
                // additional host-bound exceptions needed for a client to obtain
                // an address and resolve names; all other host ports stay blocked.
                AddHostPortPermit(topology.InterfaceIndex, "dns-udp", 17, 53);
                AddHostPortPermit(topology.InterfaceIndex, "dns-tcp", 6, 53);
                AddHostPortPermit(topology.InterfaceIndex, "dhcp-udp", 17, 67);

                foreach (var network in NetworkAddressing.GetBlockedIpv4Ranges(topology.WorkNetwork))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddFilter(CreateFilter(StableKey(topology.InterfaceIndex, LayerIpForwardV4, network.ToString()), LayerIpForwardV4,
                        FwpActionBlock,
                        Condition(ConditionSourceInterfaceIndex, Uint32((uint)topology.InterfaceIndex)),
                        V4MaskCondition(ConditionDestinationAddress, network)));
                }

                // IPv6 forwarding is disabled completely in v1; no alternate route to HOME
                // may appear if an adapter later gains a global IPv6 address.
                AddFilter(CreateFilter(StableKey(topology.InterfaceIndex, LayerIpForwardV6, "all"), LayerIpForwardV6,
                    FwpActionBlock, Condition(ConditionSourceInterfaceIndex, Uint32((uint)topology.InterfaceIndex))));
                CommitTransaction();

                // Delete quarantine only after all production filters committed.
                BeginTransaction();
                try
                {
                    var quarantineKeys = _quarantineKeys.ToArray();
                    foreach (var key in quarantineKeys)
                        DeleteNativeByKey(key);
                    CommitTransaction();
                    foreach (var key in quarantineKeys)
                    {
                        _filterKeys.Remove(key);
                        _quarantineKeys.Remove(key);
                    }
                }
                catch
                {
                    AbortTransaction();
                    RefreshTrackedKeys();
                    throw;
                }

                _interfaceIndex = topology.InterfaceIndex;
                _active = true;
            }
            catch
            {
                AbortTransaction();
                RefreshTrackedKeys();
                _active = false;
                // Do not remove quarantine here. A failed activation must remain blocked.
                throw;
            }
        }
    }

    private void RemoveCore(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_engine == IntPtr.Zero)
            {
                // Cleanup must also work after a service restart, when the in-memory
                // key set is empty but persistent filters from the previous process
                // still exist in BFE.
                EnsureEngine();
                foreach (var key in EnumerateOwnedFilterKeys())
                    _filterKeys.Add(key);
            }
            BeginTransaction();
            try
            {
                foreach (var key in _filterKeys.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DeleteNativeByKey(key);
                }
                CommitTransaction();
                _filterKeys.Clear();
                _quarantineKeys.Clear();
                _active = false;
                _interfaceIndex = 0;
            }
            catch
            {
                AbortTransaction();
                throw;
            }
            finally
            {
                var subLayerKey = WorkRouterSublayer;
                _ = FwpmSubLayerDeleteByKey0(_engine, ref subLayerKey);
                FwpmEngineClose0(_engine);
                _engine = IntPtr.Zero;
            }
        }
    }

    private void EnsureEngine()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WFP isolation is available only on Windows.");
        if (_engine != IntPtr.Zero)
        {
            // The BFE service (or another owner) can remove our persistent
            // sub-layer while this process still holds a valid engine handle.
            // Re-assert it on every operation; otherwise the next
            // FwpmFilterAdd0 returns FWP_E_SUBLAYER_NOT_FOUND (0x80320007)
            // and a fail-closed watchdog trip cannot be recovered without a
            // full Stop/Start cycle that happens to close this handle.
            EnsureSubLayer();
            return;
        }
        var status = FwpmEngineOpen0(null, RpcAuthnWinNt, IntPtr.Zero, IntPtr.Zero, out _engine);
        if (status != ErrorSuccess)
        {
            _engine = IntPtr.Zero;
            throw new Win32Exception((int)status, "FwpmEngineOpen0 failed; administrator rights are required.");
        }
        EnsureSubLayer();
    }

    private void EnsureSubLayer()
    {
        var name = Marshal.StringToHGlobalUni("WorkRouter isolation");
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<FwpmSubLayer>());
        try
        {
            var native = new FwpmSubLayer
            {
                SubLayerKey = WorkRouterSublayer,
                DisplayData = new FwpmDisplayData { Name = name, Description = IntPtr.Zero },
                Flags = FilterPersistent,
                ProviderKey = IntPtr.Zero,
                ProviderData = default,
                Weight = ushort.MaxValue,
            };
            Marshal.StructureToPtr(native, ptr, false);
            var status = FwpmSubLayerAdd0(_engine, ptr, IntPtr.Zero);
            if (status is not (ErrorSuccess or 0x80320009)) // FWP_E_ALREADY_EXISTS
                ThrowNative(status, "FwpmSubLayerAdd0");
        }
        finally
        {
            Marshal.DestroyStructure<FwpmSubLayer>(ptr);
            Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(name);
        }
    }

    private void BeginTransaction() => ThrowNative(FwpmTransactionBegin0(_engine), "FwpmTransactionBegin0");
    private void CommitTransaction() => ThrowNative(FwpmTransactionCommit0(_engine), "FwpmTransactionCommit0");
    private void AbortTransaction() { _ = FwpmTransactionAbort0(_engine); }

    private void AddFilter(FilterMemory filter)
    {
        try
        {
            ThrowNative(FwpmFilterAdd0(_engine, filter.Filter, IntPtr.Zero, out _), "FwpmFilterAdd0");
            _filterKeys.Add(filter.Key);
        }
        finally
        {
            filter.Dispose();
        }
    }

    private void DeleteNativeByKey(Guid key)
    {
        var status = FwpmFilterDeleteByKey0(_engine, ref key);
        if (status is not (ErrorSuccess or FwpFilterNotFound))
            ThrowNative(status, "FwpmFilterDeleteByKey0");
    }

    private bool FilterExists(Guid key)
    {
        var status = FwpmFilterGetByKey0(_engine, ref key, out var ptr);
        if (ptr != IntPtr.Zero)
            FwpmFreeMemory0(ref ptr);
        return status == ErrorSuccess;
    }

    private void AddHostPortPermit(int interfaceIndex, string name, byte protocol, ushort port)
        => AddFilter(CreateFilter(StableKey(interfaceIndex, LayerInboundTransportV4, name), LayerInboundTransportV4,
            FwpActionPermit,
            new[]
            {
                Condition(ConditionInterfaceIndex, Uint32((uint)interfaceIndex)),
                Condition(ConditionProtocol, Uint8(protocol)),
                Condition(ConditionLocalPort, Uint16(port)),
            }, 100));

    private IReadOnlyList<Guid> EnumerateOwnedFilterKeys()
    {
        ThrowNative(FwpmFilterCreateEnumHandle0(_engine, IntPtr.Zero, out var enumHandle), "FwpmFilterCreateEnumHandle0");
        var keys = new List<Guid>();
        try
        {
            while (true)
            {
                ThrowNative(FwpmFilterEnum0(_engine, enumHandle, 128, out var entries, out var returned), "FwpmFilterEnum0");
                try
                {
                    for (var index = 0; index < returned; index++)
                    {
                        var filterPointer = Marshal.ReadIntPtr(entries, checked(index * IntPtr.Size));
                        var identity = Marshal.PtrToStructure<FwpmFilterIdentity>(filterPointer);
                        if (identity.SubLayerKey == WorkRouterSublayer)
                            keys.Add(identity.FilterKey);
                    }
                }
                finally
                {
                    if (entries != IntPtr.Zero)
                        FwpmFreeMemory0(ref entries);
                }
                if (returned < 128)
                    break;
            }
        }
        finally
        {
            _ = FwpmFilterDestroyEnumHandle0(_engine, enumHandle);
        }
        return keys;
    }

    private void RefreshTrackedKeys()
    {
        _filterKeys.Clear();
        foreach (var key in EnumerateOwnedFilterKeys())
            _filterKeys.Add(key);
    }

    private static FilterMemory CreateFilter(Guid key, Guid layer, uint action, params FilterCondition[] conditions)
        => CreateFilter(key, layer, action, conditions, weight: 10);

    private static FilterMemory CreateFilter(Guid key, Guid layer, uint action, FilterCondition[] conditions, ulong weight)
    {
        var name = Marshal.StringToHGlobalUni($"WorkRouter {key:N}");
        var values = new List<IntPtr>();
        var conditionArray = Marshal.AllocHGlobal(Marshal.SizeOf<FwpmFilterCondition>() * conditions.Length);
        try
        {
            for (var i = 0; i < conditions.Length; i++)
            {
                values.AddRange(conditions[i].Allocations);
                Marshal.StructureToPtr(conditions[i].Native, conditionArray + i * Marshal.SizeOf<FwpmFilterCondition>(), false);
            }
            var native = new FwpmFilter
            {
                FilterKey = key,
                DisplayData = new FwpmDisplayData { Name = name, Description = IntPtr.Zero },
                Flags = FilterPersistent,
                ProviderKey = IntPtr.Zero,
                ProviderData = default,
                LayerKey = layer,
                SubLayerKey = WorkRouterSublayer,
                Weight = FwpValue.Uint64(weight, values),
                NumberOfFilterConditions = (uint)conditions.Length,
                FilterCondition = conditionArray,
                Action = new FwpmAction { Type = action, FilterType = Guid.Empty },
                RawContext = 0,
                Reserved = IntPtr.Zero,
                FilterId = 0,
                EffectiveWeight = default,
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<FwpmFilter>());
            Marshal.StructureToPtr(native, ptr, false);
            return new FilterMemory(key, ptr, conditionArray, name, values);
        }
        catch
        {
            foreach (var p in values) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(conditionArray);
            Marshal.FreeHGlobal(name);
            throw;
        }
    }

    private static FilterCondition Condition(Guid field, FwpValue value)
    {
        var allocations = new List<IntPtr>();
        var native = new FwpmFilterCondition { FieldKey = field, MatchType = FwpMatchEqual, ConditionValue = value.ToConditionValue(allocations) };
        return new FilterCondition(native, allocations);
    }

    private static FilterCondition V4MaskCondition(Guid field, IpNetwork network)
    {
        if (!network.IsIPv4)
            throw new ArgumentException("An IPv4 network is required.", nameof(network));
        var address = network.NetworkAddress.GetAddressBytes();
        var addressValue = (uint)(address[0] << 24 | address[1] << 16 | address[2] << 8 | address[3]);
        var mask = network.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - network.PrefixLength);
        var pointer = Marshal.AllocHGlobal(8);
        Marshal.WriteInt32(pointer, unchecked((int)addressValue));
        Marshal.WriteInt32(pointer + 4, unchecked((int)mask));
        return new FilterCondition(
            new FwpmFilterCondition
            {
                FieldKey = field,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue { Type = FwpDataV4AddrMask, Pointer = pointer }
            },
            new[] { pointer });
    }

    private static FwpValue Uint8(byte value) => new() { Type = FwpDataUint8, UInt32 = value };
    private static FwpValue Uint16(ushort value) => new() { Type = FwpDataUint16, UInt32 = value };
    private static FwpValue Uint32(uint value) => new() { Type = FwpDataUint32, UInt32 = value };

    private static Guid StableKey(int index, Guid layer, string suffix)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"WorkRouter\0{index}\0{layer:D}\0{suffix}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void ThrowNative(uint status, string api)
    {
        if (status != ErrorSuccess)
            throw new Win32Exception((int)status, $"{api} failed (0x{status:X8}).");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WfpNetworkIsolation));
    }

    private readonly record struct FilterCondition(FwpmFilterCondition Native, IReadOnlyList<IntPtr> Allocations);

    private sealed class FilterMemory : IDisposable
    {
        public FilterMemory(Guid key, IntPtr filter, IntPtr conditions, IntPtr name, IReadOnlyList<IntPtr> values)
            => (Key, Filter, Conditions, Name, Values) = (key, filter, conditions, name, values);
        public Guid Key { get; }
        public IntPtr Filter { get; }
        public IntPtr Conditions { get; }
        public IntPtr Name { get; }
        public IReadOnlyList<IntPtr> Values { get; }
        public void Dispose()
        {
            Marshal.DestroyStructure<FwpmFilter>(Filter);
            Marshal.FreeHGlobal(Filter);
            Marshal.FreeHGlobal(Conditions);
            Marshal.FreeHGlobal(Name);
            foreach (var p in Values) Marshal.FreeHGlobal(p);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FwpmDisplayData { public IntPtr Name; public IntPtr Description; }
    [StructLayout(LayoutKind.Sequential)] private struct FwpByteBlob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct FwpmAction { public uint Type; public Guid FilterType; }
    [StructLayout(LayoutKind.Sequential)] private struct FwpmSubLayer
    {
        public Guid SubLayerKey;
        public FwpmDisplayData DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public ushort Weight;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FwpmFilterCondition { public Guid FieldKey; public uint MatchType; public FwpConditionValue ConditionValue; }
    [StructLayout(LayoutKind.Sequential)] private struct FwpmFilter
    {
        public Guid FilterKey;
        public FwpmDisplayData DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public FwpValue Weight;
        public uint NumberOfFilterConditions;
        public IntPtr FilterCondition;
        public FwpmAction Action;
        public ulong RawContext;
        public IntPtr Reserved;
        public ulong FilterId;
        public FwpValue EffectiveWeight;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FwpmFilterIdentity
    {
        public Guid FilterKey;
        public FwpmDisplayData DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
    }
    [StructLayout(LayoutKind.Explicit)] private struct FwpValue
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public ulong UInt64;
        [FieldOffset(8)] public IntPtr Pointer;
        public static FwpValue Uint64(ulong value, ICollection<IntPtr> allocations)
        {
            var p = Marshal.AllocHGlobal(sizeof(ulong)); Marshal.WriteInt64(p, unchecked((long)value)); allocations.Add(p);
            return new FwpValue { Type = FwpDataUint64, Pointer = p };
        }
        public FwpConditionValue ToConditionValue(ICollection<IntPtr> allocations)
        {
            return new FwpConditionValue { Type = Type, UInt32 = UInt32, Pointer = Pointer };
        }
    }
    [StructLayout(LayoutKind.Explicit)] private struct FwpConditionValue
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public byte UInt8;
        [FieldOffset(8)] public ushort UInt16;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmEngineOpen0([MarshalAs(UnmanagedType.LPWStr)] string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmEngineClose0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags = 0);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmTransactionCommit0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmTransactionAbort0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterAdd0(IntPtr engineHandle, IntPtr filter, IntPtr securityDescriptor, out ulong id);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterDeleteByKey0(IntPtr engineHandle, ref Guid key);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterGetByKey0(IntPtr engineHandle, ref Guid key, out IntPtr filter);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterCreateEnumHandle0(IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterEnum0(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, IntPtr subLayer, IntPtr securityDescriptor);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);
}
