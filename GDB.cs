using System;
using System.IO.Ports;
using System.Threading;


public enum GPR{
    
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

    unknown0,    unknown1,
    unknown2,    unknown3,
    unknown4,    unknown5,
    unknown6,    unknown7,
    unknown9,

    COUNT // C# only

}

// The PSX's Thread Control Block (usually TCB[0])
public class TCB{
       
    public UInt32[] regs = new UInt32[ (int)GPR.COUNT ];

}


public enum PSXState{ normal, halted };

public class GDB{

    public static bool enabled = false;

    public static SerialPort serial => Program.activeSerial;
    public static TCB tcb = new TCB();
    public static PSXState psxState = PSXState.normal;

	public const int TCB_LENGTH_BYTES = (int)GPR.COUNT * 4;

    public static void Init(){
		
		Console.WriteLine( "Checking if Unirom is in debug mode..." );

		// if it returns true, we might enter /m (monitor) mode, etc
		if (
			!TransferLogic.ChallengeResponse( CommandMode.DEBUG )
		) {
			Console.WriteLine( "Couldn't determine if Unirom is in debug mode." );
			return;
		}

		Console.WriteLine( "Grabbing initial state..." );

		TransferLogic.Command_DumpRegs();

		Console.WriteLine( "Monitoring sio..." );

		MonitorSIO();

		

    }

	public static bool GetRegs(){
		
		// read the pointer to TCB[0]
		byte[] ptrBuffer = new byte[4];
		if ( !TransferLogic.Command_DumpBytes( 0x80000110, 4, ptrBuffer ) ){
			return false;
		}

		UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );

		Console.WriteLine( "TCB PTR " + tcbPtr.ToString("X") );

		byte[] tcbBytes = new byte[TCB_LENGTH_BYTES];
		if ( !TransferLogic.Command_DumpBytes( tcbPtr, (int)GPR.COUNT *4, tcbBytes ) ){
			return false;
		}
		
		Buffer.BlockCopy( tcbBytes, 0, tcb.regs, 0, tcbBytes.Length );
				
		return true;

	}

	public static bool SetRegs(){
		
		// read the pointer to TCB[0]
		byte[] ptrBuffer = new byte[4];
		if ( !TransferLogic.Command_DumpBytes( 0x80000110, 4, ptrBuffer ) ){
			return false;
		}

		UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );
		Console.WriteLine( "TCB PTR " + tcbPtr.ToString("X") );

		// Convert regs back to a byte array and bang them back out
		byte[] tcbBytes = new byte[TCB_LENGTH_BYTES];
		Buffer.BlockCopy( tcb.regs, 0, tcbBytes, 0, TCB_LENGTH_BYTES );

		TransferLogic.Command_SendBin( tcbPtr, tcbBytes, Program.CalculateChecksum( tcbBytes ) );
				
		return true;

	}

	public static void DumpRegs(){
		int tab = 0;
		for( int i = 0; i < (int)GPR.COUNT -9; i++ ){			
			Console.Write( "\t {0} =0x{1}", ((GPR)i).ToString().PadLeft(4), tcb.regs[ i ].ToString("X8") );
			// this format won't change, so there's no issue hardcoding them
			if ( tab++%4 == 3 || i == 1 || i == 33 || i == 34 ){
				Console.WriteLine();
				tab = 0;
			}
		}
		Console.WriteLine();
	}

    public static void MonitorSIO(){
        
        string responeBuffer = ":)";

        while( true ){

            if ( serial.BytesToRead != 0 ){
            
                responeBuffer += (char)serial.ReadByte();

                if ( responeBuffer.Length > 4 ){
                    responeBuffer = responeBuffer.Remove( 0, 1 );
                }

                Console.WriteLine( "\r ResponseBuffer: " + responeBuffer + "  Len = " + responeBuffer.Length );

                // PSX telling us it was halted.
                if ( responeBuffer == "HLTD" ){
					responeBuffer = "";
                    Console.WriteLine( "PSX was halted" );
                }

            }

        }

    }


}
