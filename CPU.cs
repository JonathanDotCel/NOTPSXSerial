using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


public class CPU {

    public enum HaltState { RUNNING, HALT }; // Keep track of the console's state

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

        // unknown0 used for BD flag
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

    private static HaltState haltState = HaltState.HALT; // Console is halted by default


    // The PSX's Thread Control Block (usually TCB[0])
    public class TCB {
        public UInt32[] regs = new UInt32[ (int)GPR.COUNT ];
    }

    // The PSX's active thread control block
    // (a copy of the psx's registers at the time of breaking)
    public static TCB tcb = new TCB();
    public const int TCB_LENGTH_BYTES = (int)GPR.COUNT * 4;

    private static UInt32 branch_address = 0;
    private static bool branch_on_next_exec;
    private static bool step_break_set = false;
    private static UInt32 step_break_addr = 0;

    public static bool IsStepBreakSet {
        get { return step_break_set; }
        set { step_break_set = value; }
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

    /// <summary>
    /// 
    /// </summary>
    /// <param name="opcode"></param>
    /// <returns></returns>
    public static void EmulateStep( ) {
        UInt32 opcode;
        if ( tcb.regs[ (int)GPR.unknown0 ] == 0 ) {
            opcode = GDBServer.GetInstructionCached( tcb.regs[ (int)GPR.rapc ] );
        } else {
            branch_on_next_exec = true;
            opcode = GDBServer.GetInstructionCached( tcb.regs[ (int)GPR.rapc ] + 4 );
        }
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

        if ( emulated ) {
            if ( branch_on_next_exec ) {
                tcb.regs[ (int)GPR.rapc ] = branch_address;
                branch_on_next_exec = false;
                tcb.regs[ (int)GPR.unknown0 ] = 0;
            } else {
                if ( tcb.regs[ (int)GPR.unknown0 ] == 0 )
                    tcb.regs[ (int)GPR.rapc ] += 4;
            }

            SetRegs();
            GDBServer.SetHaltStateInternal( HaltState.HALT, true );
        } else {
            Console.WriteLine( "Un-emulated opcode: " + opcode.ToString( "X8" ) );
            if ( branch_on_next_exec ) {
                branch_on_next_exec = false;
                GDBServer.Step( "", false );
                tcb.regs[ (int)GPR.unknown0 ] = 0;
            } else {
                // Not emulated, set breakpoint 4 ahead and hope for the best
                SetHardwareBreakpoint( tcb.regs[ (int)GPR.rapc ] + 4 );
                step_break_set = true;
                SetRegs();
            }

            if ( TransferLogic.Cont( false ) ) {
                GDBServer.SetHaltStateInternal( HaltState.RUNNING, false );
            }
        }
    }

    private static UInt32 CalculateJumpAddress( UInt32 opcode ) {
        return ((tcb.regs[ (int)GPR.rapc ] + 4) & 0x80000000) | ((opcode & 0x03FFFFFF) << 2);
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

    private static BCZOpcode GetBCZOpcode( UInt32 opcode ) {
        return (BCZOpcode)((opcode >> 16) & 0x1F);
    }


    /// <summary>
    /// Get the console's state
    /// </summary>
    public static HaltState GetHaltState() {
        return haltState;
    }

    public static void SetHaltState( HaltState state ) {
        haltState = state;
    }

    public static void SetHardwareBreakpoint( UInt32 address ) {
        lock ( SerialTarget.serialLock ) {
            TransferLogic.Command_SetBreakOnExec( address );
        }
    }

    /// <summary>
    /// Return a single register from TCB(retrieved by GetRegs)
    /// Returns the value in big endian format
    /// </summary>
    /// <param name="reg"></param>
    /// <returns></returns>
    public static uint GetOneRegisterBE( uint reg ) {
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
    public static uint GetOneRegisterLE( uint reg ) {
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

    private static PrimaryOpcode GetPrimaryOpcode( UInt32 opcode ) {
        return (PrimaryOpcode)(opcode >> 26);
    }

    /// <summary>
    /// Retrieve the regs from the PSX
    /// </summary>
    /// <returns></returns>
    public static bool GetRegs() {
        bool got_regs = false;
        byte[] ptrBuffer = new byte[ 4 ];

        lock ( SerialTarget.serialLock ) {

            bool wasRunning = haltState == HaltState.RUNNING;

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

    private static SecondaryOpcode GetSecondaryOpcode( UInt32 opcode ) {
        return (SecondaryOpcode)(opcode & 0x3F);
    }

    public static void HardwareStep() {
        UInt32 address;

        // To-do: Emulate opcodes except for load and store?
        if ( tcb.regs[ (int)GPR.unknown0 ] == 0 ) {
            // Not in BD, step one instruction
            address = tcb.regs[ (int)GPR.rapc ] += 4;
        } else {
            // We're in a branch delay slot, So we need to emulate
            // the previous opcode to find the next instruction.
            // To-do: Re-purpose this to emulate other instructions
            address = JumpAddressFromOpcode( GDBServer.GetInstructionCached( tcb.regs[ (int)GPR.rapc ] ) );

        }

        lock ( SerialTarget.serialLock ) {
            TransferLogic.Command_SetBreakOnExec( address );
        }

        step_break_set = true;
        step_break_addr = address;
    }

    public static bool IsBreakInstruction( UInt32 opcode ) {
        PrimaryOpcode primary_opcode;
        SecondaryOpcode secondary_opcode;

        primary_opcode = GetPrimaryOpcode( opcode );
        secondary_opcode = GetSecondaryOpcode( opcode );

        if ( primary_opcode == PrimaryOpcode.SPECIAL && secondary_opcode == SecondaryOpcode.BREAK )
            return true;

        return false;
    }


    /// <summary>
    /// Evaluate an instruciton to determine the address of the next instruction
    /// Used to decide where to place the next breakpoint when stepping.
    /// </summary>
    /// <param name="opcode"></param>
    /// <returns></returns>
    public static UInt32 JumpAddressFromOpcode( UInt32 opcode ) {
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
    /// Set a single register in TCB(set by SetRegs)
    /// Value given in big endian format
    /// </summary>
    /// <param name="reg"></param>
    /// <param name="value"></param>
    public static void SetOneRegisterBE( uint reg, uint value ) {
        value = ((value & 0xff000000) >> 24) | ((value & 0x00ff0000) >> 8) | ((value & 0x0000ff00) << 8) |
            ((value & 0x000000ff) << 24);

        SetOneRegisterLE( reg, value );
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
    /// Unknown opcode detected when determining next PC, march on and hope for the best.
    /// </summary>
    /// <param name="opcode"></param>
    /// <returns></returns>
    private static UInt32 SkipJumpInstruction( UInt32 opcode ) {
        Console.WriteLine( "Unknown instruction above delay slot: " + opcode.ToString( "X8" ) );
        return tcb.regs[ (int)GPR.rapc ] += 8;
    }

    public static void StepBreakCallback() {
        UInt32 current_pc = (tcb.regs[ (int)GPR.unknown0 ] == 0) ? tcb.regs[ (int)GPR.rapc ] : tcb.regs[ (int)GPR.rapc ] + 4;
        TransferLogic.Unhook();
        step_break_set = false;
        if ( current_pc != step_break_addr ) {
            Console.WriteLine( "Stopped at unexpected step address " + current_pc.ToString( "X8" ) + " instead of " + step_break_addr.ToString( "X8" ) );
        }
    }
}
