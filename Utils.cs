// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Runtime.InteropServices;

public class Utils {

    /// <summary>
    /// Grabs a 32 bit hex value from standard input
    /// </summary>		
    public static UInt32 ParseHexa( CommandMode inCommand, string inString ) {

        string iLower = inString.ToLowerInvariant();
        iLower = iLower.Replace( inCommand.command().ToLowerInvariant(), "" );

#if DebugArgs
        Console.WriteLine( "Parsing hexa " + inString );
#endif

        // Whatever's left should be the address
        UInt32 outAddr = (UInt32)Convert.ToInt32( iLower, 16 );

        Console.Write( " - Parsed hexa: 0x" + outAddr.ToString( "X8" ) + "\n" );

        return outAddr;

    }

    /// <summary>
    /// Red console text + returns false; a standard error response.
    /// </summary>	
    /// <returns>returns false so you can do return Error("blah");</returns>
    public static bool Error( string inString, bool printHeader = true ) {

        if ( printHeader )
            Program.PrintUsage( false );

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write( "\n\n" );
        Console.Write( "ERROR! " + inString + " \n \n " );

        // Leaves the user with a green console.
        // Because we can. Shh, don't tell them till they get it right.
        Console.ForegroundColor = ConsoleColor.Green;

        return false;

    }

    public static PlatformID realPlatform {
        get {

            PlatformID deviceID = Environment.OSVersion.Platform;

            // Bug:
            // Mono always returns Unix (even for version numbers) when running on OSX
            // even though the correct enum exists.

            if ( Directory.Exists( "/Applications" ) & Directory.Exists( "/Volumes" ) )
                deviceID = PlatformID.MacOSX;

            return deviceID;

        }
    }

    private static bool hasCachedDefaultConsoleColour = false;
    private static ConsoleColor originalColour = ConsoleColor.Black;

    /// <summary>
    /// Windows: sets the default console colour
    /// Mac: the terminal might be white, so cache whatever's already there
    /// </summary>    
    public static void SetDefaultColour() {

        if ( !hasCachedDefaultConsoleColour ) {
            hasCachedDefaultConsoleColour = true;
            originalColour = Console.ForegroundColor;
        }

        Console.ForegroundColor = originalColour;

    }

    public static TimeSpan GetSpan() {
        return (DateTime.UtcNow - new DateTime( 1970, 1, 1 )); // shortest way to represent the epoch?
    }

    #region virtual processing

    [DllImport( "kernel32.dll", SetLastError = true )]
    private static extern bool SetConsoleMode( IntPtr hConsoleHandle, uint dwMode );

    [DllImport( "kernel32.dll", SetLastError = true )]
    static extern bool GetConsoleMode( IntPtr hConsoleHandle, out uint lpMode );

    [DllImport( "kernel32.dll", SetLastError = true )]
    static extern IntPtr GetStdHandle( int nStdHandle );

    [Flags]
    private enum ConsoleOutputModes : uint {
        ENABLE_PROCESSED_OUTPUT = 0x0001,
        ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002,
        ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004,
        DISABLE_NEWLINE_AUTO_RETURN = 0x0008,
        ENABLE_LVB_GRID_WORLDWIDE = 0x0010
    }

    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const int INVALID_HANDLE_VALUE = -1;

    public static void ApplyConsoleMode() {
        IntPtr consoleHandle = GetStdHandle( STD_OUTPUT_HANDLE );
        if ( consoleHandle.ToInt32() == -INVALID_HANDLE_VALUE ) {
            Console.WriteLine( "Error: invalid stdout handle value" );  
            return;
        }

        UInt32 mode = 0;
        if ( !GetConsoleMode( consoleHandle, out mode ) ) {
            Console.WriteLine( "Error: couldn't read console mode..." );
            return;
        }

        mode |= (UInt32)ConsoleOutputModes.ENABLE_VIRTUAL_TERMINAL_PROCESSING;

        SetConsoleMode( consoleHandle, mode );

        Console.WriteLine( $"Applied console mode: 0x{mode.ToString( "X" )}" );

    }

    #endregion virtual processing

}

