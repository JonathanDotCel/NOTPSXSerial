// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.IO;
using System.Text;
using static Utils;
using System.Collections.Generic;
using System.Runtime.Remoting.Messaging;


#if USE_ELFSHARP
using ELFSharp.ELF;
using ELFSharp.ELF.Sections;
using ELFSharp.ELF.Segments;
#endif

public class TransferLogic {

    public static TargetDataPort activeSerial => Program.activeSerial;

    /// <summary>
    /// Read a 32 bit unsigned int from the serial connection
    /// (Takes care of endianness)
    /// </summary>	
    public static UInt32 read32() {

        UInt32 val = (UInt32)activeSerial.ReadByte();
        val += ((UInt32)activeSerial.ReadByte() << 8);
        val += ((UInt32)activeSerial.ReadByte() << 16);
        val += ((UInt32)activeSerial.ReadByte() << 24);

        return val;

    }

    /// <summary>
    /// Upload bytes to the specified address
    /// does verify contents
    /// does not execute or act upon the data
    /// </summary>
    public static bool Command_SendBin( UInt32 inAddr, byte[] inBytes ) {

        if ( !ChallengeResponse( CommandMode.SEND_BIN ) )
            return false;

        UInt32 checkSum = CalculateChecksum( inBytes );

        activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );
        activeSerial.Write( BitConverter.GetBytes( inBytes.Length ), 0, 4 );
        // In a pinch, Unirom will gloss over a null checksum. Don't though.
        activeSerial.Write( BitConverter.GetBytes( checkSum ), 0, 4 );

        // then the actual contents.

        return WriteBytes( inBytes, false );

    }

    /// <summary>
    /// Upload a ROM and attempt to flash to EEPROM
    /// </summary>
    public static bool Command_SendROM( UInt32 inAddr, byte[] inBytes ) {

        if ( inBytes.Length >= 15 ) {

            string license1 = Encoding.ASCII.GetString( inBytes, 0x04, 11 );
            string license2 = Encoding.ASCII.GetString( inBytes, 0x84, 11 );

            bool safe = (license1 == "Licensed by") || (license2 == "Licensed by");

            if ( !safe ) {

                Log.WriteLine( "Hey hey hey hey! This doesn't look like a ROM. Maybe an .exe?", LogType.Warning );
                Log.WriteLine( "Are you sure you want to flash this?", LogType.Warning );
                ConsoleKeyInfo c = Console.ReadKey();
                if ( c.KeyChar.ToString().ToLowerInvariant() != "y" ) {
                    return false;
                }

            }

        }

        if ( !ChallengeResponse( CommandMode.SEND_ROM ) )
            return false;

        UInt32 checkSum = CalculateChecksum( inBytes );

        activeSerial.Write( BitConverter.GetBytes( inBytes.Length ), 0, 4 );
        activeSerial.Write( BitConverter.GetBytes( checkSum ), 0, 4 );

        string flashResponse = "";

        while ( true ) {

            if ( activeSerial.BytesToRead != 0 ) {
                // Why the fuck does readchar return an int?
                flashResponse += (char)activeSerial.ReadByte();

                // filter any noise at the start of the response
                // seems to happen once in a while
                if ( flashResponse.Length > 4 )
                    flashResponse = flashResponse.Remove( 0, 1 );


            }

            Log.Write( "\r EEPROM Check: " + flashResponse );

            if ( flashResponse == "FITS" ) {
                Log.WriteLine( "\n\nRom will fit! \n Response: " + flashResponse + "!" );
                break;
            }

            if ( flashResponse == "NOPE" ) {
                Log.WriteLine( "\n\nThis rom is too big for the EEPROM! \n Response: " + flashResponse + "!", LogType.Error );
                return false;
            }

            if ( flashResponse == "NONE" ) {
                Log.WriteLine( "\n\nNo EEPROM detected! \n The response was: " + flashResponse + "!", LogType.Error );
                return false;
            }

            if ( flashResponse == "UNKN" ) {
                Log.WriteLine( "\n\nUnknown EEPROM detected! \n The response was: " + flashResponse + "!", LogType.Error );
                return false;
            }


        }

        Log.WriteLine( "Checks passed; sending ROM!" );

        return WriteBytes( inBytes, false );

    }


#if USE_ELFSHARP

    /// <summary>
    /// Dumps the Sections and Segments from an .ELF loaded via ElfSharp
    /// </summary>
    /// <param name="inElf"></param>
    public static void DumpElfInfo( IELF inElf ) {

        ConsoleColor oldColor = Console.ForegroundColor;

        Log.WriteLine( "\nNum ELF sections: " + inElf.Sections.Count );

        for ( int i = 0; i < inElf.Sections.Count; i++ ) {

            Section<UInt32> sect = (inElf.Sections[ i ] as Section<UInt32>);

            Console.ForegroundColor = (sect.Size == 0) ? ConsoleColor.Red : oldColor;

            Log.WriteLine( $"Section {i}: {sect.Name}" );
            Log.WriteLine( $"  Addr   : 0x{sect.LoadAddress.ToString( "X" )}" );
            Log.WriteLine( $"  Size   : 0x{sect.Size.ToString( "X" )} (0x{sect.EntrySize.ToString( "X" )})" );
            Log.WriteLine( $"  Flags  : {sect.Flags}" );
            Log.WriteLine( $"  Type   : {sect.Type}" );
            Log.WriteLine( $"  Offset : 0x{sect.Offset.ToString( "X" )}" );

            //byte[] b = sect.GetContents();
            //File.WriteAllBytes( "sect_" + sect.Name, b );

        }

        Log.WriteLine( "\nNum ELF segments: " + inElf.Segments.Count );

        for ( int i = 0; i < inElf.Segments.Count; i++ ) {

            Segment<UInt32> seg = inElf.Segments[ i ] as Segment<UInt32>;

            // Some segs have the .elf magic number
            Console.ForegroundColor = HasElfHeader( seg.GetFileContents() ) ? ConsoleColor.Red : oldColor;

            Log.WriteLine( "Segment " + i );
            Log.WriteLine( $"  Offset   : 0x{seg.Offset.ToString( "X" )}" );
            Log.WriteLine( $"  Size     : 0x{seg.Size.ToString( "X" )}  (0x{seg.FileSize.ToString( "X" )})" );
            Log.WriteLine( $"  PhysAddr : 0x{seg.PhysicalAddress.ToString( "X" )} for 0x{seg.Address.ToString( "X" )}" );
            Log.WriteLine( $"  Flags    : " + seg.Flags );
            Log.WriteLine( $"  Type     : " + seg.Type );

            //byte[] b = seg.GetFileContents();
            //File.WriteAllBytes( "seg_" + i, b );

        }

        UInt32 entryPoint = (inElf as ELF<UInt32>).EntryPoint;
        Log.WriteLine( $"\nEntry Point: 0x{entryPoint.ToString( "X" )}" );

        Console.ForegroundColor = oldColor;

    }


    /// <summary>
    ///  Convert a .ELF to an .EXE
    /// </summary>	
    public static byte[] ELF2EXE( byte[] inBytes ) {

        // Is it actually an elf tho?
        // Maybe it's a sneaky pixie.
        if ( !HasElfHeader( inBytes ) ) {
            Error( "This file doesn't have a valid .ELF header!" );
            return null;
        }

        MemoryStream mStream = new MemoryStream( inBytes );
        IELF elfy = ELFReader.Load( mStream, true );

        DumpElfInfo( elfy );

        //
        // Generate program bytes
        //

        // iterate through segs to find the lowest and highest bound
        // and grab the entry point from the elf, rather than looking for a header
        // thanks to SpicyJPEG for this method
        // reference: https://github.com/spicyjpeg/ps1-bare-metal/blob/main/tools/convertExecutable.py#L87-L107

        UInt32 lowestAddr = 0xFFFFFFFF;
        UInt32 highestAddr = 0;

        List<Segment<UInt32>> segmentsToLoad = new List<Segment<UInt32>>();

        for ( int i = 0; i < elfy.Segments.Count; i++ ) {
            Segment<UInt32> seg = elfy.Segments[ i ] as Segment<UInt32>;
            if ( seg.Type != SegmentType.Load || seg.FileSize == 0 ) {
                continue;
            }

            if ( seg.PhysicalAddress < 0x80010000 ) {
                Log.WriteLine( $"Skipping segment {i} @ phys addr < 0x80010000, presumed .exe header" );
                continue;
            }

            if ( HasElfHeader( seg.GetFileContents() ) ) {
                // Note: with the nugget build system, this is a 0x1000 length chunk
                // with an ELF header, then the actual PSX.EXE header in the middle
                // at about 0x800 into that offset.
                // We'll skip that and use our own
                Log.WriteLine( $"Skipping segment {i} with ELF header" );
                continue;
            }

            segmentsToLoad.Add( seg );

            if ( lowestAddr == 0xFFFFFFFF || seg.PhysicalAddress < lowestAddr ) {
                lowestAddr = seg.PhysicalAddress;
                Log.WriteLine( "New lowest segment at 0x" + seg.PhysicalAddress.ToString( "X" ) );
            }

            if ( seg.PhysicalAddress + seg.FileSize > highestAddr ) {
                Log.WriteLine( "New highest segment at 0x" + (seg.PhysicalAddress + seg.FileSize).ToString( "X" ) );
                highestAddr = seg.PhysicalAddress + (UInt32)seg.FileSize;
            }
        }

        UInt32 dataLength = highestAddr - lowestAddr;
        // round it up to a multiple of 0x800
        dataLength = (UInt32)((dataLength + 0x7FF) & ~0x7FF);

        byte[] progBytes = new byte[ dataLength ];

        for ( int i = 0; i < segmentsToLoad.Count; i++ ) {
            Segment<UInt32> seg = segmentsToLoad[ i ];

            // find the offset relative to the highest/lowest addr
            // lowest will be [0] in the out buffer
            UInt32 offset = seg.PhysicalAddress - lowestAddr;
            Log.WriteLine( $"Segment {i} at 0x{seg.PhysicalAddress.ToString( "X" )} has offset 0x{offset.ToString( "X" )}" );

            seg.GetFileContents().CopyTo( progBytes, offset );

        }

        //
        // Generate header bytes
        //

        const UInt32 headerLength = 0x800;
        byte[] headerBytes = new byte[ headerLength ];

        UInt32 entryPoint = (elfy as ELF<UInt32>).EntryPoint;
        UInt32 stackPointer = 0x801FFF00; // something kinda sensible looking
        byte[] magicBytes = System.Text.Encoding.ASCII.GetBytes( "PS-X EXE" );
        byte[] epBytes = BitConverter.GetBytes( entryPoint );
        byte[] destAddrBytes = BitConverter.GetBytes( lowestAddr );
        byte[] stackPointerBytes = BitConverter.GetBytes( stackPointer );
        byte[] fileSizeBytes = BitConverter.GetBytes( dataLength );

        Log.WriteLine( $"Adding a header with entry point 0x{entryPoint.ToString( "X" )} and copy dest 0x{lowestAddr.ToString( "X" )}" );
        // same jump and copy addr
        Buffer.BlockCopy( magicBytes, 0, headerBytes, 0x00, 0x08 );        // 0d
        Buffer.BlockCopy( epBytes, 0, headerBytes, 0x10, 0x04 );           // 16d
        Buffer.BlockCopy( destAddrBytes, 0, headerBytes, 0x18, 0x04 );     // 24d
        Buffer.BlockCopy( fileSizeBytes, 0, headerBytes, 0x1C, 0x04 );     // 28d
        Buffer.BlockCopy( stackPointerBytes, 0, headerBytes, 0x30, 0x04 ); // 48d

        //
        // Whole thing
        //
        // Backwards compat note:
        // Uni doesn't *write* the .exe header, but it is expecting it to be sent
        // So we don't need to strip the header even if the code starts at 0x80010000
        // 

        byte[] outBytes = new byte[ headerLength + progBytes.Length ];
        Buffer.BlockCopy( headerBytes, 0, outBytes, 0, (int)headerLength );
        Buffer.BlockCopy( progBytes, 0, outBytes, (int)headerLength, progBytes.Length );
        
        // To dump the output to a file:
        // File.WriteAllBytes( "NOPS_ELF2EXE.EXE", outBytes );

        return outBytes;

    }


#endif  // ELFSHARP

    /// <summary>
    /// Does the byte array have the 0x7F 'E' 'L' 'F' header?
    /// The header could be for the entire file, or for individual segs
    /// </summary>
    /// <param name="inBytes"></param>
    /// <returns></returns>	
    public static bool HasElfHeader( byte[] inBytes ) {

        if ( inBytes.Length < 4 )
            return false;

        UInt32 magicNumber = BitConverter.ToUInt32( inBytes, 0 );
        return (magicNumber == 0x464C457F);

    }

    /// <summary>
    /// Uploads an .exe to the address specified in the header.
    /// Uploads an .elf as .bin segments and executes based on the header section.
    /// 
    /// Note: the full header is never uploaded
    /// Note: Unirom may or may not clear .bss depending on version
    /// 
    /// </summary>
    /// <param name="inAddr">Make sure it's correct</param>
    /// <param name="inBytes">Raw bytes minus the header</param>	
    public static bool Command_SendEXE( byte[] inBytes ) {

        if ( HasElfHeader( inBytes ) ) {

#if !USE_ELFSHARP
            return Error( "Error: .ELF format not supported!" );
#else
            Log.WriteLine( "Detected .ELF file format..." );

            byte[] check = ELF2EXE( inBytes );
            if ( check == null || check.Length == 0 ) {
                return Error( "Couldn't convert this file to an .exe for sending!" );
            }

            inBytes = check;
#endif

        }

        int mod = inBytes.Length % 2048;

        // Pad .PS-EXE files up to the 2k sector boundary
        // 2MB max, 8MB for dev unit, the GC can handle this.
        if ( mod != 0 ) {

            Log.WriteLine( "Padding to 2048 bytes...\n\n", LogType.Debug );

            int paddingRequired = 2048 - mod;
            byte[] newArray = new byte[ inBytes.Length + paddingRequired ];
            for ( int i = 0; i < newArray.Length; i++ ) {
                newArray[ i ] = (i < inBytes.Length) ? inBytes[ i ] : (byte)0;
            }
            inBytes = newArray;

        }


        if ( !ChallengeResponse( CommandMode.SEND_EXE ) )
            return false;

        UInt32 checkSum = CalculateChecksum( inBytes, true );

        // An .exe with in-tact header sends the actual header over
        // followed by some choice meta data.
        //skipFirstSectorHeader = true;
        activeSerial.Write( inBytes, 0, 2048 );

        // Write in the header		
        activeSerial.Write( inBytes, 16, 4 );      // the .exe jump address
        activeSerial.Write( inBytes, 24, 4 );      // the base/write address,

        // let's not use the header-defined length, instead the actual file length minus the header
        activeSerial.Write( BitConverter.GetBytes( inBytes.Length - 0x800 ), 0, 4 );

        activeSerial.Write( BitConverter.GetBytes( checkSum ), 0, 4 );
        Log.WriteLine( "__DEBUG__Expected checksum: 0x" + checkSum.ToString( "X8" ), LogType.Debug );

        // We could send over the initial values for the fp and gp register, but 
        // GP is set via LIBSN or your Startup.s/crt0 and it's never been an issue afaik

        return WriteBytes( inBytes, true );

    }


    /// <summary>
    /// Jump immediately to the given address without
    /// touching the stack or $ra
    /// </summary>	
    public static bool Command_JumpAddr( UInt32 inAddr ) {

        if ( !ChallengeResponse( CommandMode.JUMP_JMP ) )
            return false;

        activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );

        return true;

    }

    /// <summary>
    /// Call an address with the possibility of returning
    /// Note! This may or may not be in a critical section
    /// depending on whether you're using the kernel-resident SIO debugger!
    /// </summary>	
    public static bool Command_CallAddr( UInt32 inAddr ) {

        if ( !ChallengeResponse( CommandMode.JUMP_CALL ) )
            return false;

        activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );

        return true;

    }


    //
    // Memcard Functions
    //

    /// <summary>
    /// Writes an entire memcard's contents
    /// </summary>
    /// <param name="inCard">0/1</param>	
    public static bool Command_MemcardUpload( UInt32 inCard, byte[] inFile ) {

        if ( !TransferLogic.ChallengeResponse( CommandMode.MCUP ) ) {
            return Error( "No response from Unirom. Are you using 8.0.E or higher?" );
        }

        Log.WriteLine( "Uploading card data..." );

        // send the card number
        activeSerial.Write( BitConverter.GetBytes( inCard ), 0, 4 );
        // file size in bytes, let unirom handle it
        activeSerial.Write( BitConverter.GetBytes( inFile.Length ), 0, 4 );
        activeSerial.Write( BitConverter.GetBytes( CalculateChecksum( inFile ) ), 0, 4 );

        if ( TransferLogic.WriteBytes( inFile, false ) ) {
            Log.WriteLine( "File uploaded, check your screen..." );
        } else {
            return Error( "Couldn't upload to unirom - no write attempt will be made", false );
        }

        return true;

    }

    /// <summary>
    /// Reads and dumps a memcard to disc
    /// </summary>
    /// <param name="inCard">0/1</param>	
    public static bool Command_MemcardDownload( UInt32 inCard, string fileName ) {

        if ( !TransferLogic.ChallengeResponse( CommandMode.MCDOWN ) ) {
            return Error( "No response from Unirom. Are you using 8.0.E or higher?" );
        }

        // send the card number
        activeSerial.Write( BitConverter.GetBytes( inCard ), 0, 4 );

        Log.WriteLine( "Reading card to ram..." );

        // it'll send this when it's done dumping to ram
        if ( !TransferLogic.WaitResponse( "MCRD", false ) ) {
            return Error( "Please see screen or SIO for error!" );
        }

        Log.WriteLine( "Ready, reading...." );

        UInt32 addr = TransferLogic.read32();
        Log.WriteLine( "Data is 0x" + addr.ToString( "x" ) );

        UInt32 size = TransferLogic.read32();
        Log.WriteLine( "Size is 0x" + size.ToString( "x" ) );


        Log.WriteLine( "Dumping..." );

        byte[] lastReadBytes = new byte[ size ];
        TransferLogic.ReadBytes( addr, size, lastReadBytes );


        if ( System.IO.File.Exists( fileName ) ) {
            string newFilename = fileName + GetSpan().TotalSeconds.ToString();

            Log.Write( "\n\nWARNING: Filename " + fileName + " already exists! - Dumping to " + newFilename + " instead!\n\n", LogType.Warning );

            fileName = newFilename;
        }

        try {
            File.WriteAllBytes( fileName, lastReadBytes );
        } catch ( Exception e ) {
            return Error( "Couldn't write to the output file + " + fileName + " !\nThe error returned was: " + e, false );
        }

        Log.WriteLine( "File written to: " + fileName );
        Log.WriteLine( "It is raw .mcd format used by PCSX-redux, no$psx, etc" );
        return true;

    }

    //
    // Dump
    //

    /// <summary>
    /// Dump's a RAM/ROM region to disc, auto-named
    /// </summary>	
    public static bool Command_Dump( UInt32 inAddr, UInt32 inSize, string inFileName ) {
        bool allowRewrite = false;
        string fileName = inFileName;
        byte[] lastReadBytes = new byte[ inSize ];

        if ( !ReadBytes( inAddr, inSize, lastReadBytes ) ) {
            return Error( "Failed reading from Unirom!", false );
        }

        if ( inFileName == "*" ) {
            fileName = "DUMP_" + inAddr.ToString( "X8" ) + "_to_" + inSize.ToString( "X8" ) + ".bin";
        } else {
            fileName = inFileName;
            allowRewrite = true;
        }


        if ( System.IO.File.Exists( fileName ) ) {
            if ( allowRewrite ) {
                Log.Write( "\n\nWARNING: Filename " + fileName + " already exists! Will overwrite!\n\n", LogType.Warning );
            } else {
                string newFilename = fileName.Substring( 0, fileName.Length - 4 ) + "_" + GetSpan().TotalSeconds.ToString() + ".bin";
                Log.Write( "\n\nWARNING: Filename " + fileName + " already exists! - Dumping to " + newFilename + " instead!\n\n", LogType.Warning );
                fileName = newFilename;
            }
        }

        try {
            Log.Write( "\n\nWriting dump to: " + fileName + " (overwrite:" + allowRewrite + ")\n\n" );
            File.WriteAllBytes( fileName, lastReadBytes );

        } catch ( Exception e ) {

            Error( "Couldn't write to the output file + " + fileName + " !\nThe error returned was: " + e, false );
            return false;

        }

        return true;

    }

    //
    // Debug
    //


    /// <summary>
    /// Dumps the stored registers which are saved
    /// as an interrupt triggers. $K0 is lost.
    /// </summary>	
    public static bool Command_DumpRegs() {

        if ( CPU.GetRegs() ) {
            CPU.DumpRegs( LogType.Info );
            return true;
        } else {
            Log.WriteLine( "Failed to get PSX regs - is it halted & in debug mode?", LogType.Warning );
            return false;
        }

    }

    /// <summary>
    /// Sets a register value
    /// Note: this will be applied as you /cont
    /// </summary>	
    public static bool Command_SetReg( string inReg, UInt32 inValue ) {

        // Find the index of the string value and call that specific method
        for ( int i = 0; i < (int)CPU.GPR.COUNT; i++ ) {
            if ( inReg.ToLowerInvariant() == ((CPU.GPR)i).ToString().ToLowerInvariant() ) {
                return Command_SetReg( (CPU.GPR)i, inValue );
            }
        }

        Log.WriteLine( "Unknown register: " + inReg, LogType.Debug );
        return false;

    }

    public static bool Command_SetBreakOnExec( UInt32 inAddr ) {
        if ( ChallengeResponse( CommandMode.HOOKEXEC ) ) {
            activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );
            return true;
        }

        return false;
    }

    /// <summary>
    ///  As above but typed
    /// </summary>
    public static bool Command_SetReg( CPU.GPR inReg, UInt32 inValue ) {

        Log.WriteLine( "---- Getting a copy of current registers ----", LogType.Debug );

        if ( !CPU.GetRegs() ) {
            Log.WriteLine( "Couldn't get regs to modify - is the PSX in debug mode?", LogType.Warning );
            return false;
        }

        // TODO: shouldn't really be modifying shit in another class
        // even if it is static
        CPU.tcb.regs[ (int)inReg ] = inValue;

        Log.WriteLine( "---- Done, writing regs back ----", LogType.Debug );

        return CPU.SetRegs();

    }

    // Ping? Pong!
    public static void WriteChallenge( string inChallenge ) {

        activeSerial.Write( inChallenge );

    }

    private static bool didShowUpgradewarning = false;

    /// <summary>
    /// Wait for a response to see if this version of
    /// Unirom supports the V2 protocol
    /// </summary>	
    public static bool WaitResponse( string inResponse, bool verbose = true, int timeoutMillis = 0 ) {

        Program.protocolVersion = 1;

        // Dump the response into a buffer..
        // (byte by byte so we can compare the challenge/response)
        // e.g. it may start spewing data immediately after and we
        // have to catch that.
        // note: the attribute extensions use 40ish bytes of memory per pop

        string responseBuffer = "";

        if ( verbose )
            Log.WriteLine( "Waiting for response or protocol negotiation: ", LogType.Debug );

        DateTime timeoutStartTime = DateTime.Now;
        DateTime timeoutEndTime = timeoutStartTime.AddMilliseconds( timeoutMillis );

        while ( true ) {

            if ( activeSerial.BytesToRead != 0 ) {

                responseBuffer += (char)activeSerial.ReadByte();

                // filter any noise at the start of the response
                // seems to happen once in a while
                if ( responseBuffer.Length > 4 )
                    responseBuffer = responseBuffer.Remove( 0, 1 );

                if ( verbose )
                    Log.Write( "\r InputBuffer: " + responseBuffer, LogType.Debug );

                // command unsupported in debug mode
                if ( responseBuffer == "UNSP" ) {
                    Log.WriteLine( "\nNot supported while Unirom is in debug mode!", LogType.Error );
                    return false;
                }

                if ( responseBuffer == "HECK" ) {
                    Log.WriteLine( "\nCouldn't read the memory card!", LogType.Error );
                    return false;
                }

                if ( responseBuffer == "ONLY" ) {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Log.WriteLine( "\nOnly supported while Unirom is in debug mode!", LogType.Error );
                    return false;
                }

                if (
                    !didShowUpgradewarning
                    && responseBuffer.Length >= 4
                    && responseBuffer.Substring( 0, 3 ) == "OKV"
                    && (byte)responseBuffer[ 3 ] > (byte)'3'
                ) {
                    didShowUpgradewarning = true;
                    Log.WriteLine();
                    Log.Write( "================================================================================\n", LogType.Warning );
                    Log.Write( "   Just a heads up!\n" );
                    Log.Write( "   This version of Unirom appears to be much newer than your version of NoPS.\n", LogType.Warning );
                    Log.Write( "   Time for an upgrade? github.com/JonathanDotCel/ \n", LogType.Warning );
                    Log.Write( "================================================================================\n", LogType.Warning );
                }

                // upgrade to V3 with the DJB2 checksum algo
                if ( responseBuffer == "OKV3" && Program.protocolVersion == 1 ) {
                    Log.WriteLine( "\nUpgraded connection to protocol V3!" );
                    activeSerial.Write( "UPV3" );
                    Program.protocolVersion = 3;
                }

                // upgrade to V2 with individual checksum
                if ( responseBuffer == "OKV2" && Program.protocolVersion == 1 ) {
                    Log.WriteLine( "\nUpgraded connection to protocol V2!" );
                    activeSerial.Write( "UPV2" );
                    Program.protocolVersion = 2;
                }

                // now whether we've upgraded protocol or not:
                if ( responseBuffer == inResponse ) {
                    if ( verbose )
                        Log.WriteLine( "\nGot response: " + responseBuffer, LogType.Debug );
                    break;
                }

            } // if bytes to read > nuffink

            // nope!
            if ( timeoutMillis > 0 && DateTime.Now > timeoutEndTime ) {
                return false;
            }

        }

        return true;

    }

    //
    // Halt the PSX (if debug stub is installed)
    // Holds it in a tight wait loop in an exception/int/crit state
    //
    public static bool Halt( bool notifyGDB, int timeoutMillis = 0 ) {

        lock ( SerialTarget.serialLock ) {
            bool rVal = ChallengeResponse( CommandMode.HALT, timeoutMillis );
            if ( rVal && notifyGDB ) {
                GDBServer.SetHaltStateInternal( CPU.HaltState.HALT, notifyGDB );
            }
            return rVal;
        }

    }

    //
    // Continue the PSX from a halted state or exception
    //
    public static bool Cont( bool notifyGDB, int timeoutMillis = 0 ) {

        lock ( SerialTarget.serialLock ) {
            bool rVal = ChallengeResponse( CommandMode.CONT, timeoutMillis );
            if ( rVal && notifyGDB ) {
                GDBServer.SetHaltStateInternal( CPU.HaltState.RUNNING, notifyGDB );
            }
            return rVal;
        }

    }

    /// <summary>
    /// Deceptively small function, but one of the most important
    /// This is the one that sends e.g. "/poke" and  checks that Unirom is paying attention
    /// </summary>	
    public static bool ChallengeResponse( CommandMode inMode, int timeoutMillis = 0 ) {
        return ChallengeResponse( inMode.challenge(), inMode.response(), timeoutMillis );
    }

    public static bool ChallengeResponse( string inChallenge, string expectedResponse, int timeoutMillis = 0 ) {

        // Now send the challenge code and wait
        Log.WriteLine( "Waiting for the PS1, C/R=" + inChallenge + "/" + expectedResponse + "....\n\n", LogType.Debug );

        WriteChallenge( inChallenge );

        // TODO: could this be an issue when connecting over TCP?
        Thread.Sleep( 50 );

        bool returnVal = WaitResponse( expectedResponse, false, timeoutMillis );
        Log.WriteLine( "Response: " + returnVal );
        return returnVal;

    }


    // HEY!
    // Remember to tell the PSX to expect bytes first... BIN, ROM, EXE, etc
    // as this will attempt to use the V2 protocol rather than just spamming 
    // bytes into the void
    public static bool WriteBytes( byte[] inBytes, bool skipFirstSector, bool forceProtocolV3 = false ) {


        // .exe files go [ header ][ meta ][ data @ write address ]
        // .rom files go [ meta ][ data @ 0x80100000 ]
        // .bin files go [ size ][ data @ 0xWRITEADR ]

        int start = skipFirstSector ? 2048 : 0;       // for .exes

        int chunkSize = 2048;                               // 2048 seems the most stable
        int numChunks = inBytes.Length / chunkSize + (inBytes.Length % chunkSize == 0 ? 0 : 1);

        int waityCakes = 0;                                 // Kinda extraneous, but it's interesting to watch


        // we already sent the first one?
        for ( int i = start; i < inBytes.Length; i += chunkSize ) {

            retryThisChunk:

            ulong chunkChecksum = 0;

            // Are we about to go out of range?
            // .NET doesn't care if you specify 2kb when you're only e.g. 1.7kb from the boundary
            // but it's best to declare explicityly			
            if ( i + chunkSize >= inBytes.Length )
                chunkSize = inBytes.Length - i;

            // write 1 chunk worth of bytes
            activeSerial.Write( inBytes, i, chunkSize );

            // update the expected checksum value
            for ( int j = 0; j < chunkSize; j++ ) {
                chunkChecksum += inBytes[ i + j ];
            }

            while ( activeSerial.BytesToWrite != 0 ) {
                waityCakes++;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            int percent = (i + 1) * 100 / (inBytes.Length);
            Console.Write( "\r Sending chunk {0} of {1} ({2})%", ((i / chunkSize) + 1) > numChunks ? numChunks : ((i / chunkSize) + 1), numChunks, percent );

            SetDefaultColour();

            bool useCorrective = (Program.protocolVersion >= 2 || forceProtocolV3) && !activeSerial.SkipAcks;
            if ( useCorrective ) {

                // Format change as of 8.0.C
                // every 2k, we'll send back a "MORE" from Unirom
                // Checksum change as of 8.0.L
                // every 2k, we'll use a DJB checksum, instead of summing bytes

                Log.Write( " ... " );

                string cmdBuffer = "";

                TimeSpan startSpan = GetSpan();
                while ( cmdBuffer != "CHEK" ) {

                    if ( activeSerial.BytesToRead != 0 ) {

                        cmdBuffer += (char)activeSerial.ReadByte();

                    }
                    while ( cmdBuffer.Length > 4 )
                        cmdBuffer.Remove( 0, 1 );

                }

                // did it ask for a checksum?
                if ( cmdBuffer == "CHEK" ) {

                    Log.Write( "Sending checksum...", LogType.Debug );

                    activeSerial.Write( BitConverter.GetBytes( chunkChecksum ), 0, 4 );
                    Thread.Sleep( 1 );

                    startSpan = GetSpan();

                    while ( cmdBuffer != "MORE" && cmdBuffer != "ERR!" ) {

                        if ( activeSerial.BytesToRead != 0 ) {
                            char readVal = (char)activeSerial.ReadByte();
                            cmdBuffer += readVal;
                            Log.Write( readVal.ToString() );
                        }
                        while ( cmdBuffer.Length > 4 ) {
                            cmdBuffer = cmdBuffer.Remove( 0, 1 );
                        }

                    }

                    if ( cmdBuffer == "ERR!" ) {
                        Log.WriteLine( "... Retrying\n", LogType.Warning );
                        goto retryThisChunk;
                    }

                    if ( cmdBuffer == "MORE" ) {
                        //Console.Write( "... OK\n" );
                    }

                }

                // if it didn't ask for one, crack on.


            } // corrective transfer

            Log.Write( " DONE\n" );

        }

        // might have to terminate previous line
        Log.WriteLine( "\nSend finished!\n" );

        return true;

    } // WriteBytes



    // C people: remember the byte[] is a pointer....
    /// <summary>
    /// Reads an array of bytes from the serial connection
    /// </summary>		
    public static bool ReadBytes( UInt32 inAddr, UInt32 inSize, byte[] inBytes ) {

        if ( !ChallengeResponse( CommandMode.DUMP ) ) {
            return false;
        }

        // the handshake is done, let's tell it where to start
        activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );
        activeSerial.Write( BitConverter.GetBytes( inSize ), 0, 4 );

        return ReadBytes_Raw( inSize, inBytes );

    } // DUMP

    public static bool ReadBytes_Raw( UInt32 inSize, byte[] inBytes ) {


        // now go!
        int arrayPos = 0;
        //lastReadBytes = new byte[inSize];

        // Let the loop time out if something gets a bit fucky.			
        TimeSpan lastSpan = GetSpan();
        TimeSpan currentSpan = GetSpan();

        UInt32 checksumV2 = 0;
        UInt32 checksumV3 = 5381;

        while ( true ) {

            currentSpan = GetSpan();

            if ( activeSerial.BytesToRead != 0 ) {

                lastSpan = GetSpan();

                byte responseByte = (byte)activeSerial.ReadByte();
                inBytes[ arrayPos ] = (responseByte);

                arrayPos++;

                checksumV2 += (UInt32)responseByte;
                checksumV3 = ((checksumV3 << 5) + checksumV3) ^ responseByte;

                if ( arrayPos % 2048 == 0 ) {
                    activeSerial.Write( "MORE" );
                }

                if ( arrayPos % 1024 == 0 ) {
                    long percent = (arrayPos * 100) / inSize;
                    Log.Write( $"\r Offset {arrayPos} of {inSize} ({percent})%\n" );
                }

                if ( arrayPos >= inBytes.Length ) {
                    break;
                }

            }

            // if we've been without data for more than 2 seconds, something's really up				
            if ( (currentSpan - lastSpan).TotalMilliseconds > 2000 ) {
                if ( arrayPos == 0 ) {
                    Error( "There was no data for a long time! 0 bytes were read!", false );
                    return false;
                } else {
                    Error( "There was no data for a long time! Will try to dump the " + arrayPos + " (" + arrayPos.ToString( "X8" ) + ") bytes that were read!", false );
                }

                return false;
            }


        }

        Log.WriteLine( "Read Complete!", LogType.Debug );

        // Read 4 more bytes for the checksum

        // Let the loop time out if something gets a bit fucky.			
        lastSpan = GetSpan();
        UInt32 expectedChecksum = 0;

        SetDefaultColour();
        Log.WriteLine( "Checksumming the checksums for checksummyness.\n", LogType.Debug );

        try {

            for ( int i = 0; i < 4; i++ ) {

                while ( activeSerial.BytesToRead == 0 ) {

                    currentSpan = GetSpan();

                    if ( (currentSpan - lastSpan).TotalMilliseconds > 2000 ) {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Log.WriteLine( "Error reading checksum byte " + i + " of 4!", LogType.Error );
                        break;
                    }

                }

                lastSpan = GetSpan();

                byte inByte = (byte)activeSerial.ReadByte();

                // and shift it ino the expected checksum
                expectedChecksum |= (UInt32)(inByte << (i * 8));

            }

        } catch ( System.TimeoutException ) {

            Console.ForegroundColor = ConsoleColor.Red;
            Error( "No checksum sent, continuing anyway!\n ", false );

        }

        if ( (expectedChecksum != checksumV2) && (expectedChecksum != checksumV3) ) {
            Console.ForegroundColor = ConsoleColor.Red;
            Error( "Checksum missmatch!\n    Expected    : 0x" + expectedChecksum.ToString( "X8" ) + "\n    Calced (V2) : 0x" + checksumV2.ToString( "X8" ) + "\n    Calced (V3) : 0x" + checksumV3.ToString( "X8" ), false );
            Log.WriteLine( "    May attempt to continue..." );
            return false;
        } else {
            SetDefaultColour();
            Log.WriteLine( " Checksums match: " + expectedChecksum.ToString( "X8" ) + "\n", LogType.Debug );
        }


        if ( activeSerial.BytesToRead > 0 ) {
            Console.ForegroundColor = ConsoleColor.Red;
            Error( "Extra bytes still being sent from the PSX! - Will attempt to save file anyway!", false );
        }

        SetDefaultColour();

        return true;

    }

    /// <summary>
    /// Install a read/write/exec hook at the given address
    /// The CPU supports 1 hook address (1-3 types on it)
    /// The protocol supports 1 address/type combo
    /// </summary>
    public static bool HookAddr( CommandMode inMode, UInt32 inAddr ) {

        if (
            inMode == CommandMode.HOOKREAD
            || inMode == CommandMode.HOOKWRITE
            || inMode == CommandMode.HOOKEXEC
        ) {
            if ( ChallengeResponse( inMode ) )
                Log.WriteLine( "GOT HOOK REQUEST FOR ADDRESS 0x" + inAddr.ToString( "X8" ), LogType.Debug );
            activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );
            return true;
        }

        return false;

    }

    /// <summary>
    /// Remove an existing hook installed by HookAddr or otherwise. 
    /// </summary>
    public static bool Unhook() {

        if ( ChallengeResponse( CommandMode.UNHOOK ) ) {
            Log.WriteLine( "Unhooked!", LogType.Debug );
            return true;
        }

        return false;

    }


#pragma warning disable CS0162

    /// <summary>
    /// Semi-supported: 
    /// Constantly reads from the address specified and dumps it to screen
    /// </summary>	
    public static bool Watch( UInt32 inAddr, UInt32 inSize ) {

        if ( !ChallengeResponse( CommandMode.WATCH ) )
            return false;

        int bytesRead = 0;
        int arrayPos = 0;
        byte[] lastReadBytes = new byte[ inSize ];

        // the handshake is done, let's tell it where to start
        arrayPos = 0;
        activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );
        activeSerial.Write( BitConverter.GetBytes( inSize ), 0, 4 );

        while ( true ) {

            // Keep reading bytes until we've got as many back as we've asked for

            if ( activeSerial.BytesToRead != 0 ) {

                // still bothers me that it reads an int...
                byte responseByte = (byte)activeSerial.ReadByte();
                lastReadBytes[ arrayPos ] = (responseByte);

                bytesRead++;
                arrayPos++;

                // filled the buffer? Print it

                if ( arrayPos >= lastReadBytes.Length ) {

                    Console.Clear();
                    Log.Write( "Watching address range 0x" + inAddr.ToString( "X8" ) + " to 0x" + (inAddr + inSize).ToString( "X8" ) + "\n", LogType.Debug );
                    Log.Write( "Bytes read " + bytesRead + "\n\n", LogType.Debug );

                    for ( int i = 0; i < lastReadBytes.Length; i++ ) {

                        Log.Write( lastReadBytes[ i ].ToString( "X2" ) + " " );

                        // Such a janky way to do it, but is saves appending
                        // tons and tons of strings together
                        if ( i % 16 == 15 ) {

                            // print the actual char values

                            for ( int j = i - 15; j <= i; j++ ) {

                                Log.Write( " " + (char)lastReadBytes[ j ] );

                            }

                            // then draw the character data								
                            Log.Write( "\n" );

                        }

                    }

                    if ( activeSerial.BytesToRead != 0 ) {
                        Log.Write( "\nTerminator bytes: " );
                        while ( activeSerial.BytesToRead != 0 ) {
                            int x = activeSerial.ReadByte();
                            Log.Write( x.ToString( "X2" ) + " " );
                        }
                        Log.Write( "\n" );
                    }


                    // slow it down a touch

                    // give the PSX time to do stuff
                    Thread.Sleep( 200 );

                    // Just start over...					
                    ChallengeResponse( CommandMode.WATCH.challenge(), CommandMode.WATCH.response() );

                    // start over
                    arrayPos = 0;
                    activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );
                    activeSerial.Write( BitConverter.GetBytes( inSize ), 0, 4 );

                }

            } // bytestoread

        } // while true

        return true;

    }
#pragma warning restore CS0162

    /*
    /// <summary>
    /// Leaves the serial connection open
    /// Will attempt to detect /HALT notifications from Unirom
    /// and catch crash/exception events
    /// </summary>    
    public static void DoMonitor(){

        // Note:
        // Mono hasn't implemented the activeSerial.ReceivedBytesThreshold methods yet
        // so we can't really use events. Instead the max we'll wait is 1ms to enter the
        // tight inner loop. Should be fine if you're not filling a ~2kb buffer in 1ms

        // a rolling buffer of the last 4 things recieved
        string lastMonitorBytes = "";

        while (true)
        {

            while (activeSerial.BytesToRead > 0)
            {

                // echo things to the screen

                int responseByte = activeSerial.ReadByte();
                Console.Write((char)(responseByte));

                // check if the PSX has crashed

                // TODO: use a more appropriate data collection, lol
                lastMonitorBytes += (char)responseByte;
                while ( lastMonitorBytes.Length > 4 )
                    lastMonitorBytes = lastMonitorBytes.Remove( 0, 1 );

                if ( lastMonitorBytes == "HLTD" ){

                    while ( true ){

                        Console.WriteLine( "\nThe PSX may have crashed, enter debug mode? (y/n)" );                        
                        Console.WriteLine( "(Also starts a TCP/SIO bridge on port 3333." );
                        ConsoleKeyInfo c = Console.ReadKey();
                        if ( c.KeyChar.ToString().ToLowerInvariant() == "y" ){                            
                            GDB.GetRegs();
                            GDB.DumpRegs();
                            GDB.Init( 3333 );                            
                            return;
                        } else {						
                            Console.WriteLine( "\nReturned to monitor mode." );
                            break;
                        }

                    }


                }

            }

            Thread.Sleep( 1 );

        }


    }
    */

    /// <summary>
    /// Puts Unirom into /debug mode and wipes everything from the end of the kernel
    /// to the stack, where it will crash.
    //  0x80010000 -> 0x801FFF??
    /// </summary>
    /// <param name="wipeValue">32 bit value to fill ram with</param>
    /// <returns></returns>
    public static bool Command_WipeMem( UInt32 wipeAddress, UInt32 wipeValue ) {

        // if it returns true, we might enter /m (monitor) mode, etc
        if (
            !TransferLogic.ChallengeResponse( CommandMode.DEBUG )
        ) {
            Log.WriteLine( "Couldn't determine if Unirom is in debug mode.", LogType.Warning );
            return false;
        }

        Thread.Sleep( 200 );

        byte[] buffer = new byte[ 0x80200000 - wipeAddress ]; // just shy of 2MB

        for ( int i = 0; i < buffer.Length / 4; i++ ) {
            BitConverter.GetBytes( wipeValue ).CopyTo( buffer, i * 4 );
        }

        Command_SendBin( wipeAddress, buffer );

        // It won't return.
        return true;

    }

    /// <summary>
    /// Returns a checksum for the given bytes based on the current protocol version
    /// </summary>	
    /// <param name="skipFirstSector">Skip the first 0x800 header sector on an .exe as it won't be sent over SIO</param>	
    public static UInt32 CalculateChecksum( byte[] inBytes, bool skipFirstSector = false ) {

        if ( Program.protocolVersion == 3 ) {
            // Less weak checksum
            UInt32 returnVal = 5381;
            for ( int i = (skipFirstSector ? 2048 : 0); i < inBytes.Length; i++ ) {
                returnVal = ((returnVal << 5) + returnVal) ^ inBytes[ i ];
            }
            return returnVal;
        } else {
            // Weak checksum
            UInt32 returnVal = 0;
            for ( int i = (skipFirstSector ? 2048 : 0); i < inBytes.Length; i++ ) {
                returnVal += (UInt32)inBytes[ i ];
            }
            return returnVal;
        }

    }

}
