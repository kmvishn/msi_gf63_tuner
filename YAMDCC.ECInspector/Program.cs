// This file is part of YAMDCC (Yet Another MSI Dragon Center Clone).
// Copyright © Sparronator9999 and Contributors 2025.
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
using System.Globalization;
using System.Threading;
using YAMDCC.Common;
using YAMDCC.ECAccess;

namespace YAMDCC.ECInspector;

internal static class Program
{
    private static readonly EC _EC = new();

    private static int Main(string[] args)
    {
        if (!Utils.IsAdmin())
        {
            Console.Error.WriteLine("ERROR: admin privileges required");
            return 255;
        }

        if (args.Length > 0 && args[0].Length > 0)
        {
            switch (args[0].ToLowerInvariant()[0])
            {
                case 'v':
                    Console.WriteLine(Utils.GetVerString());
                    return 0;
                case 'h':
                    break;
                case 'd':
                    return DumpEC(1000, 1) ? 0 : 1;
                case 'm':
                    return DumpEC(1000, -1) ? 0 : 1;
                case 'r':
                    if (args.Length >= 2)
                    {
                        return ParseNum(args[1], out byte reg)
                            ? ReadEC(reg) ? 0 : 1
                            : 3;
                    }
                    // we do a little string deduping
                    Console.Error.WriteLine($"ERROR: not enough arguments (expected {2})");
                    return 2;
                case 'w':
                    if (args.Length >= 3)
                    {
                        return ParseNum(args[1], out byte reg) &&
                            ParseNum(args[2], out byte val)
                                ? WriteEC(reg, val) ? 0 : 1
                                : 3;
                    }
                    Console.Error.WriteLine($"ERROR: not enough arguments (expected {3})");
                    return 2;
                default:
                    Console.Error.WriteLine($"ERROR: unknown command: {args[0]}");
                    break;
            }
        }
        else
        {
            Console.Error.WriteLine("ERROR: no command specified");
        }
        Help();
        return 1;
    }

    private static void Help()
    {
        Console.WriteLine("\nYAMDCC EC inspection utility\n\n" +
            $"OS version: {Environment.OSVersion}\n" +
            $"App version: {Utils.GetVerString()}\n" +
            $"Revision (git): {Utils.GetRevision()}\n\n" +
            $"Usage: {AppDomain.CurrentDomain.FriendlyName} <command> [<args>]\n\n" +
            "Commands:\n" +
            "  help                  Print this help screen\n" +
            "  version               Print the program version\n" +
            "  dump                  Dump all EC registers\n" +
            "  monitor               Dump EC and monitor for changes\n" +
            "  read <reg>            Read EC register <reg> and print its value\n" +
            "  write <reg> <val>     Write <val> to EC register <reg>");
    }

    private static bool DumpEC(int interval, int loops)
    {
        if (!_EC.LoadDriver())
        {
            return false;
        }

        ECValue[] values = new ECValue[256];

        Console.Clear();
        Console.SetCursorPosition(0, 0);

        // write heading
        Console.Write("YAMDCC EC inspector\n\n 0x |");
        for (int i = 0; i < 16; i++)
        {
            Console.Write($" 0{i:X}");
        }
        Console.WriteLine("\n----|".PadRight(55, '-'));

        for (int i = 0; i < 16; i++)
        {
            Console.WriteLine($" {i:X}0 |");
        }

        Console.WriteLine("Press Ctrl+C to exit");
        Console.CursorVisible = false;
        Console.CancelKeyPress += new ConsoleCancelEventHandler(CancelKey);

        for (int j = 0; loops == -1 || j < loops; j++)
        {
            // TODO: optimise out jumping all over the place?
            // (leftover from when we used YAMDCC service for EC access)
            for (int i = 0; i < values.Length; i++)
            {
                if (_EC.ReadByte((byte)i, out byte value))
                {
                    int loBits = i & 0x0F,
                        hiBits = (i & 0xF0) >> 4;

                    // keep the default console colour in case it was
                    // changed with e.g. the `color` command
                    ConsoleColor original = Console.ForegroundColor;

                    // write hex value
                    Console.SetCursorPosition(6 + loBits * 3, 4 + hiBits);

                    if (values[i].Value == value)
                    {
                        values[i].Age++;
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                    }
                    else
                    {
                        values[i].Value = value;
                        values[i].Age = 0;
                        Console.ForegroundColor = ConsoleColor.Green;
                    }

                    if (value == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                    }
                    Console.Write($"{value:X2}");

                    // write string representation
                    Console.SetCursorPosition(55 + loBits, 4 + hiBits);
                    if (value < 32 || value > 126)
                    {
                        // unprintable non-extended ASCII char
                        Console.Write('.');
                    }
                    else
                    {
                        Console.Write((char)value);
                    }

                    // restore console colour
                    Console.ForegroundColor = original;
                }
            }

            Thread.Sleep(interval);
        }

        Console.CursorVisible = true;
        _EC?.UnloadDriver();
        return true;
    }

    private static bool ReadEC(byte reg)
    {
        if (!_EC.LoadDriver())
        {
            return false;
        }

        bool success = _EC.ReadByte(reg, out byte val);
        if (success)
        {
            Console.WriteLine($"0x{val:X2} ({val})");
        }
        else
        {
            Console.Error.WriteLine($"ERROR: Failed to read EC register 0x{reg:X2} ({reg})!");
        }
        _EC?.UnloadDriver();
        return success;
    }

    private static bool WriteEC(byte reg, byte val)
    {
        if (!_EC.LoadDriver())
        {
            return false;
        }

        bool success = _EC.WriteByte(reg, val);
        if (success)
        {
            Console.WriteLine($"Wrote 0x{val:X2} ({val}) to 0x{reg:X2} ({reg})");
        }
        else
        {
            Console.Error.WriteLine($"ERROR: failed to write 0x{val:X2} ({val}) to EC register 0x{reg:X2} ({reg})!");
        }
        _EC?.UnloadDriver();
        return success;
    }

    private static bool ParseNum(string str, out byte val)
    {
        // try parsing as hex value if prefixed with "0x"
        bool success = str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(str.Substring(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out val)
        // otherwise parse as normal number
            : byte.TryParse(str, out val);

        if (!success)
        {
            Console.Error.WriteLine($"ERROR: failed to parse value: {str}");
        }
        return success;
    }

    private static void CancelKey(object sender, ConsoleCancelEventArgs e)
    {
        Console.CursorVisible = true;
        _EC?.UnloadDriver();
    }

    private struct ECValue
    {
        /// <summary>
        /// The EC value itself.
        /// </summary>
        public int Value;

        /// <summary>
        /// How many EC polls it's been since <see cref="Value"/> was last updated.
        /// </summary>
        public int Age;
    }
}
