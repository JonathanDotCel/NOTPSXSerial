﻿// This Source Code Form is subject to the terms of the Mozilla Public
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

// TODO: allow reconnection
// TODO: set running/halted state when reconnecting

using System;
using System.Text;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Globalization;
using System.Collections.Generic;


// The PSX registers
public enum GPR {
    stat,
    badv,       // repurposed for unirom

    // GPR
    r0, at, v0, v1, a0, a1, a2, a3,
    t0, t1, t2, t3, t4, t5, t6, t7,
    s0, s1, s2, s3, s4, s5, s6, s7,
    t8, t9, k0, k1, gp, sp, fp, ra,

    rapc,
    hi, lo,
    sr,
    caus,
    // GPR

    unknown0, unknown1,
    unknown2, unknown3,
    unknown4, unknown5,
    unknown6, unknown7,
    unknown9,

    COUNT // C# only, not present on the PSX struct
}

// The PSX's Thread Control Block (usually TCB[0])
public class TCB {
    public UInt32[] regs = new UInt32[ (int)GPR.COUNT ];
}


public class GDBServer {

    private static bool _enabled = false;
    public static bool enabled => _enabled;

    private static bool step_break_set = false;
    private static UInt32 step_break_addr;
    private static Dictionary<UInt32, UInt32> original_opcode = new Dictionary<UInt32, UInt32>();

    public static bool isStepBreakSet {
        get { return step_break_set; }
        set { step_break_set = value; }
    }

    // The PSX's active thread control block
    // (a copy of the psx's registers at the time of breaking)
    public static TCB tcb = new TCB();
    public const int TCB_LENGTH_BYTES = (int)GPR.COUNT * 4;

    public static TargetDataPort serial => Program.activeSerial;

    private static bool ack_enabled = true;

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

    public enum HaltState { RUNNING, HALT };

    private static HaltState haltState = HaltState.HALT;

    public static HaltState GetHaltState() {
        return haltState;
    }

    public static void SetHaltStateInternal( HaltState inState, bool notifyGDB ) {
        haltState = inState;
        if ( notifyGDB ) {
            if ( haltState == HaltState.RUNNING ) {
                SendGDBResponse( "S00" );
            } else {
                SendGDBResponse( "S05" );
            }
        }
    }

    // Double check that the console's there
    // when starting up
    public static void Init() {

        Console.WriteLine( "Checking if Unirom is in debug mode..." );

        // if it returns true, we might enter /m (monitor) mode, etc
        if (
            !TransferLogic.ChallengeResponse( CommandMode.DEBUG )
        ) {
            Console.WriteLine( "Couldn't determine if Unirom is in debug mode." );
            return;
        }

        // More of a test than a requirement...
        Console.WriteLine( "Grabbing initial state..." );
        TransferLogic.Command_DumpRegs();

        Console.WriteLine( "GDB server initialised" );
        _enabled = true;

    }

    //
    // modulo 256 addition of everything between $ and # 
    // in the GDB packet
    //
    private static string CalculateChecksum( string packet ) {

        byte checksum = 0;
        foreach ( char c in packet ) {
            checksum += (byte)c;
        }

        //checksum %= (byte)256;
        return checksum.ToString( "X2" );
    }

    private static byte GimmeNibble( char inChar ) {

        // TODO: lol not this
        switch ( inChar ) {
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
        return 0;

    }

    //
    // Parse a string of hex bytes (no preceding 0x)
    // TODO: lowercase support?
    //
    public static byte[] ParseHexBytes( string inString, int startChar, UInt32 numBytesToRead ) {

        if ( inString.Length < startChar + (numBytesToRead * 2) ) {
            throw new IndexOutOfRangeException( "Input string is too short!" );
        }

        byte[] outBytes = new byte[ numBytesToRead ];

        byte activeByte = 0x00;
        int charPos = 0;

        for ( int i = startChar; i < startChar + (numBytesToRead * 2); i += 2 ) {
            char first = inString[ i ];
            char second = inString[ i + 1 ];
            activeByte = (byte)((GimmeNibble( first ) << 4) | GimmeNibble( second ));
            outBytes[ charPos++ ] = activeByte;
        }

        return outBytes;

    }


    //
    // Parse and upload an $M packet - e.g. as a result of `load` in GDB
    //
    private static void MemWrite( string data ) {

        // TODO: validate memory regions

        UInt32 targetMemAddr = UInt32.Parse( data.Substring( 1, 8 ), NumberStyles.HexNumber );

        // Where in the string do we find the addr substring
        int sizeStart = data.IndexOf( "," ) + 1;
        int sizeEnd = data.IndexOf( ":" );
        UInt32 targetSize = UInt32.Parse( data.Substring( sizeStart, (sizeEnd - sizeStart) ), NumberStyles.HexNumber );

        byte[] bytes_out = ParseHexBytes( data, sizeEnd + 1, targetSize );
        byte[] read_buffer = new byte[ 4 ];
        UInt32 instr_in_ram = 0;


        if ( targetSize == 4 ) {
            if ( bytes_out[ 0 ] == 0x0D ) {
                // Writing breakpoint to memory, save old opcode in our cache
                if ( GetMemory( targetMemAddr, 4, read_buffer ) ) {
                    instr_in_ram = BitConverter.ToUInt32( read_buffer, 0 );
                    // Key already exists, remove it to replace the entry
                    if ( read_buffer[ 0 ] != 0x0D ) {
                        Console.WriteLine( "Saving original opcode " + instr_in_ram.ToString( "X8" ) + " at " + targetMemAddr.ToString( "X8" ) );
                        original_opcode[ targetMemAddr ] = instr_in_ram;
                    }
                }
            } else {
                if ( original_opcode.ContainsKey( targetMemAddr ) ) {
                    //if ( original_opcode[ targetMemAddr ] == BitConverter.ToUInt32( bytes_out, 0 ) )
                    original_opcode.Remove( targetMemAddr );
                }
            }
        }

        lock ( SerialTarget.serialLock ) {
            TransferLogic.Command_SendBin( targetMemAddr, bytes_out );
        }

        SendGDBResponse( "OK" );

    }

    private static void Detach() {
        Console.WriteLine( "Detaching from target..." );

        // Do some stuff to detach from the target
        // Close & restart server

        SendGDBResponse( "OK" );
    }

    private static void EnableExtendedMode() {
        SendGDBResponse( "OK" );
    }

    private static void QueryHaltReason() {
        switch ( haltState ) {
            case HaltState.RUNNING: SendGDBResponse( "S00" ); break;
            case HaltState.HALT: SendGDBResponse( "S05" ); break;
        }
    }

    private static void SetBreakpoint( string data ) {
        UInt32 targetMemAddr = UInt32.Parse( data.Substring( 3, 8 ), NumberStyles.HexNumber );
        //Console.WriteLine( "Got breakpoint request for " + targetMemAddr.ToString( "X8" ) );

        lock ( SerialTarget.serialLock ) {

            TransferLogic.Command_SetBreakOnExec( targetMemAddr );
        }
        SendGDBResponse( "OK" );
    }

    private static void MemoryRead( string data ) {
        string[] parts = data.Substring( 1 ).Split( ',' );
        uint address = uint.Parse( parts[ 0 ], System.Globalization.NumberStyles.HexNumber );
        uint length = uint.Parse( parts[ 1 ], System.Globalization.NumberStyles.HexNumber );
        byte[] buffer = new byte[ length ];
        GetMemory( address, length, buffer );
        string response = "";

        for ( int i = 0; i < length; i++ ) {
            response += buffer[ i ].ToString( "X2" );
        }
        //Console.WriteLine( "MemoryRead @ 0x"+ address.ToString("X8") + ":" + response );
        SendGDBResponse( response );
    }

    private static void ReadRegisters() {
        string register_data = "";


        GetRegs();

        for ( uint i = 0; i < 72; i++ )
            register_data += GetOneRegister( i ).ToString( "X8" );

        SendGDBResponse( register_data );
    }

    private static void WriteRegisters( string data ) {
        uint length = (uint)data.Length - 1;

        lock ( SerialTarget.serialLock ) {

            bool wasRunning = GDBServer.haltState == HaltState.RUNNING;

            if ( wasRunning )
                TransferLogic.Halt( false );

            GetRegs();
            for ( uint i = 0; i < length; i += 8 ) {
                uint reg_num = i / 8;
                uint reg_value = uint.Parse( data.Substring( (int)i + 1, 8 ), System.Globalization.NumberStyles.HexNumber );
                SetOneRegister( reg_num, reg_value );
            }
            SetRegs();

            if ( wasRunning )
                TransferLogic.Cont( false );

        }

    }

    private static void ReadRegister( string data ) {
        if ( (data.Length != 12) || (data.Substring( 3, 1 ) != "=") ) {
            SendGDBResponse( "E00" );
        } else {
            uint reg_num = uint.Parse( data.Substring( 1, 2 ), System.Globalization.NumberStyles.HexNumber );

            lock ( SerialTarget.serialLock ) {

                bool wasRunning = GDBServer.haltState == HaltState.RUNNING;

                if ( wasRunning )
                    TransferLogic.Halt( false );

                GetRegs();

                if ( wasRunning )
                    TransferLogic.Cont( false );

            }
            GetOneRegister( reg_num ).ToString( "X8" );

        }
    }

    private static UInt32 GetOriginalOpcode( UInt32 address ) {
        byte[] read_buffer = new byte[ 4 ];
        UInt32 opcode = 0;

        if ( original_opcode.ContainsKey( address ) ) {
            opcode = original_opcode[ address ];
        } else {
            if ( GetMemory( address, 4, read_buffer ) ) {
                opcode = BitConverter.ToUInt32( read_buffer, 0 );
            }
        }

        return opcode;
    }

    private static UInt32 CalculateBranchAddress( UInt32 opcode, bool eval ) {
        UInt32 offset = (opcode & 0xFFFF) << 2;

        if ( eval ) {

            if ( (offset & (1 << 17)) != 0 ) { // Extend sign bit
                offset |= 0xFFFFC000;
            }

            return offset + tcb.regs[ (int)GPR.rapc ] + 4;
        } else {
            return tcb.regs[ (int)GPR.rapc ] += 8;
        }
    }

    private static UInt32 CalculateJumpAddress( UInt32 opcode ) {
        return ((tcb.regs[ (int)GPR.rapc ] + 4) & 0x80000000) | ((opcode & 0x03FFFFFF) << 2);
    }

    private static UInt32 SkipJumpInstruction( UInt32 opcode ) {
        Console.WriteLine( "Unknown instruction above delay slot: " + opcode.ToString( "X8" ) );
        return tcb.regs[ (int)GPR.rapc ] += 8;
    }

    private static UInt32 JumpAddressFromOpcode( UInt32 opcode ) {
        UInt32 rs = GetOneRegisterRev( (opcode >> 21) & 0x1F );
        UInt32 rt = GetOneRegisterRev( (opcode >> 16) & 0x1F );

        UInt32 address;

        switch ( opcode >> 26 ) {
            case 0x00: // Special
                switch ( opcode & 0x3F ) {
                    case 0x08: // JR - Bits 21-25 contain the Jump Register
                    case 0x09: // JALR - Bits 21-25 contain the Jump Register
                        address = rs;
                        break;

                    default:
                        address = SkipJumpInstruction( opcode );
                        break;
                }
                break;

            case 0x01: // REGIMM / BcondZ
                switch ( (opcode >> 16) & 0x1F ) {
                    case 0x00: // BLTZ
                    case 0x10: // BLTZAL
                        address = CalculateBranchAddress( opcode, rs < 0 );
                        break;

                    case 0x01: // BGEZ
                    case 0x11: // BGEZAL
                        address = CalculateBranchAddress( opcode, rs >= 0 );
                        break;

                    default:
                        address = SkipJumpInstruction( opcode );
                        break;
                }
                break;

            case 0x02: // J
            case 0x03: // JAL          
                address = CalculateJumpAddress( opcode );
                break;

            case 0x04: // BEQ
                address = CalculateBranchAddress( opcode, rs == rt );
                break;

            case 0x05: // BNE
                address = CalculateBranchAddress( opcode, rs != rt );
                break;

            case 0x06: // BLEZ
                address = CalculateBranchAddress( opcode, rs <= 0 );
                break;

            case 0x07: // BGTZ
                address = CalculateBranchAddress( opcode, rs > 0 );
                break;

            default: // derp?
                address = SkipJumpInstruction( opcode );
                break;
        }

        return address;
    }

    private static void Step() {
        byte[] last_opcode = new byte[ 4 ];
        UInt32 opcode;

        if ( tcb.regs[ (int)GPR.unknown0 ] == 0 ) {
            Console.WriteLine( "Not in BD, Stepping" );
            step_break_addr = tcb.regs[ (int)GPR.rapc ] += 4;
        } else {
            // We're in a branch delay slot, so we need to do some spooky voodoo
            // to get the next PC location for our temporary breakpoint.
            Console.WriteLine( "Step on BD, testing branch to find next pc" );

            opcode = GetOriginalOpcode( tcb.regs[ (int)GPR.rapc ] );
            //opcode = BitConverter.ToUInt32( last_opcode, 0 );
            Console.WriteLine( "Last opcode " + opcode.ToString( "X8" ) );
            step_break_addr = JumpAddressFromOpcode( opcode );

        }
        // Serial already locked, do our thang           
        TransferLogic.HookAddr( CommandMode.HOOKEXEC, step_break_addr ); // To-do: Look at doing software breakpoints instead of cop0
        step_break_set = true;
    }

    private static void WriteRegister( string data ) {

        if ( (data.Length != 12) || (data.Substring( 3, 1 ) != "=") ) {
            SendGDBResponse( "E00" );
        } else {

            uint reg_num = uint.Parse( data.Substring( 1, 2 ), System.Globalization.NumberStyles.HexNumber );
            uint reg_value = uint.Parse( data.Substring( 4, 8 ), System.Globalization.NumberStyles.HexNumber );

            lock ( SerialTarget.serialLock ) {

                bool wasRunning = GDBServer.haltState == HaltState.RUNNING;

                if ( wasRunning )
                    TransferLogic.Halt( false );

                GetRegs();
                SetOneRegister( reg_num, reg_value );
                SetRegs();

                if ( wasRunning )
                    TransferLogic.Cont( false );

            }
            SendGDBResponse( "OK" );
        }
    }

    private static void Unimplemented( string data ) {
        SendGDBResponse( "" );
        Console.WriteLine( "Got unimplemented gdb command " + data + ", reply empty" );
    }

    private static void ProcessCommand( string data ) {

        Console.WriteLine( "Got command " + data );

        switch ( data[ 0 ] ) {
            case '!':
                EnableExtendedMode();
                break;

            case '?':
                QueryHaltReason();
                break;

            case 'c': // Continue - c [addr]
                      // TODO: specify an addr?
                      //Console.WriteLine( "Got continue request" );
                lock ( SerialTarget.serialLock ) {
                    if ( TransferLogic.Cont( false ) ) {
                        SetHaltStateInternal( HaltState.RUNNING, false );
                    }
                }
                break;

            case 's':
                //Console.WriteLine( "Got step request" );


                lock ( SerialTarget.serialLock ) {
                    Step();

                    if ( TransferLogic.Cont( false ) ) {
                        SetHaltStateInternal( HaltState.RUNNING, false );
                    }
                }


                break;

            case 'D':
                Detach();
                break;

            case 'g':
                ReadRegisters();
                break;

            case 'G':
                WriteRegisters( data );
                break;

            case 'H':
                // thread stuff
                if ( data.StartsWith( "Hc0" ) ) {
                    SendGDBResponse( "OK" );
                } else if ( data.StartsWith( "Hc-1" ) ) {
                    SendGDBResponse( "OK" );
                } else if ( data.StartsWith( "Hg0" ) ) {
                    SendGDBResponse( "OK" );
                } else Unimplemented( data );
                break;

            case 'm':
                MemoryRead( data );
                break;

            case 'M':
                MemWrite( data );
                break;

            case 'p':
                ReadRegister( data );
                break;

            case 'P':
                WriteRegister( data );
                break;

            case 'q':
                if ( data.StartsWith( "qAttached" ) ) {
                    SendGDBResponse( "1" );
                } else if ( data.StartsWith( "qC" ) ) {
                    // Get Thread ID, always 00
                    SendGDBResponse( "QC00" );
                } else if ( data.StartsWith( "qSupported" ) ) {
                    SendGDBResponse( "PacketSize=4000;qXfer:features:read+;qXfer:threads:read+;qXfer:memory-map:read+;QStartNoAckMode+" );
                } else if ( data.StartsWith( "qXfer:features:read:target.xml:" ) ) {
                    SendPagedResponse( targetXML );
                } else if ( data.StartsWith( "qXfer:memory-map:read::" ) ) {
                    SendPagedResponse( memoryMap );
                } else if ( data.StartsWith( "qXfer:threads:read::" ) ) {
                    SendPagedResponse( "<?xml version=\"1.0\"?><threads></threads>" );
                } else if ( data.StartsWith( "qRcmd" ) ) {
                    // To-do: Process monitor commands
                    Console.WriteLine( "Got qRcmd: " + data );
                    SendGDBResponse( "" );
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

    // User pressed Ctrl+C, do a thing
    public static void HandleCtrlC() {

        lock ( SerialTarget.serialLock ) {
            if ( TransferLogic.Halt( false ) )
                SetHaltStateInternal( HaltState.HALT, true );
        }

    }

    // For joining parts of the TCP stream
    // TODO: there's no checks to stop this getting out of hand
    private static bool stitchingPacketsTogether = false;
    private static string activePacketString = "";

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
                //Console.WriteLine( "ACK" );
                SendAck();
            }
            if ( c == '$' ) {
                //Console.WriteLine( "Got a packet" );
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
                    Console.WriteLine( "Checksums don't match!" );
                }
                offset += 2;
                size -= 3;
            } else if ( c == '-' ) {
                Console.WriteLine( "NACK" );
                //SendNAck( );
            }
        }
    }

    private static void SendAck() {
        Bridge.Send( "+" );
    }

    private static void SendGDBResponse( string response ) {
        Bridge.Send( "$" + response + "#" + CalculateChecksum( response ) );
    }

    // ?
    private static void SendPagedResponse( string response ) {
        Bridge.Send( "$l" + response + "#" + CalculateChecksum( response ) );
    }

    public static bool GetMemory( uint address, uint length, byte[] data ) {
        Console.WriteLine( "Getting memory from 0x{0} for {1} bytes", address.ToString( "X8" ), length );

        lock ( SerialTarget.serialLock ) {
            if ( !TransferLogic.ReadBytes( address, length, data ) ) {
                Console.WriteLine( "Couldn't read bytes from Unirom!" );
                return false;
            }
        }

        return true;
    }

    //
    // Retrieve the regs from the PSX
    // and 
    //
    public static bool GetRegs() {
        bool got_regs = false;
        byte[] ptrBuffer = new byte[ 4 ];

        lock ( SerialTarget.serialLock ) {

            bool wasRunning = GDBServer.haltState == HaltState.RUNNING;

            if ( wasRunning )
                TransferLogic.Halt( false );


            // read the pointer to TCB[0]
            if ( TransferLogic.ReadBytes( 0x80000110, 4, ptrBuffer ) ) {
                UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );
                //Console.WriteLine( "TCB PTR " + tcbPtr.ToString( "X" ) );

                byte[] tcbBytes = new byte[ TCB_LENGTH_BYTES ];
                if ( TransferLogic.ReadBytes( tcbPtr, (int)GPR.COUNT * 4, tcbBytes ) ) {
                    Buffer.BlockCopy( tcbBytes, 0, tcb.regs, 0, tcbBytes.Length );

                    got_regs = true;
                }

                /*if ( tcb.regs[ (int)GPR.unknown0 ] == 1 ) {
                    // Move PC to next instruction if we're in a branch delay slot
                    tcb.regs[ (int)GPR.rapc ] += 4;
                }*/
            }

            if ( wasRunning )
                TransferLogic.Cont( false );
        }

        return got_regs;

    }

    public static bool SetRegs() {

        // read the pointer to TCB[0]
        byte[] ptrBuffer = new byte[ 4 ];
        if ( !TransferLogic.ReadBytes( 0x80000110, 4, ptrBuffer ) ) {
            return false;
        }

        UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );
        //Console.WriteLine( "TCB PTR " + tcbPtr.ToString( "X" ) );

        // Convert regs back to a byte array and bang them back out
        byte[] tcbBytes = new byte[ TCB_LENGTH_BYTES ];
        Buffer.BlockCopy( tcb.regs, 0, tcbBytes, 0, TCB_LENGTH_BYTES );

        TransferLogic.Command_SendBin( tcbPtr, tcbBytes );

        return true;

    }

    private static uint GetOneRegister( uint reg ) {
        uint result;
        uint value = GetOneRegisterRev( reg );

        result = ((value >> 24) & 0xff) | ((value >> 8) & 0xff00) | ((value << 8) & 0xff0000) | ((value << 24) & 0xff000000);

        return result;
    }

    private static uint GetOneRegisterRev( uint reg ) {
        uint value = 0;
        if ( reg == 0 ) value = 0;
        else if ( reg < 32 ) value = tcb.regs[ reg + 2 ];
        else if ( reg == 32 ) value = tcb.regs[ (int)GPR.stat ];
        else if ( reg == 33 ) value = tcb.regs[ (int)GPR.lo ];
        else if ( reg == 34 ) value = tcb.regs[ (int)GPR.hi ];
        else if ( reg == 35 ) value = tcb.regs[ (int)GPR.badv ];
        else if ( reg == 36 ) value = tcb.regs[ (int)GPR.caus ];
        else if ( reg == 37 ) value = tcb.regs[ (int)GPR.rapc ];

        return value;
    }

    private static void SetOneRegister( uint reg, uint value ) {
        value = ((value & 0xff000000) >> 24) | ((value & 0x00ff0000) >> 8) | ((value & 0x0000ff00) << 8) |
            ((value & 0x000000ff) << 24);
        //Console.WriteLine( "Set register {0} with value {1}", reg, value.ToString( "X8" ) );
        if ( reg < 32 ) tcb.regs[ reg + 2 ] = value;
        if ( reg == 32 ) tcb.regs[ (int)GPR.stat ] = value;
        if ( reg == 33 ) tcb.regs[ (int)GPR.lo ] = value;
        if ( reg == 34 ) tcb.regs[ (int)GPR.hi ] = value;
        if ( reg == 35 ) tcb.regs[ (int)GPR.badv ] = value;
        if ( reg == 36 ) tcb.regs[ (int)GPR.caus ] = value;
        if ( reg == 37 ) tcb.regs[ (int)GPR.rapc ] = value;
    }

    public static void DumpRegs() {

        int tab = 0;

        for ( int i = 0; i < (int)GPR.COUNT - 8; i++ ) {
            Console.Write( "\t {0} =0x{1}", ((GPR)i).ToString().PadLeft( 4 ), tcb.regs[ i ].ToString( "X8" ) );
            // this format won't change, so there's no issue hardcoding them
            if ( tab++ % 4 == 3 || i == 1 || i == 33 || i == 34 ) {
                Console.WriteLine();
                tab = 0;
            }
        }
        Console.WriteLine();

        Console.Write( "BD = 0x{0}\n", tcb.regs[ (int)GPR.unknown0 ].ToString( "X" ) );

        UInt32 cause = (tcb.regs[ (int)GPR.caus ] >> 2) & 0xFF;

        switch ( cause ) {
            case 0x04:
                Console.WriteLine( "AdEL - Data Load or instr fetch (0x{0})\n", cause );
                break;
            case 0x05:
                Console.WriteLine( "AdES - Data Store (unaligned?) (0x{0})\n", cause );
                break;
            case 0x06:
                Console.WriteLine( "IBE - Bus Error on instr fetch (0x{0})\n", cause );
                break;
            case 0x07:
                Console.WriteLine( "DBE - Bus Error on data load/store (0x{0})\n", cause );
                break;
            case 0x08:
                Console.WriteLine( "SYS - Unconditional Syscall (0x{0})\n", cause );
                break;
            case 0x09:
                Console.WriteLine( "BP - Break! (0x{0})\n", cause );
                break;
            case 0x0A:
                Console.WriteLine( "RI - Reserved Instruction (0x{0})\n", cause );
                break;
            case 0x0B:
                Console.WriteLine( "CpU - Coprocessor unavailable (0x{0})\n", cause );
                break;
            case 0x0C:
                Console.WriteLine( "Ov - Arithmetic overflow (0x{0})\n", cause );
                break;

            default:
                Console.WriteLine( "Code {0}!\n", cause );
                break;
        }
    }
}
