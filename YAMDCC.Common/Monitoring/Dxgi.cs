// This file is part of YAMDCC (Yet Another MSI Dragon Center Clone).
// Copyright © Sparronator9999 and Contributors 2023-2025.
//
// YAMDCC is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version.
//
// YAMDCC is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for
// more details.
//
// You should have received a copy of the GNU General Public License along with
// YAMDCC. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace YAMDCC.Common.Monitoring;

/// <summary>
/// Minimal DXGI interop: enumerates display adapters with their names, LUIDs
/// and dedicated video memory.
/// </summary>
/// <remarks>
/// The LUID is the link between an adapter and its Windows performance
/// counters: GPU Engine and GPU Adapter Memory instances are named
/// <c>..._luid_0xHHHHHHHH_0xLLLLLLLL_..."</c>. Without this, per-GPU load and
/// VRAM cannot be attributed to the right adapter - which matters on a hybrid
/// laptop with both an integrated and a discrete GPU.
/// </remarks>
internal static class Dxgi
{
    internal sealed class AdapterInfo
    {
        public string Name { get; set; }
        public string Luid { get; set; }

        /// <summary>PCI device ID, e.g. 0x5693 for an Arc A370M.</summary>
        public uint DeviceId { get; set; }

        /// <summary>On-board VRAM. Near zero for an integrated GPU.</summary>
        public long DedicatedVideoMemory { get; set; }

        /// <summary>
        /// System memory the adapter may borrow. This is the meaningful
        /// capacity for an integrated GPU, which has almost no VRAM of its own.
        /// </summary>
        public long SharedSystemMemory { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public int Revision;
        public IntPtr DedicatedVideoMemory;
        public IntPtr DedicatedSystemMemory;
        public IntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    // The placeholder methods below exist only to pad the vtable to the right
    // slot offsets; they are never called, so their signatures do not matter.
    // The ones that ARE called need [PreserveSig], otherwise the CLR treats the
    // int return as an HRESULT and injects an extra out-parameter, corrupting
    // the call.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIFactory
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIAdapter
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    /// <summary>
    /// Lists the display adapters DXGI reports. Returns an empty list if DXGI
    /// is unavailable, rather than throwing.
    /// </summary>
    internal static List<AdapterInfo> EnumAdapters()
    {
        List<AdapterInfo> list = [];
        IDXGIFactory1 factory = null;

        try
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory is null)
            {
                return list;
            }

            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1 adapter = null;
                try
                {
                    if (factory.EnumAdapters1(i, out adapter) != 0 || adapter is null)
                    {
                        break;
                    }

                    if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) != 0)
                    {
                        continue;
                    }

                    list.Add(new AdapterInfo
                    {
                        Name = desc.Description?.Trim(),
                        Luid = $"luid_0x{desc.AdapterLuid.HighPart:X8}_0x{desc.AdapterLuid.LowPart:X8}",
                        DeviceId = desc.DeviceId,
                        DedicatedVideoMemory = desc.DedicatedVideoMemory.ToInt64(),
                        SharedSystemMemory = desc.SharedSystemMemory.ToInt64(),
                    });
                }
                finally
                {
                    if (adapter is not null)
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
        }
        catch
        {
            // DXGI missing or refusing to enumerate: callers fall back to
            // showing no GPU rows rather than failing.
        }
        finally
        {
            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }

        return list;
    }
}
