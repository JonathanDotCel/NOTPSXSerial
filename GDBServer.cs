// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

//
// GDB TCP->SIO bridge for Unirom 8
//
// NOTE!
//
// While all of the basic debug functionality is present, the GDB
// bridge is still very much a work in progress!
//

// TODO: set running/halted state when reconnecting

// TODO: Handle software breakpoints internally?
//       If we break/step on a BD, the original branch instruction must be
//       the next PC. We could just lie to GDB about our PC but gdb will
//       try to shove it's software breakpoint in place.

// TODO: Add 4 to the PC if we're in a branch delay slot. (see above first)
// TODO: Split GDB server code from emulation logic, cache, etc?
// TODO: Continue and Step both take optional address arguments, needs testing.
// TODO: Begin moving non-gdb-specific code to new classes
//       i.e. CPUHelper for emulation, calculating branches, etc

using System;
using System.Text;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Globalization;
using System.Collections.Generic;


public class GDBServer {

    private static bool _enabled = false;
    public static bool IsEnabled => _enabled;

    private static bool emulate_steps = false;

    private static Dictionary<UInt32, UInt32> original_opcode = new Dictionary<UInt32, UInt32>();

    public static TargetDataPort serial => Program.activeSerial;

    private static bool ack_enabled = true;
    private static bool manifest_enabled = true;

    const string memoryMap = @"<?xml version=""1.0""?>
<memory-map>
  <!-- Everything here is described as RAM, because we don't really
       have any better option. -->

  <!-- Main memory bloc: let's go with 8MB straight off the bat. -->
  <memory type=""ram"" start=""0x0000000000000000"" length=""0x800000""/>
  <memory type=""ram"" start=""0xffffffff80000000"" length=""0x800000""/>
  <memory type=""ram"" start=""0xffffffffa0000000"" length=""0x800000""/>

  <!-- EXP1 can go up to 8MB too. -->
  <memory type=""ram"" start=""0x000000001f000000"" length=""0x800000""/>
  <memory type=""ram"" start=""0xffffffff9f000000"" length=""0x800000""/>
  <memory type=""ram"" start=""0xffffffffbf000000"" length=""0x800000""/>

  <!-- Scratchpad -->
  <memory type=""ram"" start=""0x000000001f800000"" length=""0x400""/>
  <memory type=""ram"" start=""0xffffffff9f800000"" length=""0x400""/>

  <!-- Hardware registers -->
  <memory type=""ram"" start=""0x000000001f801000"" length=""0x2000""/>
  <memory type=""ram"" start=""0xffffffff9f801000"" length=""0x2000""/>
  <memory type=""ram"" start=""0xffffffffbf801000"" length=""0x2000""/>

  <!-- DTL BIOS SRAM -->
  <memory type=""ram"" start=""0x000000001fa00000"" length=""0x200000""/>
  <memory type=""ram"" start=""0xffffffff9fa00000"" length=""0x200000""/>
  <memory type=""ram"" start=""0xffffffffbfa00000"" length=""0x200000""/>

  <!-- BIOS -->
  <memory type=""ram"" start=""0x000000001fc00000"" length=""0x80000""/>
  <memory type=""ram"" start=""0xffffffff9fc00000"" length=""0x80000""/>
  <memory type=""ram"" start=""0xffffffffbfc00000"" length=""0x80000""/>

  <!-- This really is only for 0xfffe0130 -->
  <memory type=""ram"" start=""0xfffffffffffe0000"" length=""0x200""/>
</memory-map>
";

    const string targetXML = @"<?xml version=""1.0""?>
<!DOCTYPE feature SYSTEM ""gdb-target.dtd"">
<target version=""1.0"">

<!-- Helping GDB -->
<architecture>mips:3000</architecture>
<osabi>none</osabi>

<!-- Mapping ought to be flexible, but there seems to be some
     hardcoded parts in gdb, so let's use the same mapping. -->
<feature name=""org.gnu.gdb.mips.cpu"">
  <reg name=""r0"" bitsize=""32"" regnum=""0""/>
  <reg name=""r1"" bitsize=""32""/>
  <reg name=""r2"" bitsize=""32""/>
  <reg name=""r3"" bitsize=""32""/>
  <reg name=""r4"" bitsize=""32""/>
  <reg name=""r5"" bitsize=""32""/>
  <reg name=""r6"" bitsize=""32""/>
  <reg name=""r7"" bitsize=""32""/>
  <reg name=""r8"" bitsize=""32""/>
  <reg name=""r9"" bitsize=""32""/>
  <reg name=""r10"" bitsize=""32""/>
  <reg name=""r11"" bitsize=""32""/>
  <reg name=""r12"" bitsize=""32""/>
  <reg name=""r13"" bitsize=""32""/>
  <reg name=""r14"" bitsize=""32""/>
  <reg name=""r15"" bitsize=""32""/>
  <reg name=""r16"" bitsize=""32""/>
  <reg name=""r17"" bitsize=""32""/>
  <reg name=""r18"" bitsize=""32""/>
  <reg name=""r19"" bitsize=""32""/>
  <reg name=""r20"" bitsize=""32""/>
  <reg name=""r21"" bitsize=""32""/>
  <reg name=""r22"" bitsize=""32""/>
  <reg name=""r23"" bitsize=""32""/>
  <reg name=""r24"" bitsize=""32""/>
  <reg name=""r25"" bitsize=""32""/>
  <reg name=""r26"" bitsize=""32""/>
  <reg name=""r27"" bitsize=""32""/>
  <reg name=""r28"" bitsize=""32""/>
  <reg name=""r29"" bitsize=""32""/>
  <reg name=""r30"" bitsize=""32""/>
  <reg name=""r31"" bitsize=""32""/>

  <reg name=""lo"" bitsize=""32"" regnum=""33""/>
  <reg name=""hi"" bitsize=""32"" regnum=""34""/>
  <reg name=""pc"" bitsize=""32"" regnum=""37""/>
</feature>
<feature name=""org.gnu.gdb.mips.cp0"">
  <reg name=""status"" bitsize=""32"" regnum=""32""/>
  <reg name=""badvaddr"" bitsize=""32"" regnum=""35""/>
  <reg name=""cause"" bitsize=""32"" regnum=""36""/>
</feature>

<!-- We don't have an FPU, but gdb hardcodes one, and will choke
     if this section isn't present. -->
<feature name=""org.gnu.gdb.mips.fpu"">
  <reg name=""f0"" bitsize=""32"" type=""ieee_single"" regnum=""38""/>
  <reg name=""f1"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f2"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f3"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f4"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f5"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f6"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f7"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f8"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f9"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f10"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f11"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f12"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f13"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f14"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f15"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f16"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f17"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f18"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f19"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f20"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f21"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f22"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f23"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f24"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f25"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f26"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f27"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f28"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f29"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f30"" bitsize=""32"" type=""ieee_single""/>
  <reg name=""f31"" bitsize=""32"" type=""ieee_single""/>

  <reg name=""fcsr"" bitsize=""32"" group=""float""/>
  <reg name=""fir"" bitsize=""32"" group=""float""/>
</feature>
</target>
";

    // For joining parts of the TCP stream
    // TODO: there's no checks to stop this getting out of hand
    private static bool stitchingPacketsTogether = false;
    private static string activePacketString = "";




    /// <summary>
    /// Calculate the checksum for the packet
    /// </summary>
    /// <param name="packet"></param>
    /// <returns></returns>
    private static string CalculateChecksum( string packet ) {

        byte checksum = 0;
        foreach ( char c in packet ) {
            checksum += (byte)c;
        }

        //checksum %= (byte)256;
        return checksum.ToString( "X2" );
    }


    private static void Continue( string data ) {
        // TODO: specify an addr?
        Log.WriteLine( "Got continue request", LogType.Debug );
        if ( data.Length == 9 ) {
            // UNTESTED
            // To-do: Test it.
            // Got memory address to continue to
            Log.WriteLine( "Got memory address to continue to", LogType.Debug );
            CPU.SetHardwareBreakpoint( UInt32.Parse( data.Substring( 1, 8 ), NumberStyles.HexNumber ) );
        }
        lock ( SerialTarget.serialLock ) {
            if ( TransferLogic.Cont( false ) ) {
                SetHaltStateInternal( CPU.HaltState.RUNNING, false );
            }
        }

        SendGDBResponse( "OK" );
    }

    /// <summary>
    /// Respond to GDB Detach 'D' packet
    /// </summary>
    private static void Detach() {
        Log.WriteLine( "Detaching from target...", LogType.Info );

        // Do some stuff to detach from the target
        // Close & restart server
        ResetConnection();

        SendGDBResponse( "OK" );
    }

    public static void DisableManifest() {
        manifest_enabled = false;
    }

    /// <summary>
    /// Respond to GDB Extended Mode '!' packet
    /// </summary>
    private static void EnableExtendedMode() {
        SendGDBResponse( "OK" );
    }

    /*public static void EnableManifest() {
        manifest_enabled = true;
    }*/

    /// <summary>
    /// Attempt to locate an instruction from cache,
    /// otherwise grab it from ram.
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public static UInt32 GetInstructionCached( UInt32 address ) {
        byte[] read_buffer = new byte[ 4 ];
        UInt32 opcode = 0;

        if ( original_opcode.ContainsKey( address ) ) {
            opcode = original_opcode[ address ];
        } else {
            if ( GetMemory( address, 4, read_buffer ) ) {
                // To-do: Maybe grab larger chunks and parse
                opcode = BitConverter.ToUInt32( read_buffer, 0 );
                original_opcode[ address ] = opcode;
            }
        }

        return opcode;
    }

    /// <summary>
    /// Grab data from Unirom
    /// </summary>
    /// <param name="address"></param>
    /// <param name="length"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    public static bool GetMemory( uint address, uint length, byte[] data ) {
        Log.WriteLine( "Getting memory from 0x" + address.ToString( "X8" ) + " for " + length.ToString() + " bytes", LogType.Debug );

        lock ( SerialTarget.serialLock ) {
            if ( !TransferLogic.ReadBytes( address, length, data ) ) {
                Log.WriteLine( "Couldn't read bytes from Unirom!", LogType.Error );
                return false;
            }
        }

        return true;
    }


    /// <summary>
    /// Convert a hex nibble char to a byte
    /// </summary>
    /// <param name="inChar"></param>
    /// <returns></returns>
    private static byte GimmeNibble( char inChar ) {

        // TODO: lol not this
        /*switch ( inChar ) {
            case '0': return 0x0;
            case '1': return 0x1;
            case '2': return 0x2;
            case '3': return 0x3;
            case '4': return 0x4;
            case '5': return 0x5;
            case '6': return 0x6;
            case '7': return 0x7;
            case '8': return 0x8;
            case '9': return 0x9;
            case 'A': case 'a': return 0xA;
            case 'B': case 'b': return 0xB;
            case 'C': case 'c': return 0xC;
            case 'D': case 'd': return 0xD;
            case 'E': case 'e': return 0xE;
            case 'F': case 'f': return 0xF;
        }
        return 0;*/

        // Maybe this instead?        
        if ( inChar >= '0' && inChar <= '9' )
            return (byte)(inChar - 48);

        else if ( inChar >= 'A' && inChar <= 'F' )
            return (byte)(inChar - 55);

        else if ( inChar >= 'a' && inChar <= 'f' )
            return (byte)(inChar - 87);

        // Not a valid hex char
        else return 0;
    }

    /// <summary>
    /// User pressed Ctrl+C, do a thing
    /// </summary>
    private static void HandleCtrlC() {

        lock ( SerialTarget.serialLock ) {
            if ( TransferLogic.Halt( false ) )
                SetHaltStateInternal( CPU.HaltState.HALT, true );
        }

    }

    /// <summary>
    /// Double check that the console's there
    /// when starting up
    /// </summary>
    public static void Init() {

        Log.WriteLine( "Checking if Unirom is in debug mode...", LogType.Debug );

        // if it returns true, we might enter /m (monitor) mode, etc
        if (
            !TransferLogic.ChallengeResponse( CommandMode.DEBUG )
        ) {
            Log.WriteLine( "Couldn't determine if Unirom is in debug mode.", LogType.Error );
            return;
        }

        // More of a test than a requirement...
        Log.WriteLine( "Grabbing initial state...", LogType.Debug );
        CPU.DumpRegs();

        Log.WriteLine( "GDB server initialised" );
        _enabled = true;
    }




    /// <summary>
    /// Respond to GDB Memory Read 'm' packet
    /// </summary>
    /// <param name="data"></param>
    private static void MemoryRead( string data ) {
        string[] parts = data.Substring( 1 ).Split( ',' );
        uint address = uint.Parse( parts[ 0 ], System.Globalization.NumberStyles.HexNumber );
        uint length = uint.Parse( parts[ 1 ], System.Globalization.NumberStyles.HexNumber );
        byte[] read_buffer = new byte[ length ];
        string response = "";

        //ReadCached( address, length, read_buffer );
        GetMemory( address, length, read_buffer );

        for ( uint i = 0; i < length; i++ ) {
            response += read_buffer[ i ].ToString( "X2" );
        }

        //Console.WriteLine( "MemoryRead @ 0x"+ address.ToString("X8") + ":" + response );
        SendGDBResponse( response );
    }


    /// <summary>
    /// Parse and upload an $M packet - e.g. as a result of `load` in GDB
    /// </summary>
    /// <param name="data"></param>
    private static void MemoryWrite( string data ) {

        // TODO: validate memory regions

        UInt32 address = UInt32.Parse( data.Substring( 1, data.IndexOf(",") - 1 ), NumberStyles.HexNumber );

        // Where in the string do we find the addr substring
        int sizeStart = data.IndexOf( "," ) + 1;
        int sizeEnd = data.IndexOf( ":" );
        UInt32 length = UInt32.Parse( data.Substring( sizeStart, (sizeEnd - sizeStart) ), NumberStyles.HexNumber );
        byte[] bytes_out = ParseHexBytes( data, sizeEnd + 1, length );

        if ( !original_opcode.ContainsKey( address ) ) {

            PareseToCache( address, length, bytes_out );
        }


        lock ( SerialTarget.serialLock ) {
            TransferLogic.Command_SendBin( address, bytes_out );
        }

        SendGDBResponse( "OK" );
    }

    private static void PareseToCache( UInt32 address, UInt32 length, byte[] read_buffer ) {
        UInt32 instruction;

        for ( uint i = 0; i < length; i += 4 ) {
            if ( length - i < 4 )
                break; // derp?

            instruction = BitConverter.ToUInt32( read_buffer, (int)i );

            if ( !original_opcode.ContainsKey( address + i ) && !CPU.IsBreakInstruction( instruction ) ) {
                original_opcode[ address + i ] = instruction;
            }
        }
    }

    /// <summary>
    /// Parse a string of hex bytes (no preceding 0x)
    /// </summary>
    /// <param name="inString"></param>
    /// <param name="startChar"></param>
    /// <param name="numBytesToRead"></param>
    /// <returns></returns>
    /// <exception cref="IndexOutOfRangeException"></exception>
    public static byte[] ParseHexBytes( string inString, int startChar, UInt32 numBytesToRead ) {

        if ( inString.Length < startChar + (numBytesToRead * 2) ) {
            throw new IndexOutOfRangeException( "Input string is too short!" );
        }

        byte[] outBytes = new byte[ numBytesToRead ];

        byte activeByte;
        int charPos = 0;

        for ( int i = startChar; i < startChar + (numBytesToRead * 2); i += 2 ) {
            char first = inString[ i ];
            char second = inString[ i + 1 ];
            activeByte = (byte)((GimmeNibble( first ) << 4) | GimmeNibble( second ));
            outBytes[ charPos++ ] = activeByte;
        }

        return outBytes;
    }


    /// <summary>
    /// Receive a command and do some stuff with it
    /// </summary>
    /// <param name="data"></param>
    private static void ProcessCommand( string data ) {

        //Console.WriteLine( "Got command " + data );

        switch ( data[ 0 ] ) {
            case '!':
                EnableExtendedMode();
                break;

            case '?':
                QueryHaltReason();
                break;

            case 'c': // Continue - c [addr]
                Continue( data );
                break;

            case 's': // Step - s [addr]
                lock ( SerialTarget.serialLock ) {
                    Step( data, emulate_steps );
                }
                break;

            case 'D': // Detach
                Detach();
                break;

            case 'g': // Get registers
                ReadRegisters();
                break;

            case 'G': // Write registers
                WriteRegisters( data );
                break;

            case 'H': // thread stuff
                if ( data.StartsWith( "Hc0" ) ) {
                    SendGDBResponse( "OK" );
                } else if ( data.StartsWith( "Hc-1" ) ) {
                    SendGDBResponse( "OK" );
                } else if ( data.StartsWith( "Hg0" ) ) {
                    SendGDBResponse( "OK" );
                } else Unimplemented( data );
                break;

            case 'm': // Read memory
                MemoryRead( data );
                break;

            case 'M': // Write memory
                MemoryWrite( data );
                break;

            case 'p': // Read single register
                ReadRegister( data );
                break;

            case 'P': // Write single register
                WriteRegister( data );
                break;

            case 'q':
                if ( data.StartsWith( "qAttached" ) ) {
                    SendGDBResponse( "1" );
                } else if ( data.StartsWith( "qC" ) ) {
                    // Get Thread ID, always 00
                    SendGDBResponse( "QC00" );
                } else if ( data.StartsWith( "qSupported" ) ) {
                    if ( manifest_enabled ) {
                        SendGDBResponse( "PacketSize=4000;qXfer:features:read+;qXfer:threads:read+;qXfer:memory-map:read+;QStartNoAckMode+" );
                    } else {
                        SendGDBResponse( "PacketSize=4000;qXfer:threads:read+;QStartNoAckMode+" );
                    }
                    
                } else if ( manifest_enabled && data.StartsWith( "qXfer:features:read:target.xml:" ) ) {
                    SendPagedResponse( targetXML );
                } else if ( data.StartsWith( "qXfer:memory-map:read::" ) ) {
                    SendPagedResponse( memoryMap );
                } else if ( data.StartsWith( "qXfer:threads:read::" ) ) {
                    SendPagedResponse( "<?xml version=\"1.0\"?><threads></threads>" );
                } else if ( data.StartsWith( "qRcmd" ) ) {
                    // To-do: Process monitor commands
                    ProcessMonitorCommand( data );
                } else Unimplemented( data );
                break;

            case 'Q':
                if ( data.StartsWith( "QStartNoAckMode" ) ) {
                    SendGDBResponse( "OK" );
                    ack_enabled = false;
                } else Unimplemented( data );
                break;

            case 'v':
                if ( data.StartsWith( "vAttach" ) ) {
                    // 
                    Unimplemented( data );
                } else if ( data.StartsWith( "vMustReplyEmpty" ) ) {
                    SendGDBResponse( "" );
                } else if ( data.StartsWith( "vKill;" ) ) {
                    // Kill the process
                    SendGDBResponse( "OK" );
                } else Unimplemented( data );
                break;

            case 'X':
                // Write data to memory

                // E.g. to signal the start of mem writes with 
                // $Xffffffff8000f800,0:#e4
                //Console.WriteLine( "Pausing the PSX for uploads..." );
                lock ( SerialTarget.serialLock ) {
                    TransferLogic.ChallengeResponse( CommandMode.HALT );
                }
                SendGDBResponse( "" );
                break;

            // Comment out, let GDB manage writing breakpoints
            // To-do: Consider tracking/setting breakpoints in our GDB stub
            /*case 'Z':
                // Set breakpoint
                SetBreakpoint( data );

                break;

            case 'z':
                SendGDBResponse( "" );
                break;*/

            default:
                Unimplemented( data );
                break;
        }

    }


    /// <summary>
    /// Get data and do a thing with it
    /// </summary>
    /// <param name="Data"></param>
    public static void ProcessData( string Data ) {

        char[] packet = Data.ToCharArray();
        string packetData = "";
        string our_checksum = "0";
        int offset = 0;
        int size = Data.Length;

        //  This one isn't sent in plain text
        if ( Data[ 0 ] == (byte)0x03 ) {
            //Console.WriteLine( "Got a ^C" );
            HandleCtrlC();
            return;
        }

        // TODO: this could maybe be done nicer?
        if ( stitchingPacketsTogether ) {
            // rip GC, #yolo
            //Console.WriteLine( "Adding partial packet, len= " + Data.Length );
            activePacketString += Data;
            // did we reach the end?
            if ( Data.IndexOf( "#" ) == Data.Length - 2 - 1 ) {
                stitchingPacketsTogether = false;
                // now re-call this function with the completed packet
                ProcessData( activePacketString );
            }
            return;
        }

        //Console.WriteLine( "Processing data: " + Data );
        while ( size > 0 ) {
            char c = packet[ offset++ ];
            size--;
            if ( c == '+' ) {
                SendAck();
            }
            if ( c == '$' ) {
                int end = Data.IndexOf( '#', offset );
                if ( end == -1 ) {
                    //Console.WriteLine( "Partial packet, len=" + Data.Length );
                    stitchingPacketsTogether = true;
                    activePacketString = Data;
                    return;
                }

                packetData = Data.Substring( offset, end - offset );
                //Console.WriteLine( "Packet data: " + packetData );
                our_checksum = CalculateChecksum( packetData );
                size -= (end - offset);
                offset = end;
            } else if ( c == '#' ) {
                string checksum = Data.Substring( offset, 2 );
                //Console.WriteLine( "Checksum: " + checksum );
                //Console.WriteLine( "Our checksum: " + our_checksum );
                if ( checksum.ToUpper().Equals( our_checksum ) ) {
                    //Console.WriteLine( "Checksums match!" );
                    if ( ack_enabled )
                        SendAck();

                    ProcessCommand( packetData );
                    //Bridge.Send( "$" + packetData + "#" + CalculateChecksum( packetData ));
                    //ProcessPacket( packetData );
                } else {
                    Log.WriteLine( "Checksums don't match!", LogType.Error );
                }
                offset += 2;
                size -= 3;
            } else if ( c == '-' ) {
                Log.WriteLine( "Negative ACK", LogType.Error );
            }
        }
    }

    private static void ProcessMonitorCommand( string data ) {
        Log.WriteLine( "Got qRcmd: " + data, LogType.Debug );
        SendGDBResponse( "OK" );
    }


    /// <summary>
    /// Respond to GDB Query '?' packet
    /// </summary>
    private static void QueryHaltReason() {
        switch ( CPU.GetHaltState() ) {
            case CPU.HaltState.RUNNING: SendGDBResponse( "S00" ); break;
            case CPU.HaltState.HALT: SendGDBResponse( "S05" ); break;
        }
    }

    private static void ReadCached( UInt32 address, UInt32 length, byte[] read_buffer ) {
        UInt32 instruction;


        // Check for data 4 bytes at a time
        // If not found, fetch memory and push it to cache + buffer

        // Just grab the whole chunk for now if we don't have the start
        if ( original_opcode.ContainsKey( address ) ) {
            for ( uint i = 0; i < length; i += 4 ) {
                instruction = GetInstructionCached( address + i );
                Array.Copy( BitConverter.GetBytes( instruction ), 0, read_buffer, i, (length - i < 4) ? length - i : 4 );
            }
        } else {
            GetMemory( address, length, read_buffer );
            PareseToCache( address, length, read_buffer );
        }
    }

    /// <summary>
    /// Respond to GDB Read Register 'p' packet
    /// </summary>
    /// <param name="data"></param>
    private static void ReadRegister( string data ) {
        if ( (data.Length != 12) || (data.Substring( 3, 1 ) != "=") ) {
            SendGDBResponse( "E00" );
        } else {
            uint reg_num = uint.Parse( data.Substring( 1, 2 ), System.Globalization.NumberStyles.HexNumber );

            lock ( SerialTarget.serialLock ) {

                bool wasRunning = CPU.GetHaltState() == CPU.HaltState.RUNNING;

                if ( wasRunning )
                    TransferLogic.Halt( false );

                CPU.GetRegs();

                if ( wasRunning )
                    TransferLogic.Cont( false );

            }
            CPU.GetOneRegisterBE( reg_num ).ToString( "X8" );

        }
    }

    /// <summary>
    /// Respond to GDB Read Register 'g' packet
    /// </summary>
    private static void ReadRegisters() {
        string register_data = "";

        CPU.GetRegs();

        for ( uint i = 0; i < 72; i++ )
            register_data += CPU.GetOneRegisterBE( i ).ToString( "X8" );

        SendGDBResponse( register_data );
    }

    public static void ResetConnection() {
        ack_enabled = true;
    }

    /// <summary>
    /// Send GDB a packet acknowledgement(only in ack mode)
    /// </summary>
    private static void SendAck() {
        Bridge.Send( "+" );
    }

    /// <summary>
    /// The main function used for replying to GDB
    /// </summary>
    /// <param name="response"></param>
    private static void SendGDBResponse( string response ) {
        Bridge.Send( "$" + response + "#" + CalculateChecksum( response ) );
    }

    /// <summary>
    /// Send GDB a Paged response
    /// </summary>
    /// <param name="response"></param>
    private static void SendPagedResponse( string response ) {
        Bridge.Send( "$l" + response + "#" + CalculateChecksum( response ) );
    }

    /// <summary>
    /// Set the console's state
    /// </summary>
    /// <param name="inState"></param>
    /// <param name="notifyGDB"></param>
    public static void SetHaltStateInternal( CPU.HaltState inState, bool notifyGDB ) {
        CPU.SetHaltState( inState );
        if ( notifyGDB ) {
            if ( CPU.GetHaltState() == CPU.HaltState.RUNNING ) {
                SendGDBResponse( "S00" );
            } else {
                SendGDBResponse( "S05" );
            }
        }
    }

    /// <summary>
    /// Respond to a GDB Step 's' packet
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public static void Step( string data, bool use_emulation ) {

        if ( data.Length > 1 ) {
            Log.WriteLine( "Hrm?", LogType.Debug );
        }


        // This isn't really fleshed out, disabled for now.
        // Attempt to emulate instructions internally rather than firing them on console
        // If there is something we can't handle, recurse with use_emulation = false
        if ( use_emulation ) {
            CPU.EmulateStep();
            // Notify GDB of "halt"?
        } else {
            /*if ( data.Length == 9 ) {
                // UNTESTED
                // To-do: Test it.
                // Got memory address to step to
                Log.ToScreen( "Got memory address to step to", LogType.Debug );
                next_pc = UInt32.Parse( data.Substring( 1, 8 ), NumberStyles.HexNumber );
            } else {
                
            }*/
            CPU.HardwareStep();

            SendGDBResponse( "OK" );

            if ( TransferLogic.Cont( false ) ) {
                SetHaltStateInternal( CPU.HaltState.RUNNING, false );
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="data"></param>
    private static void Unimplemented( string data ) {
        SendGDBResponse( "" );
        Log.WriteLine( "Got unimplemented gdb command " + data + ", reply empty", LogType.Debug );
    }

    /// <summary>
    /// Respond to GDB Write Register 'P' packet
    /// </summary>
    /// <param name="data"></param>
    private static void WriteRegister( string data ) {

        if ( (data.Length != 12) || (data.Substring( 3, 1 ) != "=") ) {
            SendGDBResponse( "E00" );
        } else {

            uint reg_num = uint.Parse( data.Substring( 1, 2 ), System.Globalization.NumberStyles.HexNumber );
            uint reg_value = uint.Parse( data.Substring( 4, 8 ), System.Globalization.NumberStyles.HexNumber );

            lock ( SerialTarget.serialLock ) {

                bool wasRunning = CPU.GetHaltState() == CPU.HaltState.RUNNING;

                if ( wasRunning )
                    TransferLogic.Halt( false );

                //GetRegs(); // Request registers from Unirom
                CPU.SetOneRegisterBE( reg_num, reg_value ); // Set the register
                CPU.SetRegs(); // Send registers to Unirom

                if ( wasRunning )
                    TransferLogic.Cont( false );

            }
            SendGDBResponse( "OK" );
        }
    }


    /// <summary>
    /// Respond to GDB Write Registers 'G' packet
    /// </summary>
    /// <param name="data"></param>
    private static void WriteRegisters( string data ) {
        uint length = (uint)data.Length - 1;

        lock ( SerialTarget.serialLock ) {

            bool wasRunning = CPU.GetHaltState() == CPU.HaltState.RUNNING;

            if ( wasRunning )
                TransferLogic.Halt( false );

            //GetRegs();
            for ( uint i = 0; i < length; i += 8 ) {
                uint reg_num = i / 8;
                uint reg_value = uint.Parse( data.Substring( (int)i + 1, 8 ), System.Globalization.NumberStyles.HexNumber );
                CPU.SetOneRegisterBE( reg_num, reg_value );
            }
            CPU.SetRegs();

            if ( wasRunning )
                TransferLogic.Cont( false );

        }
    }
}
