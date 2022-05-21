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

using System;
using System.Text;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;


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

    public static bool enabled = false;

    // The PSX's active thread control block
    // (a copy of the psx's registers at the time of breaking)
    public static TCB tcb = new TCB();
    public const int TCB_LENGTH_BYTES = (int)GPR.COUNT * 4;

    public static TargetDataPort serial => Program.activeSerial;

    /*struct GDBPacket {
        public byte[] data;
        public int len;
        public int pos;
    }*/

    // qSupported
    struct GDBClientFeatures {
        bool multiprocess;
        bool xmlRegisters;
        bool qRelocInsn;
        bool swbreak;
        bool hwbreak;
        bool fork_events;
        bool vfork_events;
        bool exec_events;
        bool vContSupported;
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
        
    }

    //
    // modulo 256 addition of everything between $ and # 
    // in the GDB packet
    //
    private static string CalculateChecksum( string packet ) {

        int checksum = 0;
        foreach ( char c in packet ) {
            checksum += (int)c;
        }
        checksum %= 256;
        return checksum.ToString( "x" );
    }

    //
    // TODO: flesh this out a bit
    //
    public static void UniromHalted() {

    }

    public static void ProcessData( string Data, Socket replySocket ) {

        char[] packet = Data.ToCharArray();
        string packetData = "";
        string our_checksum = "0";
        int offset = 0;
        int size = Data.Length;

        Console.WriteLine( "Processing data: " + Data );
        while ( size > 0 ) {
            char c = packet[ offset++ ];
            size--;
            if ( c == '+' ) {
                Console.WriteLine( "ACK" );
                SendAck( replySocket );
            }
            if ( c == '$' ) {
                Console.WriteLine( "Got a packet" );
                int end = Data.IndexOf( '#', offset );
                if ( end == -1 ) {
                    Console.WriteLine( "No end of packet found" );
                    return;
                }

                packetData = Data.Substring( offset, end - offset );
                Console.WriteLine( "Packet data: " + packetData );
                our_checksum = CalculateChecksum( packetData );
                size -= (end - offset);
                offset = end;
                Console.WriteLine( "Size remaining: " + size );
                Console.WriteLine( "Data remaining: " + Data.Substring( offset ) );
            } else if ( c == '#' ) {
                string checksum = Data.Substring( offset, 2 );
                Console.WriteLine( "Checksum: " + checksum );
                Console.WriteLine( "Our checksum: " + our_checksum );
                if ( checksum.Equals( our_checksum ) ) {
                    Console.WriteLine( "Checksums match!" );
                    SendAck( replySocket );
                    Bridge.Send( replySocket, "$" + packetData + "#" + CalculateChecksum( packetData ));
                    //ProcessPacket( packetData );
                } else {
                    Console.WriteLine( "Checksums don't match!" );
                }
                offset += 2;
                size -= 3;
            }
            else if ( c == '-' ) {
                Console.WriteLine( "NACK" );
                SendNAck( replySocket );
            }
        }
    }

    private static void SendAck( Socket replySocket ) {
        Bridge.Send( replySocket, "+" );
        Console.WriteLine( "+" );
    }

    private static void SendNAck( Socket replySocket ) {
        Bridge.Send( replySocket, "-" );
        Console.WriteLine( "-" );
    }


    //
    // Retrieve the regs from the PSX
    // and 
    //
    public static bool GetRegs() {

        // read the pointer to TCB[0]
        byte[] ptrBuffer = new byte[ 4 ];
        if ( !TransferLogic.ReadBytes( 0x80000110, 4, ptrBuffer ) ) {
            return false;
        }

        UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );

        Console.WriteLine( "TCB PTR " + tcbPtr.ToString( "X" ) );

        byte[] tcbBytes = new byte[ TCB_LENGTH_BYTES ];
        if ( !TransferLogic.ReadBytes( tcbPtr, (int)GPR.COUNT * 4, tcbBytes ) ) {
            return false;
        }

        Buffer.BlockCopy( tcbBytes, 0, tcb.regs, 0, tcbBytes.Length );

        return true;

    }

    public static bool SetRegs() {

        // read the pointer to TCB[0]
        byte[] ptrBuffer = new byte[ 4 ];
        if ( !TransferLogic.ReadBytes( 0x80000110, 4, ptrBuffer ) ) {
            return false;
        }

        UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );
        Console.WriteLine( "TCB PTR " + tcbPtr.ToString( "X" ) );

        // Convert regs back to a byte array and bang them back out
        byte[] tcbBytes = new byte[ TCB_LENGTH_BYTES ];
        Buffer.BlockCopy( tcb.regs, 0, tcbBytes, 0, TCB_LENGTH_BYTES );

        TransferLogic.Command_SendBin( tcbPtr, tcbBytes );

        return true;

    }

    public static void DumpRegs() {

        int tab = 0;

        for ( int i = 0; i < (int)GPR.COUNT - 9; i++ ) {
            Console.Write( "\t {0} =0x{1}", ((GPR)i).ToString().PadLeft( 4 ), tcb.regs[ i ].ToString( "X8" ) );
            // this format won't change, so there's no issue hardcoding them
            if ( tab++ % 4 == 3 || i == 1 || i == 33 || i == 34 ) {
                Console.WriteLine();
                tab = 0;
            }
        }
        Console.WriteLine();

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
