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
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace YAMDCC.Common.Monitoring;

/// <summary>
/// One sensor row published by MSI Afterburner.
/// </summary>
public sealed class AbSensor
{
    /// <summary>Sensor name, e.g. <c>GPU1 core clock</c>.</summary>
    public string Name { get; set; }

    /// <summary>Unit string, e.g. <c>MHz</c>, <c>W</c>, <c>MB</c>.</summary>
    public string Units { get; set; }

    public float Value { get; set; }

    /// <summary>
    /// Adapter index this reading belongs to, or <c>0xFFFFFFFF</c> for a
    /// system-wide reading such as total CPU power.
    /// </summary>
    public uint GpuIndex { get; set; }
}

/// <summary>
/// Reads MSI Afterburner's hardware monitor table out of its shared memory.
/// </summary>
/// <remarks>
/// <para>
/// Afterburner publishes everything it measures into a memory-mapped file
/// named <c>MAHMSharedMemory</c>. Reading it needs no privileges and no driver
/// of our own, because Afterburner's <c>RTCore64</c> driver has already done
/// the ring-0 work. That is how the GPU core clock, GPU power and per-core CPU
/// temperatures become available - none of which Windows exposes on its own.
/// </para>
/// <para>
/// The catch is that this only works while Afterburner is actually running.
/// <see cref="Available"/> reports whether the table is there, and callers are
/// expected to fall back to <see cref="SystemSensors"/>'s driver-free readings
/// when it is not.
/// </para>
/// <para>
/// When RivaTuner Statistics Server is running too, Afterburner's table also
/// carries the current <c>Framerate</c>.
/// </para>
/// </remarks>
public sealed class AfterburnerSensors
{
    private const string MapName = "MAHMSharedMemory";

    // The header signature as it actually appears on disk. Accept both byte
    // orders rather than assuming: the obvious 'MAHM' spelling transposes to
    // 0x4D48414D, but Afterburner writes 0x4D41484D.
    private const uint Signature = 0x4D41484D;
    private const uint SignatureAlt = 0x4D48414D;
    private const int MaxPath = 260;

    // offsets within MAHM_SHARED_MEMORY_ENTRY: five MAX_PATH strings, then the
    // float payload
    private const int OffData = MaxPath * 5;
    private const int OffGpu = OffData + 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Signature;
        public uint Version;
        public uint HeaderSize;
        public uint NumEntries;
        public uint EntrySize;
        public int Time;
        public uint NumGpuEntries;
        public uint GpuEntrySize;
    }

    /// <summary><see langword="true"/> if the last read found the table.</summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Reads every sensor Afterburner currently publishes. Returns an empty
    /// list (and clears <see cref="Available"/>) when Afterburner is not
    /// running.
    /// </summary>
    public List<AbSensor> Read()
    {
        List<AbSensor> result = [];

        try
        {
            using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(
                MapName, MemoryMappedFileRights.Read);
            using MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(
                0, 0, MemoryMappedFileAccess.Read);

            acc.Read(0, out Header h);
            if ((h.Signature != Signature && h.Signature != SignatureAlt) ||
                h.NumEntries == 0 || h.EntrySize < OffGpu + 4)
            {
                Available = false;
                return result;
            }

            byte[] buf = new byte[h.EntrySize];
            for (uint i = 0; i < h.NumEntries; i++)
            {
                long off = h.HeaderSize + ((long)i * h.EntrySize);
                acc.ReadArray(off, buf, 0, buf.Length);

                string name = Str(buf, 0);
                if (name.Length == 0)
                {
                    continue;
                }

                result.Add(new AbSensor
                {
                    Name = name,
                    Units = Str(buf, MaxPath),
                    Value = BitConverter.ToSingle(buf, OffData),
                    GpuIndex = BitConverter.ToUInt32(buf, OffGpu),
                });
            }

            Available = result.Count > 0;
        }
        catch
        {
            // not running, or the table vanished mid-read
            Available = false;
        }

        return result;
    }

    private static string Str(byte[] buf, int offset)
    {
        int end = offset;
        int limit = Math.Min(offset + MaxPath, buf.Length);
        while (end < limit && buf[end] != 0)
        {
            end++;
        }
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    /// <summary>
    /// Finds a sensor for a given adapter, e.g. <c>("core clock", 0)</c> to get
    /// <c>GPU1 core clock</c>.
    /// </summary>
    /// <remarks>
    /// Afterburner names its rows <c>GPU1 ...</c>, <c>GPU2 ...</c> in adapter
    /// order, and tags each with the zero-based index in the same order, so the
    /// index is matched rather than the name.
    /// </remarks>
    public static float? FindGpu(List<AbSensor> sensors, uint gpuIndex, string suffix)
    {
        foreach (AbSensor s in sensors)
        {
            if (s.GpuIndex == gpuIndex &&
                s.Name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) &&
                s.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return s.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds a system-wide sensor by exact name, e.g. <c>CPU power</c>.
    /// </summary>
    public static float? Find(List<AbSensor> sensors, string name)
    {
        foreach (AbSensor s in sensors)
        {
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return s.Value;
            }
        }
        return null;
    }
}
