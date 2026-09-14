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
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace YAMDCC.Common.Monitoring;

/// <summary>
/// Publishes text into RivaTuner Statistics Server's on-screen display.
/// </summary>
/// <remarks>
/// <para>
/// RTSS reserves eight OSD slots in a shared memory block. Any application may
/// claim one, write its own text, and RTSS draws it as part of the game's
/// frame - which is the only way to appear over a game running in exclusive
/// fullscreen, where a top-most window cannot reach.
/// </para>
/// <para>
/// Crucially, this program does no injection of its own. RTSS is already
/// hooking the game; this just hands it text to draw. HWiNFO and CapFrameX use
/// the same interface for the same reason.
/// </para>
/// <para>
/// The slot is released on <see cref="Clear"/> so the text does not linger in
/// the overlay after this program stops using it.
/// </para>
/// </remarks>
public sealed class RtssOsd
{
    private const string MapName = "RTSSSharedMemoryV2";

    /// <summary>'SSTR' as stored, i.e. "RTSS" byte-reversed.</summary>
    private const uint Signature = 0x52545353;

    private const int TextBytes = 256;
    private const int OwnerBytes = 256;

    /// <summary>Identifies our slot so it is reused rather than duplicated.</summary>
    private readonly string _owner;

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Signature;
        public uint Version;
        public uint AppEntrySize;
        public uint AppArrOffset;
        public uint AppArrSize;
        public uint OsdEntrySize;
        public uint OsdArrOffset;
        public uint OsdArrSize;
        public uint OsdFrame;
    }

    /// <summary>Byte offset of the frame counter within the header.</summary>
    private const int OsdFrameOffset = 32;

    public RtssOsd(string owner = "msi_gf63_tuner")
    {
        _owner = owner;
    }

    /// <summary>
    /// <see langword="true"/> if RTSS is running and its shared memory was
    /// readable on the last attempt.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Writes text into our OSD slot. Lines are separated by newlines, and
    /// RTSS's own formatting tags may be used.
    /// </summary>
    public bool Write(string text) => Put(text ?? string.Empty, release: false);

    /// <summary>
    /// Releases our slot so the text disappears from the overlay.
    /// </summary>
    public void Clear() => Put(string.Empty, release: true);

    private bool Put(string text, bool release)
    {
        try
        {
            using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(
                MapName, MemoryMappedFileRights.ReadWrite);
            using MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(
                0, 0, MemoryMappedFileAccess.ReadWrite);

            acc.Read(0, out Header h);
            if (h.Signature != Signature || h.OsdArrSize == 0 || h.OsdEntrySize == 0)
            {
                Available = false;
                return false;
            }

            long slot = FindSlot(acc, h);
            if (slot < 0)
            {
                Available = true;
                return false;
            }

            byte[] textBuf = new byte[TextBytes];
            byte[] ownerBuf = new byte[OwnerBytes];

            if (!release)
            {
                Fill(textBuf, text, TextBytes);
                Fill(ownerBuf, _owner, OwnerBytes);
            }

            acc.WriteArray(slot, textBuf, 0, TextBytes);
            acc.WriteArray(slot + TextBytes, ownerBuf, 0, OwnerBytes);

            // RTSS only redraws when this counter changes
            acc.Write(OsdFrameOffset, h.OsdFrame + 1);

            Available = true;
            return true;
        }
        catch
        {
            // RTSS not running, or it withdrew the mapping mid-write
            Available = false;
            return false;
        }
    }

    /// <summary>
    /// Finds our existing slot, or the first free one.
    /// </summary>
    private long FindSlot(MemoryMappedViewAccessor acc, Header h)
    {
        long firstFree = -1;
        byte[] owner = new byte[OwnerBytes];

        for (uint i = 0; i < h.OsdArrSize; i++)
        {
            long off = h.OsdArrOffset + ((long)i * h.OsdEntrySize);
            acc.ReadArray(off + TextBytes, owner, 0, OwnerBytes);
            string name = Encoding.ASCII.GetString(owner).TrimEnd('\0');

            if (name == _owner)
            {
                return off;
            }
            if (name.Length == 0 && firstFree < 0)
            {
                firstFree = off;
            }
        }
        return firstFree;
    }

    private static void Fill(byte[] dest, string value, int max)
    {
        byte[] src = Encoding.ASCII.GetBytes(value);
        Array.Copy(src, dest, Math.Min(src.Length, max - 1));
    }

    /// <summary>
    /// Formats a snapshot into compact OSD lines.
    /// </summary>
    public static string Format(SensorSnapshot s, int fanPercent, int fanRpm)
    {
        if (s is null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();

        foreach (GpuReading g in s.Gpus)
        {
            sb.Append(g.Discrete ? "GPU " : "iGPU");
            if (g.TempC.HasValue) { sb.Append($" {g.TempC.Value,3:F0}C"); }
            sb.Append($" {g.LoadPercent,3:F0}%");
            if (g.CoreMHz.HasValue) { sb.Append($" {g.CoreMHz.Value,5:F0}MHz"); }
            if (g.Watts.HasValue) { sb.Append($" {g.Watts.Value,5:F1}W"); }
            sb.Append('\n');
        }

        sb.Append("CPU ");
        if (s.CpuTempC.HasValue) { sb.Append($" {s.CpuTempC.Value,3:F0}C"); }
        sb.Append($" {s.CpuLoadPercent,3:F0}%");
        if (s.CpuMHz > 0) { sb.Append($" {s.CpuMHz,5:F0}MHz"); }
        if (s.CpuPackageWatts.HasValue) { sb.Append($" {s.CpuPackageWatts.Value,5:F1}W"); }
        sb.Append('\n');

        sb.Append($"FAN  {fanPercent,3}% {fanRpm,5}RPM\n");
        sb.Append($"RAM  {s.RamUsedMB / 1024.0,5:F1}GB");

        return sb.ToString();
    }
}
