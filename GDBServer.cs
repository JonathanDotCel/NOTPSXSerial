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

// TODO: allow reconnection
// TODO: set running/halted state when reconnecting

// TODO: Handle software breakpoints internally?
//       If we break/step on a BD, the original branch instruction must be
//       the next PC. We could just lie to GDB about our PC but gdb will
//       try to shove it's software breakpoint in place.

// TODO: Add 4 to the PC if we're in a branch delay slot. (see above first)
// TODO: Split GDB server code from emulation logic, cache, etc?
// TODO: Continue and Step both take address arguments, needs testing.

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

public enum PrimaryOpcode : byte {
    SPECIAL = 0x00,
    BCZ = 0x01,
    J = 0x02,
    JAL = 0x03,
    BEQ = 0x04,
    BNE = 0x05,
    BLEZ = 0x06,
    BGTZ = 0x07,
    ADDI = 0x08,
    ADDIU = 0x09,
    SLTI = 0X0A,
    SLTIU = 0X0B,
    ANDI = 0X0C,
    ORI = 0X0D,
    XORI = 0X0E,
    LUI = 0X0F,
    COP0 = 0X10,
    COP1 = 0X11,
    COP2 = 0X12,
    COP3 = 0X13,
    LB = 0X20,
    LH = 0X21,
    LWL = 0X22,
    LW = 0X23,
    LBU = 0X24,
    LHU = 0X25,
    LWR = 0X26,
    SB = 0X28,
    SH = 0X29,
    SWL = 0X2A,
    SW = 0X2B,
    SWR = 0X2E,
    LWC0 = 0X30,
    LWC1 = 0X31,
    LWC2 = 0X32,
    LWC3 = 0X33,
    SWC0 = 0X38,
    SWC1 = 0X39,
    SWC2 = 0X3A,
    SWC3 = 0X3B
}

public enum SecondaryOpcode : byte {
    SLL = 0X00,
    SRL = 0X02,
    SRA = 0X03,
    SLLV = 0X04,
    SRLV = 0X06,
    SRAV = 0X07,
    JR = 0X08,
    JALR = 0X09,
    SYSCALL = 0X0C,
    BREAK = 0X0D,
    MFHI = 0X10,
    MTHI = 0X11,
    MFLO = 0X12,
    MTLO = 0X13,
    MULT = 0X18,
    MULTU = 0X19,
    DIV = 0X1A,
    DIVU = 0X1B,
    ADD = 0X20,
    ADDU = 0X21,
    SUB = 0X22,
    SUBU = 0X23,
    AND = 0X24,
    OR = 0X25,
    XOR = 0X26,
    NOR = 0X27,
    SLT = 0X2A,
    SLTU = 0X2B
}

public enum BCZOpcode : byte {
    BLTZ = 0x00,
    BLTZAL = 0x10,
    BGEZ = 0x01,
    BGEZAL = 0x11
}

// The PSX's Thread Control Block (usually TCB[0])
public class TCB {
    public UInt32[] regs = new UInt32[ (int)GPR.COUNT ];
}


public class GDBServer {

    private static bool _enabled = false;
    public static bool enabled => _enabled;

    private static bool emulate_steps = false;
    private static bool step_break_set = false;
    private static UInt32 step_break_addr = 0;
    private static UInt32 branch_address = 0;
    private static bool branch_on_next_exec;
    public static bool isStepBreakSet {
        get { return step_break_set; }
        set { step_break_set = value; }
    }

    private static Dictionary<UInt32, UInt32> original_opcode = new Dictionary<UInt32, UInt32>();

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

    public enum HaltState { RUNNING, HALT }; // Keep track of the console's state

    private static HaltState haltState = HaltState.HALT; // Console is halted by default

    /// <summary>
    /// Get the console's state
    /// </summary>
    public static HaltState GetHaltState() {
        return haltState;
    }

    /// <summary>
    /// Set the console's state
    /// </summary>
    /// <param name="inState"></param>
    /// <param name="notifyGDB"></param>
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

    /// <summary>
    /// Double check that the console's there
    /// when starting up
    /// </summary>
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
        DumpRegs();

        Console.WriteLine( "GDB server initialised" );
        _enabled = true;
    }

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

    private static PrimaryOpcode GetPrimaryOpcode( UInt32 opcode ) {
        return (PrimaryOpcode)(opcode >> 26);
    }

    private static SecondaryOpcode GetSecondaryOpcode( UInt32 opcode ) {
        return (SecondaryOpcode)(opcode & 0x3F);
    }

    private static BCZOpcode GetBCZOpcode( UInt32 opcode ) {
        return (BCZOpcode)((opcode >> 16) & 0x1F);
    }

    private static bool IsBreakInstruction( UInt32 opcode ) {
        PrimaryOpcode primary_opcode;
        SecondaryOpcode secondary_opcode;

        primary_opcode = GetPrimaryOpcode( opcode );
        secondary_opcode = GetSecondaryOpcode( opcode );

        if ( primary_opcode == PrimaryOpcode.SPECIAL && secondary_opcode == SecondaryOpcode.BREAK )
            return true;

        return false;
    }

    /// <summary>
    /// Parse and upload an $M packet - e.g. as a result of `load` in GDB
    /// </summary>
    /// <param name="data"></param>
    private static void MemoryWrite( string data ) {

        // TODO: validate memory regions

        UInt32 address = UInt32.Parse( data.Substring( 1, 8 ), NumberStyles.HexNumber );

        // Where in the string do we find the addr substring
        int sizeStart = data.IndexOf( "," ) + 1;
        int sizeEnd = data.IndexOf( ":" );
        UInt32 length = UInt32.Parse( data.Substring( sizeStart, (sizeEnd - sizeStart) ), NumberStyles.HexNumber );
        byte[] bytes_out = ParseHexBytes( data, sizeEnd + 1, length );
        UInt32 instruction = 0;

        if ( !original_opcode.ContainsKey( address ) ) {

            PareseToCache( address, length, bytes_out );
        }


        lock ( SerialTarget.serialLock ) {
            TransferLogic.Command_SendBin( address, bytes_out );
        }

        SendGDBResponse( "OK" );
    }

    /// <summary>
    /// Respond to GDB Detach 'D' packet
    /// </summary>
    private static void Detach() {
        Console.WriteLine( "Detaching from target..." );

        // Do some stuff to detach from the target
        // Close & restart server

        SendGDBResponse( "OK" );
    }

    /// <summary>
    /// Respond to GDB Extended Mode '!' packet
    /// </summary>
    private static void EnableExtendedMode() {
        SendGDBResponse( "OK" );
    }

    /// <summary>
    /// Respond to GDB Query '?' packet
    /// </summary>
    private static void QueryHaltReason() {
        switch ( haltState ) {
            case HaltState.RUNNING: SendGDBResponse( "S00" ); break;
            case HaltState.HALT: SendGDBResponse( "S05" ); break;
        }
    }

    /// <summary>
    /// Set a breakpoint at the specified address
    /// </summary>
    /// <param name="address"></param>
    private static void SetBreakpoint( uint address ) {
        // To-do: Convert this to software breakpoints, not hardware?
        // Maybe use hardware breakpoint if break request in ROM?
        lock ( SerialTarget.serialLock ) {
            TransferLogic.Command_SetBreakOnExec( address );
        }
        SendGDBResponse( "OK" );
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

        ReadCached( address, length, read_buffer );
        //GetMemory( address, length, read_buffer );

        for ( uint i = 0; i < length; i++ ) {
            response += read_buffer[ i ].ToString( "X2" );
        }

        //Console.WriteLine( "MemoryRead @ 0x"+ address.ToString("X8") + ":" + response );
        SendGDBResponse( response );
    }

    private static void PareseToCache( UInt32 address, UInt32 length, byte[] read_buffer ) {
        UInt32 instruction;

        for ( uint i = 0; i < length; i += 4 ) {
            if ( length - i < 4 )
                break; // derp?

            instruction = BitConverter.ToUInt32( read_buffer, (int)i );

            if ( !original_opcode.ContainsKey( address + i ) && !IsBreakInstruction( instruction ) ) {
                original_opcode[ address + i ] = instruction;
            }
        }
    }

    private static void ReadCached( UInt32 address, UInt32 length, byte[] read_buffer ) {
        UInt32 instruction;


        // Check for data 4 bytes at a time
        // If not found, fetch memory and push it to cache + buffer

        // Just grab the whole chunk for now if we don't have the start
        if ( !original_opcode.ContainsKey( address ) ) {
            GetMemory( address, length, read_buffer );
            PareseToCache(address, length, read_buffer );
        } else {
            for ( uint i = 0; i < length; i += 4 ) {
                instruction = GetInstructionCached( address + i );
                Array.Copy( BitConverter.GetBytes( instruction ), 0, read_buffer, i, 4 );
            }
        }
    }

    /// <summary>
    /// Respond to GDB Read Register 'g' packet
    /// </summary>
    private static void ReadRegisters() {
        string register_data = "";

        GetRegs();

        for ( uint i = 0; i < 72; i++ )
            register_data += GetOneRegisterBE( i ).ToString( "X8" );

        SendGDBResponse( register_data );
    }

    /// <summary>
    /// Respond to GDB Write Registers 'G' packet
    /// </summary>
    /// <param name="data"></param>
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
                SetOneRegisterBE( reg_num, reg_value );
            }
            SetRegs();

            if ( wasRunning )
                TransferLogic.Cont( false );

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

                bool wasRunning = GDBServer.haltState == HaltState.RUNNING;

                if ( wasRunning )
                    TransferLogic.Halt( false );

                GetRegs();

                if ( wasRunning )
                    TransferLogic.Cont( false );

            }
            GetOneRegisterBE( reg_num ).ToString( "X8" );

        }
    }

    /// <summary>
    /// Attempt to locate an instruction from cache,
    /// otherwise grab it from ram.
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    private static UInt32 GetInstructionCached( UInt32 address ) {
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
    /// Determine target address from a branch instruction
    /// </summary>
    /// <param name="opcode"></param>
    /// <param name="eval"></param>
    /// <returns></returns>
    private static UInt32 CalculateBranchAddress( UInt32 opcode, bool eval ) {
        UInt32 offset = (opcode & 0xFFFF) << 2;

        if ( eval ) {

            if ( (offset & (1 << 17)) != 0 ) { // if bit 17 is set, sign extend
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

    /// <summary>
    /// Unknown opcode detected when determining next PC, march on and hope for the best.
    /// </summary>
    /// <param name="opcode"></param>
    /// <returns></returns>
    private static UInt32 SkipJumpInstruction( UInt32 opcode ) {
        Console.WriteLine( "Unknown instruction above delay slot: " + opcode.ToString( "X8" ) );
        return tcb.regs[ (int)GPR.rapc ] += 8;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="opcode"></param>
    /// <returns></returns>
    private static void EmulateStep( UInt32 opcode ) {
        UInt32 rs = GetOneRegisterBE( (opcode >> 21) & 0x1F );
        UInt32 rt = GetOneRegisterBE( (opcode >> 16) & 0x1F );
        UInt32 rd = (opcode >> 11) & 0x1F;
        UInt32 immediate = opcode & 0xFFFF;

        //UInt32 pc = 0;
        bool emulated = true;

        switch ( GetPrimaryOpcode( opcode ) ) {
            case PrimaryOpcode.SPECIAL: // Special
                switch ( GetSecondaryOpcode( opcode ) ) {
                    case SecondaryOpcode.ADD:
                        SetOneRegisterBE( rd, rs + rt );
                        break;

                    case SecondaryOpcode.ADDU:
                        SetOneRegisterBE( rd, rs + rt );
                        break;
                        

                    case SecondaryOpcode.JR: // JR - Bits 21-25 contain the Jump Register
                    case SecondaryOpcode.JALR: // JALR - Bits 21-25 contain the Jump Register
                        branch_address = rs;
                        branch_on_next_exec = true;
                        break;

                    default: // derp?
                        //pc = SkipJumpInstruction( opcode );
                        emulated = false;
                        break;
                }
                break;

            case PrimaryOpcode.BCZ: // REGIMM / BcondZ
                switch ( GetBCZOpcode( opcode ) ) {
                    case BCZOpcode.BLTZ: // BLTZ
                    case BCZOpcode.BLTZAL: // BLTZAL
                        branch_address = CalculateBranchAddress( opcode, (Int32)rs < 0 );
                        tcb.regs[ (int)GPR.unknown0 ] = 1;
                        break;

                    case BCZOpcode.BGEZ: // BGEZ
                    case BCZOpcode.BGEZAL: // BGEZAL
                        branch_address = CalculateBranchAddress( opcode, (Int32)rs >= 0 );
                        tcb.regs[ (int)GPR.unknown0 ] = 1;
                        break;

                    default: // derp?
                        //pc = SkipJumpInstruction( opcode );
                        emulated = false;
                        break;
                }
                break;

            case PrimaryOpcode.J: // J
            case PrimaryOpcode.JAL: // JAL          
                branch_address = CalculateJumpAddress( opcode );
                tcb.regs[ (int)GPR.unknown0 ] = 1;
                break;

            case PrimaryOpcode.BEQ: // BEQ
                branch_address = CalculateBranchAddress( opcode, rs == rt );
                tcb.regs[ (int)GPR.unknown0 ] = 1;
                break;

            case PrimaryOpcode.BNE: // BNE
                branch_address = CalculateBranchAddress( opcode, rs != rt );
                tcb.regs[ (int)GPR.unknown0 ] = 1;
                break;

            case PrimaryOpcode.BLEZ: // BLEZ
                branch_address = CalculateBranchAddress( opcode, (Int32)rs <= 0 );
                tcb.regs[ (int)GPR.unknown0 ] = 1;
                break;

            case PrimaryOpcode.BGTZ: // BGTZ
                branch_address = CalculateBranchAddress( opcode, (Int32)rs > 0 );
                tcb.regs[ (int)GPR.unknown0 ] = 1;
                break;

            default: // derp?
                emulated = false;
                break;
        }

        if( emulated ) {
            if ( branch_on_next_exec ) {
                tcb.regs[ (int)GPR.rapc ] = branch_address;
                branch_on_next_exec = false;
                tcb.regs[ (int)GPR.unknown0 ] = 0;
            } else {
                if( tcb.regs[ (int)GPR.unknown0 ] == 0)
                    tcb.regs[ (int)GPR.rapc ] += 4;
            }
            
            SetRegs();
            SetHaltStateInternal( HaltState.HALT, true );
        }
        else {
            Console.WriteLine( "Un-emulated opcode: " + opcode.ToString( "X8" ) );
            if ( branch_on_next_exec ) {
                branch_on_next_exec = false;
                Step( "", false );
                tcb.regs[ (int)GPR.unknown0 ] = 0;
            } else {
                // Not emulated, set breakpoint 4 ahead and hope for the best
                SetBreakpoint( tcb.regs[ (int)GPR.rapc ] + 4 );
                step_break_set = true;
                SetRegs();
            }

            if ( TransferLogic.Cont( false ) ) {
                SetHaltStateInternal( HaltState.RUNNING, false );
            }
        }
    }

    /// <summary>
    /// Evaluate an instruciton to determine the address of the next instruction
    /// Used to decide where to place the next breakpoint when stepping.
    /// </summary>
    /// <param name="opcode"></param>
    /// <returns></returns>
    private static UInt32 JumpAddressFromOpcode( UInt32 opcode ) {
        UInt32 rs = GetOneRegisterLE( (opcode >> 21) & 0x1F );
        UInt32 rt = GetOneRegisterLE( (opcode >> 16) & 0x1F );

        UInt32 address;

        switch ( GetPrimaryOpcode( opcode ) ) {
            case PrimaryOpcode.SPECIAL: // Special
                switch ( GetSecondaryOpcode( opcode ) ) {
                    case SecondaryOpcode.JR: // JR - Bits 21-25 contain the Jump Register
                    case SecondaryOpcode.JALR: // JALR - Bits 21-25 contain the Jump Register
                        address = rs;
                        break;

                    default: // derp?
                        address = SkipJumpInstruction( opcode );
                        break;
                }
                break;

            case PrimaryOpcode.BCZ: // REGIMM / BcondZ
                switch ( GetBCZOpcode( opcode ) ) {
                    case BCZOpcode.BLTZ: // BLTZ
                    case BCZOpcode.BLTZAL: // BLTZAL
                        address = CalculateBranchAddress( opcode, (Int32)rs < 0 );
                        break;

                    case BCZOpcode.BGEZ: // BGEZ
                    case BCZOpcode.BGEZAL: // BGEZAL
                        address = CalculateBranchAddress( opcode, (Int32)rs >= 0 );
                        break;

                    default: // derp?
                        address = SkipJumpInstruction( opcode );
                        break;
                }
                break;

            case PrimaryOpcode.J: // J
            case PrimaryOpcode.JAL: // JAL          
                address = CalculateJumpAddress( opcode );
                break;

            case PrimaryOpcode.BEQ: // BEQ
                address = CalculateBranchAddress( opcode, rs == rt );
                break;

            case PrimaryOpcode.BNE: // BNE
                address = CalculateBranchAddress( opcode, rs != rt );
                break;

            case PrimaryOpcode.BLEZ: // BLEZ
                address = CalculateBranchAddress( opcode, (Int32)rs <= 0 );
                break;

            case PrimaryOpcode.BGTZ: // BGTZ
                address = CalculateBranchAddress( opcode, (Int32)rs > 0 );
                break;

            default: // derp?
                address = SkipJumpInstruction( opcode );
                break;
        }

        return address;
    }

    /// <summary>
    /// Respond to a GDB Step 's' packet
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private static void Step( string data, bool use_emulation ) {
        UInt32 next_pc;
        UInt32 opcode;


        if ( data.Length > 1 ) {
            Console.WriteLine( "Hrm?" );
        }


        // This isn't really fleshed out, disabled for now.
        // Attempt to emulate instructions internally rather than firing them on console
        // If there is something we can't handle, 
        if ( use_emulation ) {
            opcode = GetInstructionCached( tcb.regs[ (int)GPR.rapc ] );
            if ( tcb.regs[ (int)GPR.unknown0 ] == 0 ) {
                opcode = GetInstructionCached( tcb.regs[ (int)GPR.rapc ] );
            } else {
                branch_on_next_exec = true;
                opcode = GetInstructionCached( tcb.regs[ (int)GPR.rapc ] + 4 );
            }
            EmulateStep( opcode );
            // Notify GDB of "halt"?
        } else {
            if ( data.Length == 9 ) {
                // UNTESTED
                // To-do: Test it.
                // Got memory address to step to
                Console.WriteLine( "Got memory address to step to" );
                next_pc = UInt32.Parse( data.Substring( 1, 8 ), NumberStyles.HexNumber );
            } else {
                // To-do: Emulate opcodes except for load and store?
                if ( tcb.regs[ (int)GPR.unknown0 ] == 0 ) {
                    // Not in BD, step one instruction
                    next_pc = tcb.regs[ (int)GPR.rapc ] += 4;
                } else {
                    // We're in a branch delay slot, So we need to emulate
                    // the previous opcode to find the next instruction.
                    // To-do: Re-purpose this to emulate other instructions
                    opcode = GetInstructionCached( tcb.regs[ (int)GPR.rapc ] );
                    next_pc = JumpAddressFromOpcode( opcode );

                }
            }

            // Serial already locked, do our thang           
            SetBreakpoint( next_pc ); // To-do: Look at doing software breakpoints instead of cop0
            step_break_set = true;
            step_break_addr = next_pc;

            if ( TransferLogic.Cont( false ) ) {
                SetHaltStateInternal( HaltState.RUNNING, false );
            }
        }
    }

    public static void StepBreakCallback() {
        UInt32 current_pc = (tcb.regs[ (int)GPR.unknown0 ] == 0) ? tcb.regs[ (int)GPR.rapc ] : tcb.regs[ (int)GPR.rapc ] + 4;
        TransferLogic.Unhook();
        GDBServer.isStepBreakSet = false;
        if( current_pc != step_break_addr) {
            Console.WriteLine( "Stopped at unexpected step address " + current_pc.ToString( "X8" ) + " instead of " + step_break_addr.ToString( "X8" ) );
        }
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

                bool wasRunning = GDBServer.haltState == HaltState.RUNNING;

                if ( wasRunning )
                    TransferLogic.Halt( false );

                GetRegs(); // Request registers from Unirom
                SetOneRegisterBE( reg_num, reg_value ); // Set the register
                SetRegs(); // Send registers to Unirom

                if ( wasRunning )
                    TransferLogic.Cont( false );

            }
            SendGDBResponse( "OK" );
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="data"></param>
    private static void Unimplemented( string data ) {
        SendGDBResponse( "" );
        Console.WriteLine( "Got unimplemented gdb command " + data + ", reply empty" );
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
                      // TODO: specify an addr?
                      //Console.WriteLine( "Got continue request" );
                if ( data.Length == 9 ) {
                    // UNTESTED
                    // To-do: Test it.
                    // Got memory address to continue to
                    Console.WriteLine( "Got memory address to continue to" );
                    SetBreakpoint( UInt32.Parse( data.Substring( 1, 8 ), NumberStyles.HexNumber ) );
                }
                lock ( SerialTarget.serialLock ) {
                    if ( TransferLogic.Cont( false ) ) {
                        SetHaltStateInternal( HaltState.RUNNING, false );
                    }
                }
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
    /// User pressed Ctrl+C, do a thing
    /// </summary>
    private static void HandleCtrlC() {

        lock ( SerialTarget.serialLock ) {
            if ( TransferLogic.Halt( false ) )
                SetHaltStateInternal( HaltState.HALT, true );
        }

    }

    // For joining parts of the TCP stream
    // TODO: there's no checks to stop this getting out of hand
    private static bool stitchingPacketsTogether = false;
    private static string activePacketString = "";

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
                    Console.WriteLine( "Checksums don't match!" );
                }
                offset += 2;
                size -= 3;
            } else if ( c == '-' ) {
                Console.WriteLine( "NACK" );
            }
        }
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
    /// Grab data from Unirom
    /// </summary>
    /// <param name="address"></param>
    /// <param name="length"></param>
    /// <param name="data"></param>
    /// <returns></returns>
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

    /// <summary>
    /// Retrieve the regs from the PSX
    /// </summary>
    /// <returns></returns>
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

    /// <summary>
    /// Write registers back to the PSX
    /// </summary>
    /// <returns></returns>
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

    /// <summary>
    /// Return a single register from TCB(retrieved by GetRegs)
    /// Returns the value in big endian format
    /// </summary>
    /// <param name="reg"></param>
    /// <returns></returns>
    private static uint GetOneRegisterBE( uint reg ) {
        uint result;
        uint value = GetOneRegisterLE( reg );

        result = ((value >> 24) & 0xff) | ((value >> 8) & 0xff00) | ((value << 8) & 0xff0000) | ((value << 24) & 0xff000000);

        return result;
    }

    /// <summary>
    /// Return a single register from TCB(retrieved by GetRegs)
    /// Returns the value in little endian format(the default)
    /// </summary>
    /// <param name="reg"></param>
    /// <returns></returns>
    private static uint GetOneRegisterLE( uint reg ) {
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

    /// <summary>
    /// Set a single register in TCB(set by SetRegs)
    /// Value given in big endian format
    /// </summary>
    /// <param name="reg"></param>
    /// <param name="value"></param>
    private static void SetOneRegisterBE( uint reg, uint value ) {
        value = ((value & 0xff000000) >> 24) | ((value & 0x00ff0000) >> 8) | ((value & 0x0000ff00) << 8) |
            ((value & 0x000000ff) << 24);

        SetOneRegisterLE( reg, value );
    }

    /// <summary>
    /// Set a single register in TCB(set by SetRegs)
    /// Value given in little endian format( the default)
    /// </summary>
    /// <param name="reg"></param>
    /// <param name="value"></param>
    private static void SetOneRegisterLE( uint reg, uint value ) {
        if ( reg < 32 ) tcb.regs[ reg + 2 ] = value;
        if ( reg == 32 ) tcb.regs[ (int)GPR.stat ] = value;
        if ( reg == 33 ) tcb.regs[ (int)GPR.lo ] = value;
        if ( reg == 34 ) tcb.regs[ (int)GPR.hi ] = value;
        if ( reg == 35 ) tcb.regs[ (int)GPR.badv ] = value;
        if ( reg == 36 ) tcb.regs[ (int)GPR.caus ] = value;
        if ( reg == 37 ) tcb.regs[ (int)GPR.rapc ] = value;
    }

    /// <summary>
    /// Print out the current register values in TCB(retrieved by GetRegs)
    /// </summary>
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
