// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.IO;
using System.IO.Ports;
using System.Text;
using static Utils;
#if USE_ELFSHARP
using ELFSharp.ELF;
using ELFSharp.ELF.Sections;
using ELFSharp.ELF.Segments;
#endif

public class TransferLogic
{

	public static SerialPort activeSerial => Program.activeSerial;

	/// <summary>
	/// Read a 32 bit unsigned int from the serial connection
	/// (Takes care of endianness)
	/// </summary>	
	public static UInt32 read32(){

		UInt32 val = (UInt32)activeSerial.ReadByte();
		val += ((UInt32) activeSerial.ReadByte() << 8 );
		val += ((UInt32) activeSerial.ReadByte() << 16);
		val += ((UInt32) activeSerial.ReadByte() << 24);

		return val;

	}

	/// <summary>
	/// Upload bytes to the specified address
	/// does verify contents
	/// does not execute or act upon the data
	/// </summary>	
	public static bool Command_SendBin( UInt32 inAddr, byte[] inBytes ){

		UInt32 checkSum = CalculateChecksum(inBytes);

		if (!ChallengeResponse( CommandMode.SEND_BIN ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes( inAddr ), 0, 4);
		activeSerial.Write(BitConverter.GetBytes( inBytes.Length ), 0, 4);
		// In a pinch, Unirom will gloss over a null checksum. Don't though.
		activeSerial.Write(BitConverter.GetBytes( checkSum ), 0, 4);

		// then the actual contents.

		return WriteBytes( inBytes, false );

	}

	/// <summary>
	/// Upload a ROM and attempt to flash to EEPROM
	/// </summary>	
	public static bool Command_SendROM( UInt32 inAddr, byte[] inBytes ){

        if ( inBytes.Length >= 15 ){

            string license1 = Encoding.ASCII.GetString( inBytes, 0x04, 11 );
            string license2 = Encoding.ASCII.GetString( inBytes, 0x84, 11 );

            bool safe = (license1 == "Licensed by") || (license2 == "Licensed by");

            if ( !safe ){

                Console.WriteLine( "Hey hey hey hey! This doesn't look like a ROM. Maybe an .exe?" );
                Console.WriteLine( "Are you sure you want to flash this?" );
                ConsoleKeyInfo c = Console.ReadKey();
                if ( c.KeyChar.ToString().ToLowerInvariant() != "y" ){
                    return false;
                }

            }
            
        }
        
		UInt32 checkSum = CalculateChecksum(inBytes);

		if ( !ChallengeResponse( CommandMode.SEND_ROM ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes(inBytes.Length), 0, 4);
		activeSerial.Write(BitConverter.GetBytes(checkSum), 0, 4);

		string flashResponse = "";

		while (true)
		{

			if (activeSerial.BytesToRead != 0)
			{
				// Why the fuck does readchar return an int?
				flashResponse += (char)activeSerial.ReadByte();

				// filter any noise at the start of the response
				// seems to happen once in a while
				if (flashResponse.Length > 4)
					flashResponse = flashResponse.Remove(0, 1);


			}

			Console.Write("\r EEPROM Check: " + flashResponse);

			if (flashResponse == "FITS")
			{
				Console.WriteLine("\n\nRom will fit! \n Response: " + flashResponse + "!");
				break;
			}

			if (flashResponse == "NOPE")
			{
				Console.WriteLine("\n\nThis rom is too big for the EEPROM! \n Response: " + flashResponse + "!");
				return false;
			}

			if (flashResponse == "NONE")
			{
				Console.WriteLine("\n\nNo EEPROM detected! \n The response was: " + flashResponse + "!");
				return false;
			}

			if (flashResponse == "UNKN")
			{
				Console.WriteLine("\n\nUnknown EEPROM detected! \n The response was: " + flashResponse + "!");
				return false;
			}


		}

		Console.WriteLine( "Checks passed; sending ROM!" );

		return WriteBytes( inBytes, false );

	}


#if USE_ELFSHARP

	/// <summary>
	/// Dumps the Sections and Segments from an .ELF loaded via ElfSharp
	/// </summary>
	/// <param name="inElf"></param>
	public static void DumpElfInfo( IELF inElf ){

		ConsoleColor oldColor = Console.ForegroundColor;

		Console.WriteLine( "\nNum ELF sections: " + inElf.Sections.Count );

		for( int i = 0; i < inElf.Sections.Count; i++ ){

			Section<UInt32> sect = (inElf.Sections[i] as Section<UInt32>);

			Console.ForegroundColor = (sect.Size == 0)? ConsoleColor.Red : oldColor;

			Console.WriteLine( $"Section {i}: {sect.Name}" );			
			Console.WriteLine( $"  Addr: 0x{sect.LoadAddress.ToString("X")}" );
			Console.WriteLine( $"  Size: 0x{sect.Size.ToString( "X" )} (0x{sect.EntrySize.ToString("X")})" );
			Console.WriteLine( $"  Flags: {sect.Flags}" );
			
			//byte[] b = sect.GetContents();
			//File.WriteAllBytes( "sect_" + sect.Name, b );

		}

		Console.WriteLine( "\nNum ELF segments: " + inElf.Segments.Count );

		for ( int i = 0; i < inElf.Segments.Count; i++ ) {

			Segment<UInt32> seg = inElf.Segments[i] as Segment<UInt32>;

			// Some segs have the .elf magic number
			Console.ForegroundColor = HasElfHeader( seg.GetFileContents() ) ? ConsoleColor.Red : oldColor;
			
			Console.WriteLine( "Segment " + i );
			Console.WriteLine( $"  Offset   : 0x{seg.Offset.ToString("X")}");
			Console.WriteLine( $"  Size     : 0x{seg.Size.ToString("X")}  (0x{seg.FileSize.ToString("X")})" );
			Console.WriteLine( $"  PhysAddr : 0x{seg.PhysicalAddress.ToString("X")} for 0x{seg.Address.ToString("X")}" );			
			Console.WriteLine( $"  Flags    : " + seg.Flags );
			Console.WriteLine( $"  Type     : " + seg.Type );

			//byte[] b = seg.GetFileContents();
			//File.WriteAllBytes( "seg_" + i, b );

		}

		Console.ForegroundColor = oldColor;

	}


	/// <summary>
	///  Convert a .ELF to an .EXE
	/// </summary>	
	public static byte[] ELF2EXE( byte[] inBytes ){

		// Is it actually an elf tho?
		// Maybe it's a sneaky pixie.
		if ( !HasElfHeader( inBytes ) ){
			Error( "This file doesn't have a valid .ELF header!" );
			return null;
		}

		MemoryStream mStream = new MemoryStream( inBytes );
		IELF elfy = ELFReader.Load( mStream, true );

		DumpElfInfo( elfy );
		
		// TODO: allow for larger RAM mods?		
		UInt32 ramLength = 0x80200000 - 0x80000000;

		// Let's build an .exe!
		UInt32 seekPos = 0;
		byte[] outBytes = new byte[ramLength];

		// Start with the header section:

		for( int i = 0; i < elfy.Sections.Count; i++ ){

			Section<UInt32> sect = elfy.Sections[i] as Section<UInt32>;

			// Assume it's the header, since the 'PS-EXE' ASCII isn't guaranteed
			if ( sect.Size == 0x800 ){

				sect.GetContents().CopyTo( outBytes, seekPos );
				seekPos += sect.Size;
				break;

			}

		}

		if ( seekPos == 0 ){			
			Error( "Couldn't find a PS-EXE header!" );
			return null;
		}

		Segment<UInt32> lastAddedSegment = null;

		// Add the relevant segments:

		for( int i = 0; i < elfy.Segments.Count; i++ ){

			Segment<UInt32> ss = elfy.Segments[ i ] as Segment<UInt32>;

			// Usually 0x00010000 lower than the .exe starts
			// E.g. would nuke the full kernel area for a program at 0x80010000			
			bool segmentHasElfHeader = HasElfHeader( ss.GetFileContents() );

			Console.WriteLine( "\nSending Segment " + i );
			Console.WriteLine( $"  Offset   : 0x{ss.Offset.ToString( "X" )}  Size  : 0x{ss.Size.ToString( "X" )}" );
			Console.WriteLine( $"  PhysAddr : 0x{ss.PhysicalAddress.ToString( "X" )} for 0x{ss.Address.ToString( "X" )}" );
			Console.WriteLine( $"  ElfHddr  : {segmentHasElfHeader}" );
					
			if ( segmentHasElfHeader || ss.Size == 0 ) {
				Console.WriteLine( "Skipping..." );
				continue;
			}

			if ( lastAddedSegment == null ){
				// First segment always goes right on the end of the header
			} else {
				// Else we'll judge the next segment start based on the disance between
				// their physAddrs. So if there's a gap, it doesn't matter.
				// E.g. when nextSeg.Start is bigger than (lastSeg.Start + lastSeg.Length)
				seekPos += (ss.PhysicalAddress - lastAddedSegment.PhysicalAddress);
			}

			ss.GetFileContents().CopyTo( outBytes, seekPos );
			lastAddedSegment = ss;

		}

		if ( lastAddedSegment == null ){			
			Error( "Couldn't find any segments to send!" );
			return null;
		}

		UInt32 fileLength = seekPos + lastAddedSegment.Size;

		// Trim the array to use only as long as the .exe requires.
		Array.Resize<byte>( ref outBytes, (int)fileLength );

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
	public static bool Command_SendEXE( byte[] inBytes ){
		
		if ( HasElfHeader( inBytes ) ){
		
#if !USE_ELFSHARP
			return Error( "Error: .ELF format not supported!" );
#else
			Console.WriteLine( "Detected .ELF file format..." );
			
			byte[] check = ELF2EXE( inBytes );
			if (  check == null || check.Length == 0 ){
				return Error( "Couldn't convert this file to an .exe for sending!" );				
			}

			inBytes = check;
#endif

		}

		UInt32 checkSum = CalculateChecksum(inBytes, true);

		int mod = inBytes.Length % 2048;

		// Pad .PS-EXE files up to the 2k sector boundary
		// 2MB max, 8MB for dev unit, the GC can handle this.
		if (mod != 0)
		{

			Console.WriteLine("Padding to 2048 bytes...\n\n");

			int paddingRequired = 2048 - mod;
			byte[] newArray = new byte[inBytes.Length + paddingRequired];
			for (int i = 0; i < newArray.Length; i++)
			{
				newArray[i] = (i < inBytes.Length) ? inBytes[i] : (byte)0;
			}
			inBytes = newArray;

		}


		if ( !ChallengeResponse( CommandMode.SEND_EXE )	)
			return false;

		// An .exe with in-tact header sends the actual header over
		// followed by some choice meta data.
		//skipFirstSectorHeader = true;
		activeSerial.Write(inBytes, 0, 2048);

		// Write in the header		
		activeSerial.Write(inBytes, 16, 4);      // the .exe jump address
		activeSerial.Write(inBytes, 24, 4);      // the base/write address, e.g. where the linker org'd it
												//serialPort.Write( inFile, 28, 4 );		// size
												// let's not use the header-defined length, instead the actual file length minus the header
		activeSerial.Write(BitConverter.GetBytes(inBytes.Length - 0x800), 0, 4);

		activeSerial.Write(BitConverter.GetBytes(checkSum), 0, 4);
		Console.WriteLine("__DEBUG__Expected checksum: 0x" + checkSum.ToString("X8"));

		// We could send over the initial values for the fp and gp register, but 
		// GP is set via LIBSN or your Startup.s/crt0 and it's never been an issue afaik

		return WriteBytes( inBytes, true);

	}


	/// <summary>
	/// Jump immediately to the given address without
	/// touching the stack or $ra
	/// </summary>	
	public static bool Command_JumpAddr( UInt32 inAddr ){

		if ( !ChallengeResponse( CommandMode.JUMP_JMP ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);

		return true;

	}

	/// <summary>
	/// Call an address with the possibility of returning
	/// Note! This may or may not be in a critical section
	/// depending on whether you're using the kernel-resident SIO debugger!
	/// </summary>	
	public static bool Command_CallAddr( UInt32 inAddr ){
		
		if ( !ChallengeResponse( CommandMode.JUMP_CALL ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);

		return true;

	}


	//
	// Memcard Functions
	//

	/// <summary>
	/// Writes an entire memcard's contents
	/// </summary>
	/// <param name="inCard">0/1</param>	
	public static bool Command_MemcardUpload( UInt32 inCard, byte[] inFile ){

		if ( !TransferLogic.ChallengeResponse( CommandMode.MCUP ) ){
			return Error( "No response from Unirom. Are you using 8.0.E or higher?" );
		}

		Console.WriteLine("Uploading card data...");

		// send the card number
		activeSerial.Write( BitConverter.GetBytes(inCard), 0, 4 );
		// file size in bytes, let unirom handle it
		activeSerial.Write( BitConverter.GetBytes( inFile.Length), 0, 4 );
		activeSerial.Write( BitConverter.GetBytes( CalculateChecksum(inFile) ), 0, 4);

		if (TransferLogic.WriteBytes(inFile, false))
		{
			Console.WriteLine("File uploaded, check your screen...");			
		}
		else
		{
			return Error("Couldn't upload to unirom - no write attempt will be made", false);
		}

		return true;

	}

	/// <summary>
	/// Reads and dumps a memcard to disc
	/// </summary>
	/// <param name="inCard">0/1</param>	
	public static bool Command_MemcardDownload( UInt32 inCard, string fileName ){

		if ( !TransferLogic.ChallengeResponse(CommandMode.MCDOWN) ){
			return Error( "No response from Unirom. Are you using 8.0.E or higher?" );
		}

		// send the card number
		activeSerial.Write(BitConverter.GetBytes(inCard), 0, 4);

		Console.WriteLine("Reading card to ram...");

		// it'll send this when it's done dumping to ram
		if (!TransferLogic.WaitResponse("MCRD", false))
		{
			return Error("Please see screen or SIO for error!");
		}

		Console.WriteLine("Ready, reading....");

		UInt32 addr = TransferLogic.read32();
		Console.WriteLine("Data is 0x" + addr.ToString("x"));

		UInt32 size = TransferLogic.read32();
		Console.WriteLine("Size is 0x" + size.ToString("x"));


		Console.WriteLine("Dumping...");

		byte[] lastReadBytes = new byte[size];
		TransferLogic.ReadBytes(addr, size, lastReadBytes);

		
		if (System.IO.File.Exists(fileName))
		{
			string newFilename = fileName + GetSpan().TotalSeconds.ToString();

			Console.Write("\n\nWARNING: Filename " + fileName + " already exists! - Dumping to " + newFilename + " instead!\n\n");

			fileName = newFilename;
		}

		try
		{
			File.WriteAllBytes(fileName, lastReadBytes);
		}
		catch (Exception e)
		{
			return Error("Couldn't write to the output file + " + fileName + " !\nThe error returned was: " + e, false);			
		}

		Console.WriteLine("File written to: " + fileName);
		Console.WriteLine("It is raw .mcd format used by PCSX-redux, no$psx, etc");
		return true;

	}

	//
	// Dump
	//

	/// <summary>
	/// Dump's a RAM/ROM region to disc, auto-named
	/// </summary>	
	public static bool Command_Dump( UInt32 inAddr, UInt32 inSize ){

		byte[] lastReadBytes = new byte[inSize];

		if ( !ReadBytes( inAddr, inSize, lastReadBytes) ){
			return Error( "Couldn't ready bytes from Unirom!" );
		}

		string fileName = "DUMP_" + inAddr.ToString("X8") + "_to_" + inSize.ToString("X8") + ".bin";

		if (System.IO.File.Exists(fileName))
		{

			string newFilename = fileName + GetSpan().TotalSeconds.ToString();

			Console.Write("\n\nWARNING: Filename " + fileName + " already exists! - Dumping to " + newFilename + " instead!\n\n");

			fileName = newFilename;

		}

		try
		{

			File.WriteAllBytes(fileName, lastReadBytes);

		}
		catch (Exception e)
		{

			Error("Couldn't write to the output file + " + fileName + " !\nThe error returned was: " + e, false);
			return false;

		}

        return true;

	}

	//
	// Debug
	//

	/// <summary>
	/// Halt the system by entering a SIO-wait loop in an interrupt/critical section
	/// </summary>	
	public static bool Command_Halt(){
		
		return ChallengeResponse( CommandMode.HALT );

	}

	/// <summary>
	/// UnHalt
	/// </summary>	
	public static bool Command_CONT(){
		
		return ChallengeResponse( CommandMode.CONT );

	}
	
	/// <summary>
	/// Dumps the stored registers which are saved
	/// as an interrupt triggers. $K0 is lost.
	/// </summary>	
	public static bool Command_DumpRegs(){
		
		if ( GDB.GetRegs() ){
			GDB.DumpRegs();
			return true;
		} else {
			Console.WriteLine( "Couldn't get regs" );
			return false;
		}
		
	}

	/// <summary>
	/// Sets a register value
	/// Note: this will be applied as you /cont
	/// </summary>	
	public static bool Command_SetReg( string inReg, UInt32 inValue ){
		
		// Find the index of the string value and call that specific method
		for ( int i = 0; i < (int)GPR.COUNT; i++ ){
			if ( inReg.ToLowerInvariant() == ((GPR)i).ToString().ToLowerInvariant() ){				
				return Command_SetReg( (GPR)i, inValue );
			}
		}

		Console.WriteLine( "Unknown register: " + inReg );
		return false;

	}

	/// <summary>
	///  As above but typed
	/// </summary>	
	public static bool Command_SetReg( GPR inReg, UInt32 inValue ){
		
		Console.WriteLine( "---- Getting a copy of current registers ----" );

		if ( !GDB.GetRegs() ){
			Console.WriteLine( "Couldn't get regs" );
			return false;
		}

		GDB.tcb.regs[ (int)inReg ] = inValue;

		Console.WriteLine( "---- Done, writing regs back ----" );
		
		return GDB.SetRegs();
		
	}

	// Ping? Pong!
	public static void WriteChallenge(string inChallenge){

		activeSerial.Write(inChallenge);

	}

	private static bool didShowUpgradewarning = false;

	/// <summary>
	/// Wait for a response to see if this version of
	/// Unirom supports the V2 protocol
	/// </summary>	
	public static bool WaitResponse(string inResponse, bool verbose = true) {

		Program.protocolVersion = 1;

		// Dump the response into a buffer..
		// (byte by byte so we can compare the challenge/response)
		// e.g. it may start spewing data immediately after and we
		// have to catch that.
		// note: the attribute extensions use 40ish bytes of memory per pop

		string responseBuffer = "";

		if (verbose)
			Console.WriteLine("Waiting for response or protocol negotiation: ");
		
		while (true)
		{
			
			if (activeSerial.BytesToRead != 0)
			{
								
				responseBuffer += (char)activeSerial.ReadByte();

				// filter any noise at the start of the response
				// seems to happen once in a while
				if (responseBuffer.Length > 4)
					responseBuffer = responseBuffer.Remove(0, 1);

				if (verbose)
					Console.Write("\r InputBuffer: " + responseBuffer);

				// command unsupported in debug mode
				if (responseBuffer == "UNSP")
				{
					Console.WriteLine( "\nNot supported while Unirom is in debug mode!" );
					return false;
				}

				if ( responseBuffer == "HECK" ){
					Console.WriteLine("\nCouldn't read the memory card!");
					return false;
				}

				if (responseBuffer == "ONLY")
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine( "\nOnly supported while Unirom is in debug mode!" );
					return false;
				}

				if (
					!didShowUpgradewarning
					&& responseBuffer.Length >= 4
					&& responseBuffer.Substring(0, 3) == "OKV"
					&& (byte)responseBuffer[3] > (byte)'2'
				)
				{
					didShowUpgradewarning = true;
					Console.WriteLine();
					Console.Write("================================================================================\n");
					Console.Write("   Just a heads up!\n");
					Console.Write("   This version of Unirom appears to be much newer than your version of NoPS.\n");
					Console.Write("   Time for an upgrade? github.com/JonathanDotCel/ \n");
					Console.Write("================================================================================\n");
				}

				// upgrade to V2 with individual checksum
				if (responseBuffer == "OKV2" && Program.protocolVersion == 1)
				{
					Console.WriteLine("\nUpgraded to protocol V2!");
					activeSerial.Write("UPV2");
					Program.protocolVersion = 2;
				}

				// now whether we've upgraded protocol or not:
				if (responseBuffer == inResponse)
				{
					if (verbose)
						Console.WriteLine("\nGot response: " + responseBuffer );
					break;
				}

			} // while(1)




		}

		return true;

	}


	/// <summary>
	/// Deceptively small function, but one of the most important
	/// This is the one that sends e.g. "/poke" and  checks that Unirom is paying attention
	/// </summary>	
	public static bool ChallengeResponse( CommandMode inMode ){
		return ChallengeResponse( inMode.challenge(), inMode.response() );
	}

	public static bool ChallengeResponse( string inChallenge, string expectedResponse ){
		
		// Now send the challenge code and wait
		Console.WriteLine("Waiting for the PS1, C/R={0}/{1}....\n\n", inChallenge, expectedResponse );

		WriteChallenge(inChallenge);

		Thread.Sleep(50);

		return WaitResponse(expectedResponse);

	}



	// HEY!
	// Remember to tell the PSX to expect bytes first... BIN, ROM, EXE, etc
	// as this will attempt to use the V2 protocol rather than just spamming 
	// bytes into the void
	public static bool WriteBytes( byte[] inBytes, bool skipFirstSector, bool forceProtocolV2 = false ){


		// .exe files go [ header ][ meta ][ data @ write address ]
		// .rom files go [ meta ][ data @ 0x80100000 ]
		// .bin files go [ size ][ data @ 0xWRITEADR ]

		int start = skipFirstSector ? 2048 : 0;       // for .exes

		int chunkSize = 2048;                               // 2048 seems the most stable
		int numChunks = inBytes.Length / chunkSize + (inBytes.Length % chunkSize == 0 ? 0 : 1);

		int waityCakes = 0;                                 // Kinda extraneous, but it's interesting to watch


		// we already sent the first one?
		for (int i = start; i < inBytes.Length; i += chunkSize)
		{

		retryThisChunk:

			ulong chunkChecksum = 0;

			// Are we about to go out of range?
			// .NET doesn't care if you specify 2kb when you're only e.g. 1.7kb from the boundary
			// but it's best to declare explicityly			
			if ( i + chunkSize >= inBytes.Length )
				chunkSize = inBytes.Length - i;

			// write 1 chunk worth of bytes
			activeSerial.Write(inBytes, i, chunkSize);
			//Console.WriteLine( " " + i + " of " + inBytes.Length + " " + skipFirstSector );

			// update the expected checksum value
			for (int j = 0; j < chunkSize; j++)
			{
				chunkChecksum += inBytes[i + j];
			}

			while (activeSerial.BytesToWrite != 0)
			{
				waityCakes++;
			}

			Console.ForegroundColor = ConsoleColor.Green;
			int percent = (i + 1) * 100 / (inBytes.Length);
			Console.Write("\r Sending chunk {0} of {1} ({2})%", (i / chunkSize) + 1, numChunks, percent);

			SetDefaultColour();


			if ( Program.protocolVersion == 2 || forceProtocolV2 )
			{

				// Format change as of 8.0.C
				// every 2k, we'll send back a "MORE" from Unirom

				Console.Write(" ... ");

				string cmdBuffer = "";

				TimeSpan startSpan = GetSpan();
				while (cmdBuffer != "CHEK")
				{

					if (activeSerial.BytesToRead != 0)
					{
						
						cmdBuffer += (char)activeSerial.ReadByte();

					}
					while (cmdBuffer.Length > 4)
						cmdBuffer.Remove(0, 1);

				}

				// did it ask for a checksum?
				if (cmdBuffer == "CHEK")
				{

					Console.Write("Sending checksum...");

					activeSerial.Write(BitConverter.GetBytes(chunkChecksum), 0, 4);
					Thread.Sleep(1);

					startSpan = GetSpan();

					while (cmdBuffer != "MORE" && cmdBuffer != "ERR!")
					{

						if (activeSerial.BytesToRead != 0)
						{
							char readVal = (char)activeSerial.ReadByte();
							cmdBuffer += readVal;
							Console.Write(readVal);
						}
						while (cmdBuffer.Length > 4)
						{
							cmdBuffer = cmdBuffer.Remove(0, 1);
						}

					}

					if (cmdBuffer == "ERR!")
					{
						Console.WriteLine("... Retrying\n");
						goto retryThisChunk;
					}

					if (cmdBuffer == "MORE")
					{
						//Console.Write( "... OK\n" );
					}

				}

				// if it didn't ask for one, crack on.


			} // corrective transfer

			Console.Write(" DONE\n");

		}

		// might have to terminate previous line
		Console.WriteLine("\nSend finished!\n");

		return true;

	} // WriteBytes



	// C people: remember the byte[] is a pointer....
	/// <summary>
	/// Reads an array of bytes from the serial connection
	/// </summary>		
	public static bool ReadBytes(UInt32 inAddr, UInt32 inSize, byte[] inBytes )
	{

		if ( !ChallengeResponse( CommandMode.DUMP ) ) {
			return false;
		}

		// the handshake is done, let's tell it where to start
		activeSerial.Write( BitConverter.GetBytes( inAddr ), 0, 4 );
		activeSerial.Write( BitConverter.GetBytes( inSize ), 0, 4 );

		return ReadBytes_Raw( inSize, inBytes );

	} // DUMP

	public static bool ReadBytes_Raw( UInt32 inSize, byte[] inBytes ){


		// now go!
		int arrayPos = 0;
		//lastReadBytes = new byte[inSize];

		// Let the loop time out if something gets a bit fucky.			
		TimeSpan lastSpan = GetSpan();
		TimeSpan currentSpan = GetSpan();

		UInt32 checkSum = 0;

		while ( true ) {

			currentSpan = GetSpan();

			if ( activeSerial.BytesToRead != 0 ) {

				lastSpan = GetSpan();

				byte responseByte = (byte)activeSerial.ReadByte();
				inBytes[ arrayPos ] = (responseByte);

				arrayPos++;

				checkSum += (UInt32)responseByte;

				if ( arrayPos % 2048 == 0 ) {
					activeSerial.Write( "MORE" );
				}

				if ( arrayPos % 1024 == 0 ) {
					long percent = (arrayPos * 100) / inSize;
					Console.Write( "\r Offset {0} of {1} ({2})%\n", arrayPos, inSize, percent );
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

		Console.WriteLine( "Read Complete!" );

		// Read 4 more bytes for the checksum

		// Let the loop time out if something gets a bit fucky.			
		lastSpan = GetSpan();
		int expectedChecksum = 0;

		SetDefaultColour();
		Console.WriteLine( "Checksumming the checksums for checksummyness.\n" );

		try {

			for ( int i = 0; i < 4; i++ ) {

				while ( activeSerial.BytesToRead == 0 ) {

					currentSpan = GetSpan();

					if ( (currentSpan - lastSpan).TotalMilliseconds > 2000 ) {
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine( "Error reading checksum byte " + i + " of 4!" );
						break;
					}

				}

				lastSpan = GetSpan();

				byte inByte = (byte)activeSerial.ReadByte();

				// and shift it ino the expected checksum
				expectedChecksum |= (inByte << (i * 8));

			}

		} catch ( System.TimeoutException ) {

			Console.ForegroundColor = ConsoleColor.Red;
			Error( "No checksum sent, continuing anyway!\n ", false );

		}

		if ( expectedChecksum != checkSum ) {
			Console.ForegroundColor = ConsoleColor.Red;
			Error( "Checksum missmatch! Expected: " + expectedChecksum.ToString( "X8" ) + "    Calced: %x\n" + checkSum.ToString( "X8" ), false );
			Error( " WILL ATTEMPT TO CONTINUE\n", false );
			return false;
		} else {
			SetDefaultColour();
			Console.WriteLine( " Checksums match: " + expectedChecksum.ToString( "X8" ) + "\n" );
		}


		if ( activeSerial.BytesToRead > 0 ) {
			Console.ForegroundColor = ConsoleColor.Red;
			Error( "Extra bytes still being sent from the PSX! - Will attempt to save file anyway!", false );
		}

		SetDefaultColour();

		return true;

	}


#pragma warning disable CS0162

	/// <summary>
	/// Semi-supported: 
	/// Constantly reads from the address specified and dumps it to screen
	/// </summary>	
	public static bool Watch( UInt32 inAddr, UInt32 inSize ){

		if ( !ChallengeResponse( CommandMode.WATCH ) ) 
			return false;

		int bytesRead = 0;
		int arrayPos = 0;
		byte[] lastReadBytes = new byte[inSize];

		// the handshake is done, let's tell it where to start
		arrayPos = 0;
		activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);
		activeSerial.Write(BitConverter.GetBytes(inSize), 0, 4);

		while (true)
		{

			// Keep reading bytes until we've got as many back as we've asked for

			if (activeSerial.BytesToRead != 0)
			{

				// still bothers me that it reads an int...
				byte responseByte = (byte)activeSerial.ReadByte();
				lastReadBytes[arrayPos] = (responseByte);

				bytesRead++;
				arrayPos++;

				// filled the buffer? Print it

				if (arrayPos >= lastReadBytes.Length)
				{

					Console.Clear();
					Console.Write("Watching address range 0x" + inAddr.ToString("X8") + " to 0x" + (inAddr + inSize).ToString("X8") + "\n");
					Console.Write("Bytes read " + bytesRead + "\n\n");

					for (int i = 0; i < lastReadBytes.Length; i++)
					{

						Console.Write(lastReadBytes[i].ToString("X2") + " ");

						// Such a janky way to do it, but is saves appending
						// tons and tons of strings together
						if (i % 16 == 15)
						{

							// print the actual char values

							for (int j = i - 15; j <= i; j++)
							{

								Console.Write(" " + (char)lastReadBytes[j]);

							}

							// then draw the character data								
							Console.Write("\n");

						}

					}

					if (activeSerial.BytesToRead != 0)
					{
						Console.Write("\nTerminator bytes: ");
						while (activeSerial.BytesToRead != 0)
						{
							int x = activeSerial.ReadByte();
							Console.Write(x.ToString("X2") + " ");
						}
						Console.Write("\n");
					}


					// slow it down a touch

					// give the PSX time to do stuff
					Thread.Sleep(200);

					// Just start over...					
					ChallengeResponse(CommandMode.WATCH.challenge(), CommandMode.WATCH.response());

					// start over
					arrayPos = 0;
					activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);
					activeSerial.Write(BitConverter.GetBytes(inSize), 0, 4);

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
	public static bool Command_WipeMem( UInt32 wipeAddress, UInt32 wipeValue ){

		// if it returns true, we might enter /m (monitor) mode, etc
		if (
			!TransferLogic.ChallengeResponse( CommandMode.DEBUG )
		) {
			Console.WriteLine( "Couldn't determine if Unirom is in debug mode." );
			return false;
		}

		Thread.Sleep( 200 );

		byte[] buffer = new byte[ 0x80200000 - wipeAddress ]; // just shy of 2MB

		for( int i = 0; i < buffer.Length / 4 ; i++ ){
			BitConverter.GetBytes( wipeValue ).CopyTo( buffer, i *4 );
		}

		Command_SendBin( wipeAddress, buffer );

		// It won't return.
		return true;

	}

	/// <summary>
	/// Returns a (weak) checksum for the given bytes
	/// </summary>	
	/// <param name="skipFirstSector">Skip the first 0x800 header sector on an .exe as it won't be sent over SIO</param>	
	public static UInt32 CalculateChecksum(byte[] inBytes, bool skipFirstSector = false)
	{

		UInt32 returnVal = 0;
		for (int i = (skipFirstSector ? 2048 : 0); i < inBytes.Length; i++)
		{
			returnVal += (UInt32)inBytes[i];
		}
		return returnVal;

	}

}
